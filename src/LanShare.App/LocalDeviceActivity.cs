using System.Runtime.InteropServices;
using LanShare.Core;

namespace LanShare.App;

// 被控期间仅根据本机设备报告判断是否抢回，窗口切换和输入法产生的通知不会抢回。
internal sealed class LocalDeviceActivity : NativeWindow, IDisposable
{
    private readonly Func<bool> receiving;
    private readonly Action<InputEnd> takeover;
    private Point? lastAbsolute;
    private nint lastDevice;
    private uint? touchMoveTime;
    internal bool Registered { get; private set; }

    // 监听设备活动；注册失败时调用方继续使用已有钩子和紧急返回快捷键。
    internal LocalDeviceActivity(Func<bool> receiving, Action<InputEnd> takeover)
    {
        this.receiving = receiving; this.takeover = takeover;
        CreateHandle(new CreateParams { Parent = new IntPtr(-3), Caption = "SimpleLanShare.LocalActivity" });
    }

    // 进入被控会话才订阅，返回本机后立即注销并丢弃上一轮的移动基线。
    internal void SetReceiving(bool enabled)
    {
        if (enabled == Registered) return;
        lastAbsolute = null; touchMoveTime = null;
        if (enabled) Registered = Register(0x100, Handle);
        else if (Register(1, 0)) Registered = false;
    }

    // 只订阅鼠标与键盘，在被控期间读取活动，不保存输入的键值。
    private static bool Register(uint flags, nint target)
    {
        Device[] devices = [new() { Page = 1, Usage = 2, Flags = flags, Target = target },
                            new() { Page = 1, Usage = 6, Flags = flags, Target = target }];
        return RegisterRawInputDevices(devices, 2, (uint)Marshal.SizeOf<Device>());
    }

    // 精密触控板可能不提供设备句柄，需同时收到未注入的移动与对应的原始报告。
    internal void ObserveLocalMove(int time) => touchMoveTime = unchecked((uint)time);

    // 绝对定位设备首次报告仅作为基线，重复坐标不会触发抢回。
    protected override void WndProc(ref System.Windows.Forms.Message message)
    {
        if (message.Msg == 0x00FF && receiving())
        {
            uint size = (uint)Marshal.SizeOf<Report>();
            uint read = GetRawInputData(message.LParam, 0x10000003, out var report, ref size, (uint)Marshal.SizeOf<Header>());
            if (read != uint.MaxValue && read >= Marshal.SizeOf<Header>() + 16)
            {
                if (report.Header.Type == 1 && IsDeviceKeyDown(report.Header.Device, report.Body.KeyFlags, report.Body.VirtualKey, report.Body.KeyExtra))
                    takeover(InputEnd.LocalKeyboard);
                else if (report.Header.Type == 0 && report.Body.MouseExtra != WindowsInput.ReplayTag && read >= Marshal.SizeOf<Report>() &&
                    (report.Header.Device != 0 || (touchMoveTime is uint time && unchecked((uint)GetMessageTime() - time) <= 20)))
                {
                    var point = new Point(report.Body.X, report.Body.Y);
                    bool absolute = (report.Body.MouseFlags & 1) != 0;
                    bool moved = absolute ? lastDevice == report.Header.Device && lastAbsolute is {} old && old != point : !point.IsEmpty;
                    lastDevice = report.Header.Device; lastAbsolute = absolute ? point : null;
                    touchMoveTime = null;
                    if (moved || report.Body.ButtonFlags != 0) takeover(InputEnd.LocalMouse);
                }
            }
        }
        else if (message.Msg == 0x00FF) { lastAbsolute = null; touchMoveTime = null; }
        base.WndProc(ref message);
    }

    // 仅真实来源的首次或重复按下表示本机接管；回放、无设备通知和抬起不参与判断。
    internal static bool IsDeviceKeyDown(nint device, ushort flags, ushort vk, uint extra) =>
        device != 0 && extra != WindowsInput.ReplayTag && (flags & 1) == 0 && vk is > 0 and < 255;

    // 关闭监听时撤销原始输入订阅并释放消息窗口。
    public void Dispose()
    {
        if (Registered) Register(1, 0);
        DestroyHandle();
    }

    [StructLayout(LayoutKind.Sequential)] private struct Device { public ushort Page, Usage; public uint Flags; public nint Target; }
    [StructLayout(LayoutKind.Sequential)] private struct Header { public uint Type, Size; public nint Device, Param; }
    [StructLayout(LayoutKind.Explicit, Size = 24)] private struct Body
    {
        [FieldOffset(0)] public ushort MouseFlags;
        [FieldOffset(4)] public ushort ButtonFlags;
        [FieldOffset(12)] public int X;
        [FieldOffset(16)] public int Y;
        [FieldOffset(2)] public ushort KeyFlags;
        [FieldOffset(6)] public ushort VirtualKey;
        [FieldOffset(12)] public uint KeyExtra;
        [FieldOffset(20)] public uint MouseExtra;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Report { public Header Header; public Body Body; }

    // 订阅或注销本机 HID 设备活动。
    [DllImport("user32.dll")] private static extern bool RegisterRawInputDevices(Device[] devices, uint count, uint size);
    // 从 WM_INPUT 句柄读取当前这一份设备报告。
    [DllImport("user32.dll")] private static extern uint GetRawInputData(nint handle, uint command, out Report data, ref uint size, uint headerSize);
    // 使用消息投递时刻比对触控板事件，避免受界面线程调度延迟影响。
    [DllImport("user32.dll")] private static extern int GetMessageTime();
}
