using System.ComponentModel;
using System.Runtime.InteropServices;
using LanShare.Core;

namespace LanShare.App;

// 仅在本机开启键鼠共享后安装桌面钩子。回调只进行状态判断和入队，不等待套接字。
internal sealed class WindowsInput : IInputDesktop, IDisposable
{
    private readonly HookProc mouseProc, keyProc;
    private nint mouseHook, keyHook;
    private readonly HashSet<int> suppressedKeys = [], suppressedButtons = [];
    private readonly HashSet<InputPacket> pendingReleases = [];
    public bool ForegroundHasHigherIntegrity => InputIntegrity.ForegroundIsHigher();
    public bool HasPendingReleases => pendingReleases.Count != 0;
    private Rectangle bounds;
    private Rectangle[] monitors = [];
    private readonly PointerMotion motion = new();
    private readonly SenderCursorWindow senderCursor = new();
    private SynchronizationContext? ui;
    private bool centering;
    // 标记本进程重放的事件，从钩子回调中识别自己的输入。
    internal const uint ReplayTag = 0x534C5348;
    private LocalDeviceActivity? localActivity;
    public SharedInput? Router { get; set; }
    public Keys ReturnKey { get; set; } = Keys.Home;
    public int Width => bounds.Width;
    public int Height => bounds.Height;

    // 仅接受当前用户普通交互桌面，锁屏、UAC 和其他输入桌面不能作为键鼠目标。
    public static bool OnUserDesktop()
    {
        nint desktop = OpenInputDesktop(0, false, 1);
        if (desktop == 0) return false;
        try
        {
            var name = new System.Text.StringBuilder(128);
            return GetUserObjectInformation(desktop, 2, name, name.Capacity * sizeof(char), out _) &&
                string.Equals(name.ToString(), "Default", StringComparison.OrdinalIgnoreCase);
        }
        finally { CloseDesktop(desktop); }
    }
    public (int X, int Y) Anchor
    {
        get
        {
            Rectangle screen = Screen.FromPoint(Cursor.Position).Bounds;
            return (screen.Left + screen.Width / 2 - bounds.Left, screen.Top + screen.Height / 2 - bounds.Top);
        }
    }
    public bool PhysicalInputHeld
    {
        get
        {
            for (int vk = 1; vk < 255; vk++) if (GetAsyncKeyState(vk) < 0) return true;
            return false;
        }
    }

    // 委托必须与原生钩子拥有相同生命周期。
    public WindowsInput() { mouseProc = Mouse; keyProc = Keyboard; }

    // 建立钩子前读取本次桌面坐标，部分初始化失败时释放已建立的钩子。
    public void Start()
    {
        bounds = SystemInformation.VirtualScreen;
        monitors = Screen.AllScreens.Select(s => s.Bounds).ToArray();
        if (mouseHook != 0) return;
        ui = SynchronizationContext.Current;
        motion.Observe(Cursor.Position);
        nint module = GetModuleHandle(null);
        try
        {
            mouseHook = SetWindowsHookEx(14, mouseProc, module, 0);
            if (mouseHook == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            keyHook = SetWindowsHookEx(13, keyProc, module, 0);
            if (keyHook == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            localActivity = new LocalDeviceActivity(() => Router?.Mode == ControlMode.Receiving,
                cause => Router?.ReturnLocal(cause == InputEnd.LocalKeyboard ? "本机实体键盘已接管" : "本机实体鼠标已接管", cause));
        }
        catch { Dispose(); throw; }
    }

    // 物理键盘有优先权。已在对端处理的按键在收回控制后也要吞掉配对的抬起事件。
    private nint Keyboard(int code, nint message, nint data)
    {
        if (code >= 0 && Router is {} router)
        {
            var key = Marshal.PtrToStructure<KeyHookData>(data);
            bool injected = (key.Flags & 0x10) != 0;
            if (key.Extra != ReplayTag && (!injected || router.Mode == ControlMode.Sending))
            {
                bool up = (key.Flags & 0x80) != 0;
                if (!injected && !up && router.Mode != ControlMode.Local && key.Vk == (int)ReturnKey && CtrlAltHeld())
                {
                    suppressedKeys.Add(key.Vk);
                    router.ReturnLocal("快捷键返回本机");
                    return 1;
                }
                // 被控端依据实体设备报告接管，让输入法和系统派生的键盘通知通过。
                if (router.Mode == ControlMode.Receiving && localActivity?.Registered == true)
                    return CallNextHookEx(0, code, message, data);
                if (router.Mode == ControlMode.Local && suppressedKeys.Contains(key.Vk))
                {
                    if (up) suppressedKeys.Remove(key.Vk);
                    return 1;
                }
                if (!TryMakeKey(key.Vk, key.Scan, key.Flags, out var packet))
                    return CallNextHookEx(0, code, message, data);
                if (router.PhysicalEvent(packet))
                {
                    if (up) suppressedKeys.Remove(key.Vk); else suppressedKeys.Add(key.Vk);
                    return 1;
                }
            }
        }
        return CallNextHookEx(0, code, message, data);
    }

    // 驱动可能提供带 E0/E1 前缀的扫描码，其他未知编码回退到虚拟键。
    internal static bool TryMakeKey(int vk, int scan, int flags, out InputPacket packet)
    {
        packet = default;
        if (vk is <= 0 or >= 255 or 0xE7) return false;
        bool extended = (flags & 1) != 0 || (scan & 0xFFFFFF00L) == 0xE000;
        int code = scan is >= 0 and <= 255 ? scan : (scan & 0xFFFFFF00L) == 0xE000 ? scan & 255 : 0;
        packet = new(InputOp.Key, default, vk, code, ((flags & 0x80) != 0 ? 1 : 0) | (extended ? 2 : 0));
        return true;
    }

    // 按会话切换本机设备监听，发送端只保留原有的钩子。
    public void SetReceiving(bool receiving) => localActivity?.SetReceiving(receiving);

    // 系统继续提交移动，前后事件仍使用同一坐标系；仅控制端隐藏鼠标。
    public bool SetSending(bool sending)
    {
        if (sending) return senderCursor.TryShow(bounds);
        senderCursor.Dispose(); return true;
    }

    // 被转发的修饰键没有进入本地异步键状态，需要同时检查已拦截的按下记录。
    private bool CtrlAltHeld() =>
        (suppressedKeys.Contains(0xA2) || suppressedKeys.Contains(0xA3) || GetAsyncKeyState(0x11) < 0) &&
        (suppressedKeys.Contains(0xA4) || suppressedKeys.Contains(0xA5) || GetAsyncKeyState(0x12) < 0);

    // 主控时也接受同级或更高权限驱动注入的滚轮，过滤低权限注入和本程序回放。
    internal static bool CanRouteWheel(ControlMode mode, int flags, nuint extra) =>
        extra != ReplayTag && (flags & 2) == 0 && ((flags & 1) == 0 || mode == ControlMode.Sending);

    // 区分物理移动、五种鼠标按钮及两个滚轮方向；系统注入的移动不会再被转发。
    private nint Mouse(int code, nint message, nint pointer)
    {
        if (code >= 0 && Router is {} router)
        {
            var data = Marshal.PtrToStructure<MouseHookData>(pointer);
            bool simulated = data.Extra == ReplayTag || (data.Flags & 1) != 0;
            if ((int)message == 0x200)
            {
                var point = new Point(data.X, data.Y);
                var delta = motion.Observe(point, simulated);
                if (router.Mode == ControlMode.Receiving && !simulated && !delta.IsEmpty)
                    localActivity?.ObserveLocalMove(data.Time);
                // 注入的回中、返回和接收端定位也更新基线，但不转发为物理移动。
                if (simulated || (router.Mode == ControlMode.Receiving && (delta.IsEmpty || localActivity?.Registered == true)))
                    return CallNextHookEx(0, code, message, pointer);
                if (router.Mode == ControlMode.Sending)
                {
                    router.RemoteMotion(delta.X, delta.Y);
                    if (router.Mode != ControlMode.Sending) return 1;
                    QueueCenterNearEdge(point);
                    // 让系统提交物理位移，下一事件与上一次实际位置使用同一坐标系。
                    return CallNextHookEx(0, code, message, pointer);
                }
                if (router.Pointer(point.X - bounds.Left, point.Y - bounds.Top)) return 1;
            }
            int msg = (int)message;
            if (!simulated && router.Mode == ControlMode.Receiving && localActivity?.Registered == true)
            {
                if (msg is >= 0x201 and <= 0x20E) localActivity.ObserveLocalMove(data.Time);
                return CallNextHookEx(0, code, message, pointer);
            }
            if (msg is 0x20A or 0x20E)
            {
                if (CanRouteWheel(router.Mode, data.Flags, data.Extra) &&
                    router.PhysicalEvent(new(InputOp.Wheel, default, data.Data >> 16, msg == 0x20E ? 1 : 0))) return 1;
            }
            else if (!simulated)
            {
                int button = msg switch
                {
                    0x201 or 0x202 => 1, 0x204 or 0x205 => 2, 0x207 or 0x208 => 3,
                    0x20B or 0x20C => 3 + (data.Data >> 16), _ => 0
                };
                if (button is >= 1 and <= 5)
                {
                    bool down = msg is 0x201 or 0x204 or 0x207 or 0x20B;
                    if (!down && router.Mode == ControlMode.Local && suppressedButtons.Remove(button)) return 1;
                    if (router.PhysicalEvent(new(InputOp.Button, default, button, down ? 1 : 0)))
                    {
                        if (down) suppressedButtons.Add(button); else suppressedButtons.Remove(button);
                        return 1;
                    }
                }
            }
        }
        return CallNextHookEx(0, code, message, pointer);
    }

    // 仅靠近屏幕边缘时在钩子返回后回中，给后续物理移动留出空间。
    private void QueueCenterNearEdge(Point point)
    {
        if (centering || ui == null || monitors.Any(r => Rectangle.Inflate(r, -16, -16).Contains(point))) return;
        centering = true;
        ui.Post(_ =>
        {
            centering = false;
            if (Router?.Mode != ControlMode.Sending) return;
            var (x, y) = Anchor;
            if (!Move(x, y)) Router.ReturnLocal("无法定位本机光标");
        }, null);
    }

    // 将虚拟桌面空隙内的点投影到最近显示器，保证点击落在实际可见像素上。
    internal static Point VisiblePoint(Point point, Rectangle[] displays)
    {
        Point nearest = point;
        long best = long.MaxValue;
        foreach (var display in displays)
        {
            if (display.Contains(point)) return point;
            int x = Math.Clamp(point.X, display.Left, display.Right - 1);
            int y = Math.Clamp(point.Y, display.Top, display.Bottom - 1);
            long dx = (long)x - point.X, dy = (long)y - point.Y;
            long distance = dx * dx + dy * dy;
            if (distance < best) { best = distance; nearest = new Point(x, y); }
        }
        return nearest;
    }

    // 使用各显示器的实际像素和完整虚拟桌面原点进行绝对定位。
    public bool Move(int x, int y)
    {
        var p = VisiblePoint(new Point(bounds.Left + x, bounds.Top + y), monitors);
        int nx = Normalize(p.X - bounds.Left, Width), ny = Normalize(p.Y - bounds.Top, Height);
        return SendMouse(0xC001, 0, nx, ny);
    }

    // 映射到像素中心，避免不同 DPI 下的端点舍入误差。
    private static int Normalize(int value, int size) =>
        (int)((Math.Clamp(value, 0, size - 1) * 2L + 1) * 32768 / size);

    // 优先按扫描码在目标电脑的键盘布局和输入法中重放物理键。
    public bool Inject(InputPacket packet)
    {
        if (packet.Op == InputOp.Key)
        {
            bool useScan = packet.B != 0 && packet.A != (int)Keys.Pause;
            var input = new NativeInput { Type = 1 };
            input.Payload.Key.Vk = useScan ? (ushort)0 : (ushort)packet.A;
            input.Payload.Key.Scan = (ushort)packet.B;
            input.Payload.Key.Extra = ReplayTag;
            input.Payload.Key.Flags = (useScan ? 8u : 0u) | ((packet.C & 1) != 0 ? 2u : 0u) | ((packet.C & 2) != 0 ? 1u : 0u);
            return SendInput(1, ref input, Marshal.SizeOf<NativeInput>()) == 1;
        }
        if (packet.Op == InputOp.Wheel) return SendMouse(packet.B == 0 ? 0x800u : 0x1000u, packet.A);
        uint flag = packet.A switch { 1 => 2u, 2 => 8u, 3 => 32u, _ => 128u };
        return SendMouse(packet.B == 1 ? flag : flag << 1, packet.A > 3 ? packet.A - 3 : 0);
    }

    // 锁屏或权限切换可暂时阻止释放，保存未被系统接受的抬起事件。
    public void Release(InputPacket packet)
    {
        if (ForegroundHasHigherIntegrity || !Inject(packet)) pendingReleases.Add(packet);
    }

    // 只补发会话清理时的抬起，不重放按下、文字或鼠标移动。
    public void RetryReleases() => pendingReleases.RemoveWhere(Inject);

    // 检查 Windows 实际接受的事件数，拒绝注入时由控制器及时返回本机。
    private static bool SendMouse(uint flags, int data, int x = 0, int y = 0)
    {
        var input = new NativeInput();
        input.Payload.Mouse = new MouseInput { X = x, Y = y, Flags = flags, Data = data, Extra = ReplayTag };
        return SendInput(1, ref input, Marshal.SizeOf<NativeInput>()) == 1;
    }

    // 关闭或锁屏后解除钩子并清空短期按下记录。
    public void Dispose()
    {
        senderCursor.Dispose();
        localActivity?.Dispose(); localActivity = null;
        if (keyHook != 0) UnhookWindowsHookEx(keyHook);
        if (mouseHook != 0) UnhookWindowsHookEx(mouseHook);
        keyHook = mouseHook = 0;
        suppressedKeys.Clear(); suppressedButtons.Clear();
    }

    // 下列结构与 Windows SDK 中的低级钩子和 INPUT 联合体布局一致。
    private delegate nint HookProc(int code, nint message, nint data);
    [StructLayout(LayoutKind.Sequential)] private struct MouseHookData { public int X, Y, Data, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyHookData { public int Vk, Scan, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y, Data; public uint Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyInput { public ushort Vk, Scan; public uint Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Explicit)] private struct Payload { [FieldOffset(0)] public MouseInput Mouse; [FieldOffset(0)] public KeyInput Key; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeInput { public uint Type; public Payload Payload; }

    // 获取当前进程模块供 Windows 安装低级钩子。
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
    // 在当前交互桌面安装低级键盘或鼠标回调。
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint SetWindowsHookEx(int kind, HookProc callback, nint module, uint thread);
    // 卸载指定钩子。
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    // 将未转发的输入交还给系统。
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    // 查询物理输入是否正在按下，避免拖拽中跨屏。
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);
    // 向当前用户桌面输入队列提交一个事件。
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, ref NativeInput input, int size);
    // 以只读权限打开当前输入桌面。
    [DllImport("user32.dll")] private static extern nint OpenInputDesktop(uint flags, bool inherit, uint access);
    // 查询桌面名称，识别普通桌面与系统保留桌面。
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetUserObjectInformation(nint handle, int index, System.Text.StringBuilder name, int bytes, out int needed);
    // 释放临时桌面句柄。
    [DllImport("user32.dll")] private static extern bool CloseDesktop(nint desktop);
}
