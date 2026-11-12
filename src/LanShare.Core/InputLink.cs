using System.Buffers.Binary;

namespace LanShare.Core;

// 每次跨屏独立生成控制编号，旧会话中的延迟消息不能操作新的桌面。
public enum InputOp : byte { Request, Grant, End, Pulse, Move, Key, Button, Wheel }

// 固定长度帧。移动使用虚拟桌面内的像素；键盘保留虚拟键、扫描码和扩展位。
public readonly record struct InputPacket(InputOp Op, Guid Lease, int A = 0, int B = 0, int C = 0)
{
    internal const int Length = 29;

    // 在读取外部数据后验证取值范围，避免无效按键、按钮或坐标进入系统输入接口。
    internal bool Valid => Lease != Guid.Empty && Op switch
    {
        InputOp.Request => A is -1 or 1 && B is >= 0 and <= 65535,
        InputOp.Grant => A is >= 2 and <= 131072 && B is >= 2 and <= 131072,
        InputOp.End => true,
        InputOp.Pulse => A is 0 or 1,
        InputOp.Move => A is >= 0 and < 131072 && B is >= 0 and < 131072,
        InputOp.Key => A is > 0 and < 255 && B is >= 0 and <= 511 && C is >= 0 and <= 3,
        InputOp.Button => A is >= 1 and <= 5 && B is 0 or 1,
        InputOp.Wheel => A is >= -32768 and <= 32767 && B is 0 or 1,
        _ => false
    };

    // 合并仍在队尾的相邻输入。小幅滚动最多合到单个刻度，保留余量、反转和横纵切换。
    internal bool TryMergeTail(LinkedListNode<InputPacket>? tail)
    {
        if (tail is null || tail.Value.Lease != Lease || tail.Value.Op != Op) return false;
        var previous = tail.Value;
        if (Op == InputOp.Move) { tail.Value = this; return true; }
        if (Op != InputOp.Wheel || B != previous.B || A == 0 || Math.Sign(A) != Math.Sign(previous.A)) return false;
        long delta = (long)previous.A + A;
        if (delta is < -120 or > 120) return false;
        tail.Value = this with { A = (int)delta };
        return true;
    }

    // 同一缓冲反复使用，避免鼠标移动时为每一帧分配 JSON。
    internal void Encode(Span<byte> bytes)
    {
        bytes[0] = (byte)Op;
        Lease.TryWriteBytes(bytes[1..17]);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[17..], A);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[21..], B);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[25..], C);
    }

    // 解码定长事件，未知操作直接关闭键鼠通道。
    internal static InputPacket Decode(ReadOnlySpan<byte> bytes)
    {
        var packet = new InputPacket((InputOp)bytes[0], new Guid(bytes[1..17]), BinaryPrimitives.ReadInt32LittleEndian(bytes[17..]),
            BinaryPrimitives.ReadInt32LittleEndian(bytes[21..]), BinaryPrimitives.ReadInt32LittleEndian(bytes[25..]));
        return packet.Valid ? packet : throw new InvalidDataException("无效键鼠事件。");
    }
}

// 每个已协商的 TLS 连接独立读写键鼠；网络读写不会发生在系统输入回调内。
public sealed class InputLink(Stream stream) : IDisposable
{
    private readonly LinkedList<InputPacket> queue = new();
    private readonly SemaphoreSlim available = new(0, 1);
    private readonly CancellationTokenSource stop = new();
    private int closed;
    public bool IsOpen => Volatile.Read(ref closed) == 0;
    public Func<InputPacket, Task>? Received { get; set; }
    public event Action? Closed;

    // 队列从空变为非空时立即唤醒写线程；连续小幅滚动只合并尚未发送的部分。
    public bool TrySend(InputPacket packet)
    {
        lock (queue)
        {
            if (!IsOpen || !packet.Valid) return false;
            if (packet.TryMergeTail(queue.Last)) return true;
            if (queue.Count >= 256) { _ = Task.Run(Dispose); return false; }
            queue.AddLast(packet);
            if (queue.Count == 1) available.Release();
            return true;
        }
    }

    // 读写循环使用独立缓冲，应用处理完当前事件才读取下一条。
    internal void Start()
    {
        _ = Task.Run(async () =>
        {
            byte[] buffer = new byte[InputPacket.Length];
            try
            {
                while (IsOpen)
                {
                    await stream.ReadExactlyAsync(buffer, stop.Token);
                    var packet = InputPacket.Decode(buffer);
                    if (Received is {} receive) await receive(packet).WaitAsync(stop.Token);
                }
            }
            catch (Exception) { Dispose(); }
        });
        _ = Task.Run(async () =>
        {
            // 使用原有定长帧，只将已经就绪的输入一起写入 TLS，不等待凑批。
            byte[] buffer = new byte[32 * InputPacket.Length];
            try
            {
                while (IsOpen)
                {
                    await available.WaitAsync(stop.Token);
                    int bytes = 0;
                    lock (queue)
                    {
                        if (!IsOpen) return;
                        while (queue.First is { } first && bytes < buffer.Length)
                        {
                            first.Value.Encode(buffer.AsSpan(bytes, InputPacket.Length));
                            bytes += InputPacket.Length;
                            queue.RemoveFirst();
                        }
                        if (queue.Count != 0) available.Release();
                    }
                    await stream.WriteAsync(buffer.AsMemory(0, bytes), stop.Token);
                }
            }
            catch (Exception) { Dispose(); }
        });
    }

    // 只关闭此输入连接；控制器收到通知后释放本次远端按下的所有键。
    public void Dispose()
    {
        if (Interlocked.Exchange(ref closed, 1) != 0) return;
        stop.Cancel();
        stream.Dispose();
        lock (queue) queue.Clear();
        Closed?.Invoke();
    }
}
