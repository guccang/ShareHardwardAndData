namespace LanShare.Core;

// 桌面适配器使用当前虚拟桌面的相对像素；测试可替换为内存桌面。
public interface IInputDesktop
{
    int Width { get; }
    int Height { get; }
    (int X, int Y) Anchor { get; }
    bool PhysicalInputHeld { get; }
    // 前台窗口的完整性高于接收进程时，普通桌面输入无法可靠送达。
    bool ForegroundHasHigherIntegrity => false;
    bool Move(int x, int y);
    bool Inject(InputPacket packet);
    // 只在被控期间需要确认本机实体设备活动，发送端避免重复订阅鼠标事件。
    void SetReceiving(bool receiving) { }
    // 发送端在进入远端控制时隐藏本机指针，返回时恢复；等待确认期间保持可见。
    bool SetSending(bool sending) => true;
    // 系统暂时拒绝输入时，原生适配器可在普通桌面恢复后补送释放事件。
    void Release(InputPacket packet) => Inject(packet);
}

public enum ControlMode { Local, Waiting, Sending, Receiving }

// End 帧只传递固定的退出原因，旧版忽略该字段，不包含按键或窗口内容。
public enum InputEnd { Normal, LocalKeyboard, LocalMouse, Paused, KeyboardRejected, PointerRejected, Timeout, HigherIntegrity }

// 所有状态变更在桌面消息线程上串行执行。两个方向使用相同的授权、心跳及释放规则。
public sealed class SharedInput(string localId, IInputDesktop desktop, Func<string, int, string?> neighbor,
    Func<string, bool> authorized, Func<string, InputPacket, bool> send, Func<bool> selecting, Func<long>? milliseconds = null)
{
    private const string LocalPrivilegeReason = "前台窗口权限高于局域共享。操作此窗口需要在本机以管理员身份运行局域共享。";
    private readonly Func<long> now = milliseconds ?? (() => Environment.TickCount64);
    private readonly HashSet<InputPacket> held = [];
    private Guid lease;
    private int direction, remoteWidth, remoteHeight, x, y, fraction, homeX, homeY;
    private int? edge;
    private long contact, nextPulse, cooldown;
    private bool remoteSelecting, leaveEdge;
    private bool keyboardBlocked;
    public ControlMode Mode { get; private set; }
    public string? Peer { get; private set; }
    public bool Enabled { get; private set; }
    public event Action<string>? Changed;
    public event Action<string>? Notice;

    // 关闭功能、锁屏、撤销授权和链路关闭都可同步恢复本机。
    public void Enable(bool enabled)
    {
        Enabled = enabled;
        if (!enabled) ReturnLocal("键鼠共享已暂停", InputEnd.Paused);
    }

    // 任意异常退出都先撤销会话编号，再释放远端输入，避免晚到数据重新生效。
    public void ReturnLocal(string reason, InputEnd cause = InputEnd.Normal)
    {
        var previous = Mode;
        string? peer = Peer;
        Guid previousLease = lease;
        Mode = ControlMode.Local;
        Peer = null;
        lease = Guid.Empty;
        keyboardBlocked = false;
        edge = null;
        cooldown = now() + 450;
        if (previous == ControlMode.Local) return;
        leaveEdge = true;
        if (previous == ControlMode.Receiving)
        {
            desktop.SetReceiving(false);
            foreach (var down in held)
                desktop.Release(Up(down));
        }
        held.Clear();
        if (previous == ControlMode.Sending)
        {
            desktop.SetSending(false);
            desktop.Move(homeX, homeY);
        }
        if (peer is not null) send(peer, new(InputOp.End, previousLease, (int)cause));
        Changed?.Invoke(reason);
    }

    // 将按下记录转换为相应的抬起；只操作本次会话注入过的按键和鼠标按钮。
    private static InputPacket Up(InputPacket down) => down.Op == InputOp.Key
        ? down with { C = down.C | 1 } : down with { B = 0 };

    // 接收端必须先验证指纹授权、空闲桌面和会话号，再执行来自网络的输入。
    public void Receive(string peer, InputPacket packet)
    {
        if (!packet.Valid) return;
        if (packet.Op == InputOp.Request)
        {
            if (!Enabled || Mode != ControlMode.Local || !authorized(peer) || selecting() || desktop.PhysicalInputHeld)
            {
                send(peer, new(InputOp.End, packet.Lease));
                return;
            }
            if (desktop.ForegroundHasHigherIntegrity)
            {
                send(peer, new(InputOp.End, packet.Lease, (int)InputEnd.HigherIntegrity));
                Notice?.Invoke(LocalPrivilegeReason);
                return;
            }
            Peer = peer; lease = packet.Lease; contact = now(); nextPulse = contact;
            Mode = ControlMode.Receiving;
            desktop.SetReceiving(true);
            int startX = packet.A > 0 ? 1 : desktop.Width - 2;
            if (!desktop.Move(startX, Pixel(packet.B, desktop.Height)))
                EndRejectedPointer();
            else if (!send(peer, new(InputOp.Grant, lease, desktop.Width, desktop.Height)))
                ReturnLocal("无法接管桌面");
            else Changed?.Invoke("正在接受控制");
            return;
        }
        if (peer != Peer || packet.Lease != lease || Mode == ControlMode.Local) return;
        if (now() - contact >= 1800) { ReturnLocal("键鼠连接超时，已返回本机", InputEnd.Timeout); return; }
        if (packet.Op == InputOp.End)
        {
            ReturnLocal((InputEnd)packet.A switch
            {
                InputEnd.LocalKeyboard => "对方的实体键盘已接管",
                InputEnd.LocalMouse => "对方的实体鼠标已接管",
                InputEnd.Paused => "对方已暂停共享或离开普通桌面",
                InputEnd.KeyboardRejected => "对方窗口拒绝键盘输入，已释放按键",
                InputEnd.PointerRejected => "对方桌面无法接收鼠标输入",
                InputEnd.HigherIntegrity => "对方前台窗口权限更高，键鼠已返回本机。操作该窗口需要在受控电脑上以管理员身份运行局域共享。",
                InputEnd.Timeout => "对方等待键鼠连接超时",
                _ => Mode == ControlMode.Waiting ? "对方暂未接管，请检查目标电脑的开关与授权" : "已返回本机"
            });
            return;
        }
        contact = now();
        if (packet.Op == InputOp.Pulse)
        {
            remoteSelecting = packet.A == 1;
            if (Mode == ControlMode.Sending) SetKeyboardBlocked(packet.B == 1);
            return;
        }
        if (Mode == ControlMode.Waiting && packet.Op == InputOp.Grant)
        {
            if (selecting() || desktop.PhysicalInputHeld) { ReturnLocal("已取消切换"); return; }
            remoteWidth = packet.A; remoteHeight = packet.B;
            x = direction > 0 ? 1 : remoteWidth - 2;
            y = Pixel(fraction, remoteHeight);
            var (anchorX, anchorY) = desktop.Anchor;
            if (!desktop.SetSending(true)) { ReturnLocal("无法隐藏本机指针，已取消切换"); return; }
            if (Mode != ControlMode.Waiting) { desktop.SetSending(false); return; }
            Mode = ControlMode.Sending;
            if (!desktop.Move(anchorX, anchorY)) { ReturnLocal("无法定位光标"); return; }
            cooldown = now() + 250;
            Changed?.Invoke("正在控制");
            return;
        }
        if (Mode != ControlMode.Receiving) return;
        if (!authorized(peer)) { ReturnLocal("已撤销控制权限"); return; }
        if (packet.Op == InputOp.Move)
        {
            if (!desktop.Move(packet.A, packet.B)) EndRejectedPointer();
        }
        else if (packet.Op is InputOp.Key or InputOp.Button or InputOp.Wheel)
        {
            // 被拒绝的按下不会在新窗口补发抬起，防止窗口切换后出现孤立按键。
            if (packet.Op == InputOp.Key && (packet.C & 1) != 0 && !held.Contains(packet with { C = packet.C & ~1 })) return;
            if (!desktop.Inject(packet))
            {
                if (desktop.ForegroundHasHigherIntegrity) ReturnLocal(LocalPrivilegeReason, InputEnd.HigherIntegrity);
                else if (packet.Op == InputOp.Key && held.Count == 0) SetKeyboardBlocked(true);
                else ReturnLocal("系统拒绝输入，已释放按键并返回本机",
                    packet.Op == InputOp.Key ? InputEnd.KeyboardRejected : InputEnd.PointerRejected);
                return;
            }
            Remember(packet);
            if (packet.Op == InputOp.Key) SetKeyboardBlocked(false);
        }
        // 活跃接收端按照同一心跳间隔报告进度，即使持续输入令定时回调延后。
        if (Mode == ControlMode.Receiving) SendPulseIfDue(now());
    }

    // 输入失败时重新检查焦点，保留其他鼠标失败的原有原因。
    private void EndRejectedPointer()
    {
        if (desktop.ForegroundHasHigherIntegrity) ReturnLocal(LocalPrivilegeReason, InputEnd.HigherIntegrity);
        else ReturnLocal("系统无法移动光标", InputEnd.PointerRejected);
    }

    // 首次按键被窗口拒绝时保留鼠标控制，允许切回普通窗口；两端都显示原因。
    private void SetKeyboardBlocked(bool blocked)
    {
        if (keyboardBlocked == blocked) return;
        keyboardBlocked = blocked;
        Notice?.Invoke(blocked ? "目标窗口未接受键盘输入，可点击普通窗口重试" : "键盘输入已恢复");
        if (Mode == ControlMode.Receiving && !send(Peer!, new(InputOp.Pulse, lease, selecting() ? 1 : 0, blocked ? 1 : 0)))
            ReturnLocal("键鼠连接已断开");
    }

    // 记录实际按下的键和按钮，重复按键只对应一次释放。
    private void Remember(InputPacket packet)
    {
        if (packet.Op is not (InputOp.Key or InputOp.Button)) return;
        var down = packet.Op == InputOp.Key ? packet with { C = packet.C & ~1 } : packet with { B = 1 };
        bool pressed = packet.Op == InputOp.Key ? (packet.C & 1) == 0 : packet.B == 1;
        if (pressed) held.Add(down); else held.Remove(down);
    }

    // 按键和滚轮按顺序投递，返回值表示是否抑制本机对应事件。
    public bool PhysicalEvent(InputPacket packet)
    {
        if (Mode is ControlMode.Receiving or ControlMode.Waiting)
            ReturnLocal("本机已接管", packet.Op == InputOp.Key ? InputEnd.LocalKeyboard : InputEnd.LocalMouse);
        if (Mode != ControlMode.Sending) return false;
        packet = packet with { Lease = lease };
        Remember(packet);
        if (!send(Peer!, packet)) ReturnLocal("键鼠连接已断开");
        return true;
    }

    // 每个物理位移只应用一次。系统自身的定位事件不作为远端位移累加。
    public void RemoteMotion(int dx, int dy)
    {
        if (Mode != ControlMode.Sending || (dx == 0 && dy == 0)) return;
        int requestedX = x + dx;
        y = Math.Clamp(y + dy, 0, remoteHeight - 1);
        x = Math.Clamp(requestedX, 0, remoteWidth - 1);
        int side = requestedX < 0 ? -1 : requestedX >= remoteWidth ? 1 : 0;
        if (side != 0 && held.Count == 0 && !remoteSelecting && now() >= cooldown)
        {
            string? next = neighbor(Peer!, side);
            if (next is not null)
            {
                int heightFraction = Fraction(y, remoteHeight);
                ReturnLocal("切换电脑");
                if (next == localId)
                    desktop.Move(side < 0 ? desktop.Width - 2 : 1, Pixel(heightFraction, desktop.Height));
                else Request(next, side, heightFraction);
                return;
            }
        }
        if (!send(Peer!, new(InputOp.Move, lease, x, y))) ReturnLocal("键鼠连接已断开");
    }

    // 本机桌面使用真实屏幕位置判断边缘，等待确认期间仍允许用户取消。
    public bool Pointer(int px, int py)
    {
        if (!Enabled || Mode == ControlMode.Sending) return false;
        if (Mode == ControlMode.Receiving) { ReturnLocal("本机鼠标已接管", InputEnd.LocalMouse); return false; }
        if (Mode == ControlMode.Waiting)
        {
            if (Math.Abs(px - homeX) > 12 || Math.Abs(py - homeY) > 20) ReturnLocal("已取消切换");
            return false;
        }
        int boundary = px <= 0 ? -1 : px >= desktop.Width - 1 ? 1 : 0;
        if (boundary == 0) leaveEdge = false;
        if (boundary == 0 || leaveEdge || selecting() || now() < cooldown) { edge = null; return false; }
        if (edge == boundary && Math.Abs(py - homeY) <= 2)
        {
            string? next = neighbor(localId, boundary);
            if (next is not null && !desktop.PhysicalInputHeld) Request(next, boundary, Fraction(py, desktop.Height));
        }
        edge = boundary; homeX = px; homeY = py;
        return false;
    }

    // 请求阶段继续允许本地输入，只有接收端返回同一会话的确认才切换。
    private void Request(string peer, int side, int heightFraction)
    {
        Peer = peer; direction = side; fraction = heightFraction; lease = Guid.NewGuid();
        Mode = ControlMode.Waiting; contact = now(); remoteSelecting = false;
        if (!send(peer, new(InputOp.Request, lease, side, fraction))) ReturnLocal("目标电脑未连接");
        else Changed?.Invoke("等待目标电脑接管");
    }

    // 两端独立计时，拔网线或对方进程停止时，即使无法发送结束消息也会本地释放。
    public void Tick()
    {
        if (Mode == ControlMode.Local) return;
        long time = now();
        if (time - contact >= 1800) { ReturnLocal("键鼠连接超时，已返回本机", InputEnd.Timeout); return; }
        if (Mode != ControlMode.Waiting) SendPulseIfDue(time);
    }

    // 滚动与定时检查共用发送期限，连续输入不增加心跳频率。
    private void SendPulseIfDue(long time)
    {
        if (time < nextPulse) return;
        if (Mode == ControlMode.Receiving && desktop.ForegroundHasHigherIntegrity)
        {
            ReturnLocal(LocalPrivilegeReason, InputEnd.HigherIntegrity);
            return;
        }
        nextPulse = time + 300;
        if (!send(Peer!, new(InputOp.Pulse, lease, selecting() ? 1 : 0, Mode == ControlMode.Receiving && keyboardBlocked ? 1 : 0))) ReturnLocal("键鼠连接已断开");
    }

    // 将屏幕边缘高度按比例映射到不同分辨率的电脑。
    private static int Fraction(int pos, int size) => (int)(Math.Clamp(pos, 0, size - 1) * 65535L / Math.Max(1, size - 1));
    // 把归一化高度转换回物理像素。
    private static int Pixel(int value, int size) => (int)(value * (size - 1L) / 65535);
}
