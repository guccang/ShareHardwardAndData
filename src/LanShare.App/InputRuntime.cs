using LanShare.Core;

namespace LanShare.App;

// 主窗口只提交设置快照和读取状态。所有钩子、输入包和会话状态均在键鼠线程处理。
internal sealed class InputRuntime : IDisposable
{
    internal sealed record Status(ControlMode Mode, string? Peer, bool Enabled);
    private readonly InputMessageLoop loop = new();
    private readonly WindowsInput desktop = new();
    private readonly SharedInput router;
    private readonly string localId;
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 150 };
    private RemotePointerMarker? marker;
    private string[] order = [];
    private HashSet<string> allowed = [];
    private Dictionary<string, PeerSession> peers = [];
    private bool running;
    private volatile Status status = new(ControlMode.Local, null, false);
    internal Status Current => status;
    internal event Action<string?, bool>? Changed;

    // 建立状态机，首次启用时才在专用线程安装钩子和启动心跳。
    internal InputRuntime(string localId, Func<bool> selecting)
    {
        this.localId = localId;
        router = new(localId, desktop, Neighbor, id => allowed.Contains(id) && !desktop.HasPendingReleases,
            (id, packet) => peers.TryGetValue(id, out var peer) && peer.Input?.TrySend(packet) == true, selecting);
        desktop.Router = router;
        timer.Tick += (_, _) => Tick();
        router.Changed += reason => Publish(reason);
        router.Notice += reason => Publish(reason, notice: true);
    }

    // 只跨到当前快照中相邻且仍在线的电脑。
    private string? Neighbor(string from, int side)
    {
        int index = Array.IndexOf(order, from), next = index + side;
        if (index < 0 || next < 0 || next >= order.Length) return null;
        string id = order[next];
        return id == localId || peers.GetValueOrDefault(id)?.Input?.IsOpen == true ? id : null;
    }

    // 会话替换或撤权时先结束旧控制，再切换到新的路由表。
    internal void Configure(string[] positions, HashSet<string> permissions, Dictionary<string, PeerSession> sessions)
    {
        loop.Post(() =>
        {
            if (router.Peer is {} id)
            {
                if (peers.GetValueOrDefault(id) != sessions.GetValueOrDefault(id)) router.ReturnLocal("对方已离线");
                else if (router.Mode == ControlMode.Receiving && !permissions.Contains(id)) router.ReturnLocal("已撤销控制权限");
            }
            order = positions; allowed = permissions; peers = sessions;
        });
    }

    // 由输入线程更换紧急返回快捷键，避免与键盘回调并发修改。
    internal void SetReturnKey(Keys key) => loop.Post(() => desktop.ReturnKey = key);

    // 托盘或显示器变化可主动结束当前会话。
    internal void ReturnLocal(string reason) => loop.Post(() => router.ReturnLocal(reason));

    // 钩子和定时器在同一线程启动、停用和释放。
    internal void SetRunning(bool enabled) => loop.Post(() =>
    {
        running = enabled;
        if (enabled)
        {
            try { desktop.Start(); router.Enable(WindowsInput.OnUserDesktop()); timer.Start(); }
            catch (Exception ex)
            {
                running = false; router.Enable(false); desktop.Dispose();
                timer.Enabled = desktop.HasPendingReleases; Publish(ex.Message); return;
            }
        }
        else
        {
            router.Enable(false); desktop.Dispose(); timer.Enabled = desktop.HasPendingReleases;
        }
        Publish();
    });

    // 即使主界面堵塞也继续检查桌面状态、补送释放事件和维护会话。
    private void Tick()
    {
        bool active = WindowsInput.OnUserDesktop();
        if (active && desktop.HasPendingReleases && !desktop.ForegroundHasHigherIntegrity) desktop.RetryReleases();
        bool enabled = running && active;
        if (router.Enabled != enabled) { router.Enable(enabled); Publish(); }
        if (!running && !desktop.HasPendingReleases) timer.Stop();
        router.Tick();
    }

    // 绑定到具体的已配对会话，晚到的旧连接事件不会影响新会话。
    internal void Bind(PeerSession peer, TaskCompletionSource<bool> registered)
    {
        if (peer.Input is not {} link) return;
        var inbox = new DesktopInputQueue(loop.Post, packet =>
        {
            if (link.IsOpen && peers.GetValueOrDefault(peer.PeerId) == peer)
                router.Receive(peer.PeerId, packet);
        }, link.Dispose);
        // 配对回调先建立通道，主界面随后提交身份与授权；首包等到该快照入队。
        link.Received = async packet =>
        {
            if (await registered.Task.ConfigureAwait(false)) await inbox.Add(packet).ConfigureAwait(false);
        };
        link.Closed += () =>
        {
            registered.TrySetResult(false);
            inbox.Dispose();
            try
            {
                loop.Post(() =>
                {
                    if (router.Peer == peer.PeerId && peers.GetValueOrDefault(peer.PeerId) == peer)
                        router.ReturnLocal("键鼠连接已断开");
                });
            }
            catch (ObjectDisposedException) { }
        };
    }

    // 立即发布只读方向状态，界面异步更新文字，接收端指针仍由输入线程跟踪。
    private void Publish(string? reason = null, bool notice = false)
    {
        status = new(router.Mode, router.Peer, router.Enabled);
        // 延后创建或隐藏标记窗口，避免在原生鼠标钩子内执行窗口绘制。
        if (router.Mode == ControlMode.Receiving || marker is not null)
            SynchronizationContext.Current!.Post(_ =>
            {
                if (router.Mode == ControlMode.Receiving) (marker ??= new()).SetReceiving(true);
                else marker?.SetReceiving(false);
            }, null);
        Changed?.Invoke(reason, notice);
    }

    // 先释放按下状态及两端指针窗口，再退出键鼠线程。
    public void Dispose()
    {
        loop.Post(() =>
        {
            router.Enable(false); timer.Dispose(); desktop.Dispose(); marker?.Dispose(); marker = null;
        });
        loop.Dispose();
    }
}
