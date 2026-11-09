using System.Buffers.Binary;
using System.Text.Json;

namespace LanShare.Core;

// 使用有长度上限的帧，避免网络输入造成无限内存分配。
public static class Wire
{
    public const int MaxPayload = 9 * 1024 * 1024;
    public const byte Json = 1, Blob = 2, Chunk = 3;
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // 帧格式为类型一字节、大端长度四字节、负载；每个方向只能有一个写入者。
    public static async Task WriteAsync(Stream stream, byte kind, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        if (data.Length > MaxPayload) throw new InvalidDataException("消息超过大小上限。");
        byte[] header = new byte[5];
        header[0] = kind;
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(1), data.Length);
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(data, ct);
    }

    // 先验证长度再分配，ReadExactlyAsync 同时处理分片与提前断线。
    public static async Task<Frame> ReadAsync(Stream stream, CancellationToken ct, int limit = MaxPayload)
    {
        byte[] header = new byte[5];
        await stream.ReadExactlyAsync(header, ct);
        int length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1));
        if (length < 0 || length > limit || length > MaxPayload) throw new InvalidDataException("无效消息长度。");
        if (header[0] is not (Json or Blob or Chunk)) throw new InvalidDataException("无效消息类型。");
        byte[] data = new byte[length];
        await stream.ReadExactlyAsync(data, ct);
        return new Frame(header[0], data);
    }

    // 控制消息统一采用 UTF-8 JSON，二进制内容另用原始字节帧。
    public static Task SendJsonAsync<T>(Stream stream, T value, CancellationToken ct) =>
        WriteAsync(stream, Json, JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions), ct);

    // JSON 握手及元数据使用较小上限。
    public static async Task<T> ReadJsonAsync<T>(Stream stream, CancellationToken ct)
    {
        var frame = await ReadAsync(stream, ct, 16 * 1024);
        if (frame.Kind != Json) throw new InvalidDataException("预期控制消息。");
        return JsonSerializer.Deserialize<T>(frame.Data, JsonOptions) ?? throw new InvalidDataException("空消息。");
    }
}

// 不同帧类型共享不可变传输容器。
public sealed record Frame(byte Kind, byte[] Data);
public sealed record Hello(string Channel, string Token, string Name, int Version = 2, string NodeId = "");
public sealed record Ready(string Token, string Name, int Version = 2, string NodeId = "");
public sealed record Message(string Kind, string Id = "", string Text = "", long Size = 0, string Hash = "");
public sealed record ClipboardContent(string Format, byte[] Data);
public sealed record FileOffer(string Id, string Name, long Size);
public sealed record TransferProgress(string Name, long Completed, long Total, bool Sending);
