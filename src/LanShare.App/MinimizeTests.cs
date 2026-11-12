using System.Diagnostics;
using System.Runtime.InteropServices;
using LanShare.Core;

namespace LanShare.App;

// 使用真实窗口消息和模拟受控桌面验证最小化，不向用户桌面注入键鼠。
internal static class MinimizeTests
{
    // 模拟桌面只记录最后一次收到的坐标。
    private sealed class TestDesktop : IInputDesktop
    {
        public int Width => 1000;
        public int Height => 1000;
        public (int X, int Y) Anchor => (500, 500);
        public bool PhysicalInputHeld => false;
        internal int LastX { get; private set; }

        // 保存收到的移动，用于验证窗口隐藏期间仍能投递。
        public bool Move(int x, int y) { LastX = x; return true; }

        // 自检不操作实体键盘和鼠标。
        public bool Inject(InputPacket packet) => true;
    }

    // 检查连续最小化、窗口恢复及主线程超过会话超时时间仍能处理心跳和鼠标。
    internal static void Run(MainForm form, List<string> lines)
    {
        form.RestoreWindow();
        nint window = form.Handle;
        long longest = 0;
        for (int trip = 0; trip < 8; trip++)
        {
            var watch = Stopwatch.StartNew();
            SendMessage(window, 0x0112, 0xF020, 0);
            longest = Math.Max(longest, watch.ElapsedMilliseconds);
            if (form.Visible || form.IsDisposed || form.WindowState == FormWindowState.Minimized)
                throw new Exception("Minimize must hide directly without native minimize layout");
            form.RestoreWindow();
            if (!form.Visible || form.Handle != window) throw new Exception("Restore replaced or lost the main window");
        }
        if (longest >= 1000) throw new Exception($"Minimize took {longest} ms");
        lines.Add($"PASS: eight minimize/restore cycles preserve the window, slowest minimize {longest} ms");
        form.WindowState = FormWindowState.Minimized;
        Application.DoEvents();
        if (form.Visible) throw new Exception("Direct size change did not hide the window");
        form.RestoreWindow();

        using var loop = new InputMessageLoop();
        var desktop = new TestDesktop();
        var lease = Guid.NewGuid();
        int pulses = 0;
        DesktopInputQueue? inbox = null;
        var router = new SharedInput("B", desktop, (_, _) => null, _ => true, (peer, packet) =>
        {
            if (packet.Op == InputOp.Pulse)
            {
                int x = ++pulses;
                _ = inbox!.Add(new(InputOp.Pulse, lease));
                _ = inbox.Add(new(InputOp.Move, lease, x, 80));
            }
            return true;
        }, () => false);
        using var queue = new DesktopInputQueue(loop.Post, p => router.Receive("A", p), () => { });
        inbox = queue;
        using var started = new ManualResetEventSlim();
        System.Windows.Forms.Timer? heartbeat = null;
        loop.Post(() =>
        {
            router.Enable(true);
            heartbeat = new() { Interval = 150 };
            heartbeat.Tick += (_, _) => router.Tick();
            heartbeat.Start();
            started.Set();
        });
        if (!started.Wait(5000)) throw new Exception("Input loop did not start independently");
        _ = queue.Add(new(InputOp.Request, lease, 1, 8000));
        // 在最小化的可见性通知中故意卡住主线程，期间不处理任何界面消息。
        void StallWhenHidden(object? sender, EventArgs args)
        {
            if (!form.Visible) Thread.Sleep(2200);
        }
        form.VisibleChanged += StallWhenHidden;
        try { SendMessage(window, 0x0112, 0xF020, 0); }
        finally { form.VisibleChanged -= StallWhenHidden; }

        var result = new TaskCompletionSource<bool>();
        loop.Post(() =>
        {
            heartbeat!.Dispose();
            result.SetResult(router.Mode == ControlMode.Receiving && pulses >= 3 && desktop.LastX >= 3);
            router.ReturnLocal("自检结束");
        });
        if (!result.Task.Wait(5000) || !result.Task.Result)
            throw new Exception("Shared mouse or heartbeat paused behind the minimized UI");
        lines.Add("PASS: mouse packets and session heartbeats continue during a 2.2 s stall inside minimize");
        form.RestoreWindow();
    }

    // 向实际主窗体发送标题栏最小化使用的系统命令。
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendMessage(nint hwnd, uint msg, nint wParam, nint lParam);
}
