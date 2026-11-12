using System.Diagnostics;

namespace LanShare.Core;

// 网络线程有序入队，键鼠线程每次处理一小批；只合并同一会话中连续的绝对位置。
public sealed class DesktopInputQueue(Action<Action> post, Action<InputPacket> apply, Action failed) : IDisposable
{
    private readonly LinkedList<InputPacket> items = new();
    private TaskCompletionSource? room;
    private bool scheduled, closed;

    // 单个连接只有一个读取者。达到上限后等待键鼠线程取走事件，按钮与键盘绝不丢弃。
    public Task Add(InputPacket packet)
    {
        Task ready;
        lock (items)
        {
            if (closed) return Task.CompletedTask;
            if (packet.Op == InputOp.Move && items.Last is {} tail && tail.Value.Op == InputOp.Move && tail.Value.Lease == packet.Lease)
                tail.Value = packet;
            else items.AddLast(packet);
            ready = items.Count >= 128 ? (room ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task : Task.CompletedTask;
            if (scheduled) return ready;
            scheduled = true;
        }
        Schedule();
        return ready;
    }

    // 窗口关闭期间调度可能失败，关闭该输入通道并唤醒等候的网络读取者。
    private void Schedule()
    {
        try { post(Drain); }
        catch (InvalidOperationException) { Dispose(); failed(); }
    }

    // 短批次为窗口消息、实体抢回和心跳留出处理时间。
    private void Drain()
    {
        long start = Stopwatch.GetTimestamp();
        try
        {
            for (int i = 0; i < 32; i++)
            {
                InputPacket packet;
                lock (items)
                {
                    if (closed || items.First == null) { scheduled = false; return; }
                    packet = items.First.Value; items.RemoveFirst();
                    room?.TrySetResult(); room = null;
                }
                apply(packet);
                if (Stopwatch.GetElapsedTime(start).TotalMilliseconds >= 4) break;
            }
        }
        catch (Exception) { Dispose(); failed(); return; }
        lock (items)
        {
            if (closed || items.Count == 0) { scheduled = false; return; }
        }
        Schedule();
    }

    // 会话关闭后废弃旧输入，不让它落入下一次接管。
    public void Dispose()
    {
        lock (items) { closed = true; items.Clear(); room?.TrySetResult(); room = null; }
    }
}
