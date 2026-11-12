namespace LanShare.App;

// 专供键鼠钩子、入站输入与心跳使用的 STA 消息循环，独立于主窗体。
internal sealed class InputMessageLoop : IDisposable
{
    private readonly TaskCompletionSource<Control> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread thread;
    private readonly object gate = new();
    private readonly System.Threading.Timer timer;
    private Action? tick;
    private int tickPosted;
    private bool closed;

    // 创建消息窗口后才允许投递，确保首次启用钩子时已有可用的线程上下文。
    internal InputMessageLoop()
    {
        timer = new System.Threading.Timer(_ => QueueTick());
        thread = new Thread(Run) { IsBackground = true, Name = "LAN Share Input" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    // 窗口、定时器和原生资源由同一线程分派消息。
    private void Run()
    {
        try
        {
            using var target = new Control();
            _ = target.Handle;
            ready.SetResult(target);
            Application.Run();
        }
        catch (Exception ex) when (ready.TrySetException(ex)) { }
    }

    // 按投递顺序执行；关闭期间让网络队列走已有的断开处理。
    internal void Post(Action action)
    {
        lock (gate)
        {
            if (closed) throw new ObjectDisposedException(nameof(InputMessageLoop));
            ready.Task.GetAwaiter().GetResult().BeginInvoke(action);
        }
    }

    // 在键鼠线程启停时钟，线程池仅负责投递，所有状态检查仍在原线程执行。
    internal void SetClock(Action? check)
    {
        if (tick == check) return;
        tick = check;
        int interval = check is null ? Timeout.Infinite : 150;
        timer.Change(interval, interval);
    }

    // 心跳作为普通工作项排队，避免滚轮输入饿死低优先级 WM_TIMER；每次最多挂起一次检查。
    private void QueueTick()
    {
        if (Interlocked.Exchange(ref tickPosted, 1) != 0) return;
        try
        {
            Post(() =>
            {
                try { tick?.Invoke(); }
                finally { Volatile.Write(ref tickPosted, 0); }
            });
        }
        catch (ObjectDisposedException) { Volatile.Write(ref tickPosted, 0); }
    }

    // 让先前提交的清理工作执行完，再结束本线程的消息循环。
    public void Dispose()
    {
        lock (gate)
        {
            if (closed) return;
            closed = true;
            ready.Task.GetAwaiter().GetResult().BeginInvoke(new Action(() =>
            {
                tick = null; timer.Dispose(); Application.ExitThread();
            }));
        }
        thread.Join();
    }
}
