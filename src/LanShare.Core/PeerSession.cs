using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace LanShare.Core;

// 一台对端的双向会话：控制连接负责剪贴板、心跳和确认，数据连接负责流式文件。
public sealed class PeerSession : IDisposable
{
    public const int MaxTextBytes = 1024 * 1024, MaxImageBytes = 8 * 1024 * 1024;
    private readonly Stream control, bulk;
    private readonly SemaphoreSlim controlGate = new(1), bulkGate = new(1);
    private readonly CancellationTokenSource stop = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<Message>> replies = new();
    private long lastSeen = Environment.TickCount64;
    private int closed, started;
    public string PeerName { get; }
    public string PeerId { get; }
    public InputLink? Input { get; }
    public string ReceiveFolder { get; }
    public bool IsConnected => Volatile.Read(ref closed) == 0;
    public event Action<ClipboardContent>? ClipboardReceived;
    public event Action<DesktopLayout>? LayoutReceived;
    public event Action<string>? FileReceived;
    public event Action<TransferProgress>? Progress;
    public event Action<string>? Closed;
    public event Action<string>? Notice;
    public Func<FileOffer, CancellationToken, Task<bool>>? ConfirmFileAsync { get; set; }

    // 接收目录在会话建立时固定，避免传输过程中切换目录。
    public PeerSession(Stream control, Stream bulk, string peerName, string folder, string peerId = "", Stream? input = null)
    {
        this.control = control; this.bulk = bulk;
        Input = input is null ? null : new InputLink(input);
        PeerName = peerName; PeerId = peerId;
        ReceiveFolder = Path.GetFullPath(folder);
    }

    // 订阅事件后再启动接收，避免遗漏首个剪贴板或文件请求。
    public void Start()
    {
        if (Interlocked.Exchange(ref started, 1) != 0) return;
        Input?.Start();
        _ = Task.Run(() => GuardAsync(ReadControlAsync));
        _ = Task.Run(() => GuardAsync(ReadBulkAsync));
        _ = Task.Run(() => GuardAsync(HeartbeatAsync));
    }

    // 只发送文字与 PNG，不读取密码日志、文件内容索引或其他剪贴板格式。
    public async Task SendClipboardAsync(ClipboardContent content, CancellationToken ct = default)
    {
        ValidateClipboard(content.Format, content.Data.Length);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, stop.Token);
        await controlGate.WaitAsync(linked.Token);
        try
        {
            // 剪贴板使用控制通道，不等待文件传输占用的数据通道。
            await Wire.SendJsonAsync(control, new Message("clipboard", Text: content.Format, Size: content.Data.Length), linked.Token);
            await Wire.WriteAsync(control, Wire.Blob, content.Data, linked.Token);
        }
        catch { Close("剪贴板发送中断"); throw; }
        finally { controlGate.Release(); }
    }

    // 文件使用固定大小缓冲流式发送，只有接收方确认后才读取内容。
    public async Task SendFileAsync(string path, CancellationToken ct = default)
    {
        string name = Path.GetFileName(path);
        SafeFiles.ValidateName(name);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, stop.Token);
        await bulkGate.WaitAsync(linked.Token);
        bool writing = false;
        string id = Guid.NewGuid().ToString("N");
        try
        {
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
            long size = file.Length;
            if (size > SafeFiles.MaxFileSize) throw new IOException("单个文件不能超过 10 GiB。");
            var accepted = NewReply(id + ":offer");
            await Wire.SendJsonAsync(bulk, new Message("file", id, name, size), linked.Token);
            writing = true;
            var answer = await accepted.Task.WaitAsync(TimeSpan.FromMinutes(2), linked.Token);
            if (answer.Kind != "accept") { writing = false; throw new IOException("对方拒绝或未允许接收：" + name); }
            var completed = NewReply(id + ":done");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[65536];
            long sent = 0;
            var clock = Stopwatch.StartNew();
            Progress?.Invoke(new(name, 0, size, true));
            while (sent < size)
            {
                int read = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, size - sent)), linked.Token);
                if (read == 0) throw new IOException("源文件在传输中发生变化。");
                hash.AppendData(buffer, 0, read);
                await Wire.WriteAsync(bulk, Wire.Chunk, buffer.AsMemory(0, read), linked.Token);
                sent += read;
                if (clock.ElapsedMilliseconds >= 100) { Progress?.Invoke(new(name, sent, size, true)); clock.Restart(); }
            }
            await Wire.SendJsonAsync(bulk, new Message("end", id, Hash: Convert.ToHexString(hash.GetHashAndReset())), linked.Token);
            var result = await completed.Task.WaitAsync(TimeSpan.FromSeconds(60), linked.Token);
            writing = false;
            if (result.Kind != "complete") throw new IOException("接收端未能保存文件：" + result.Text);
            Progress?.Invoke(new(name, size, size, true));
        }
        catch { if (writing) Close("文件传输中断，请重新连接后重试"); throw; }
        finally
        {
            replies.TryRemove(id + ":offer", out _); replies.TryRemove(id + ":done", out _);
            bulkGate.Release();
        }
    }

    // 文件确认与完成回执按传输标识匹配，未知或迟到回执不会唤醒其他任务。
    private TaskCompletionSource<Message> NewReply(string id)
    {
        var source = new TaskCompletionSource<Message>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!replies.TryAdd(id, source)) throw new InvalidOperationException("重复传输标识。");
        return source;
    }

    // 控制通道串行写入，防止心跳与多个文件回执交错。
    private async Task SendControlAsync(Message message, CancellationToken ct)
    {
        await controlGate.WaitAsync(ct);
        try { await Wire.SendJsonAsync(control, message, ct); }
        catch { Close("控制通道写入中断"); throw; }
        finally { controlGate.Release(); }
    }

    // 使用旧版也能识别的心跳信封，仅在连接和编辑时发送快照。
    public async Task SendLayoutAsync(DesktopLayout layout)
    {
        if (!layout.IsValid()) throw new InvalidDataException("无效的设备排列。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await SendControlAsync(new Message("ping", Layout: layout), timeout.Token);
    }

    // 控制通道不包含文件正文，不受大文件传输等待影响。
    private async Task ReadControlAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            var message = await Wire.ReadJsonAsync<Message>(control, stop.Token);
            Interlocked.Exchange(ref lastSeen, Environment.TickCount64);
            if (message.Kind == "ping")
            {
                if (message.Layout is {} layout && layout.IsValid()) LayoutReceived?.Invoke(layout);
                continue;
            }
            if (message.Kind == "clipboard")
            {
                ValidateClipboard(message.Text, message.Size);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(12));
                var frame = await Wire.ReadAsync(control, timeout.Token, (int)message.Size);
                if (frame.Kind != Wire.Blob || frame.Data.Length != message.Size) throw new InvalidDataException("剪贴板数据长度不匹配。");
                ClipboardReceived?.Invoke(new(message.Text, frame.Data));
                Interlocked.Exchange(ref lastSeen, Environment.TickCount64);
                continue;
            }
            string suffix = message.Kind is "accept" or "reject" ? ":offer" : ":done";
            if (message.Kind is not ("accept" or "reject" or "complete" or "failed")) throw new InvalidDataException("未知控制消息。");
            if (replies.TryGetValue(message.Id + suffix, out var source)) source.TrySetResult(message);
        }
    }

    // 持续发送轻量心跳，休眠或网络中断后自动结束失效会话。
    private async Task HeartbeatAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(stop.Token))
        {
            if (Environment.TickCount64 - Interlocked.Read(ref lastSeen) > 15000) throw new IOException("连接超时。");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await SendControlAsync(new Message("ping"), timeout.Token);
        }
    }

    // 业务帧严格按类型解析，畸形或超限消息使该对端断开。
    private async Task ReadBulkAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            var message = await Wire.ReadJsonAsync<Message>(bulk, stop.Token);
            if (message.Kind == "file") await ReceiveFileAsync(message);
            else throw new InvalidDataException("未知数据消息。");
        }
    }

    // 文件先落盘到唯一临时名，校验大小和 SHA-256 后才出现在接收列表。
    private async Task ReceiveFileAsync(Message message)
    {
        SafeFiles.ValidateName(message.Text);
        if (message.Size < 0 || message.Size > SafeFiles.MaxFileSize || !Guid.TryParseExact(message.Id, "N", out _))
            throw new InvalidDataException("无效文件元数据。");
        bool accepted = false;
        using (var decision = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
        {
            decision.CancelAfter(TimeSpan.FromSeconds(110));
            try
            {
                if (ConfirmFileAsync is not null)
                    accepted = await ConfirmFileAsync(new(message.Id, message.Text, message.Size), decision.Token).WaitAsync(decision.Token);
            }
            catch (OperationCanceledException) when (!stop.IsCancellationRequested) { }
        }
        if (!accepted) { await SendControlAsync(new("reject", message.Id), stop.Token); return; }
        string? temporary = null;
        bool receiving = false;
        try
        {
            Directory.CreateDirectory(ReceiveFolder);
            temporary = Path.Combine(ReceiveFolder, ".lanshare-" + Guid.NewGuid().ToString("N") + ".partial");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                await SendControlAsync(new("accept", message.Id), stop.Token);
                receiving = true;
                long received = 0;
                var clock = Stopwatch.StartNew();
                Progress?.Invoke(new(message.Text, 0, message.Size, false));
                while (received < message.Size)
                {
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                    idle.CancelAfter(TimeSpan.FromSeconds(60));
                    var frame = await Wire.ReadAsync(bulk, idle.Token, 65536);
                    if (frame.Kind != Wire.Chunk || frame.Data.Length == 0 || frame.Data.Length > message.Size - received)
                        throw new InvalidDataException("无效文件数据块。");
                    await file.WriteAsync(frame.Data, stop.Token);
                    hash.AppendData(frame.Data); received += frame.Data.Length;
                    if (clock.ElapsedMilliseconds >= 100) { Progress?.Invoke(new(message.Text, received, message.Size, false)); clock.Restart(); }
                }
                using var endTimeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                endTimeout.CancelAfter(TimeSpan.FromSeconds(60));
                var end = await Wire.ReadJsonAsync<Message>(bulk, endTimeout.Token);
                if (end.Kind != "end" || end.Id != message.Id || end.Hash != Convert.ToHexString(hash.GetHashAndReset()))
                    throw new InvalidDataException("文件完整性校验失败。");
                await file.FlushAsync(stop.Token);
            }
            string saved = SafeFiles.Commit(temporary, ReceiveFolder, message.Text);
            temporary = null;
            await SendControlAsync(new("complete", message.Id), stop.Token);
            Progress?.Invoke(new(message.Text, message.Size, message.Size, false));
            FileReceived?.Invoke(saved);
        }
        catch (Exception ex) when (!receiving && !stop.IsCancellationRequested)
        {
            await SendControlAsync(new("reject", message.Id), stop.Token);
            Notice?.Invoke("无法创建接收文件：" + ex.Message);
        }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); }
                catch (IOException) { Notice?.Invoke("未完成文件暂时无法删除：" + temporary); }
                catch (UnauthorizedAccessException) { Notice?.Invoke("未完成文件暂时无法删除：" + temporary); }
            }
        }
    }

    // 在发送端和接收端同时限制格式和大小。
    public static void ValidateClipboard(string format, long length)
    {
        int limit = format switch { "text" => MaxTextBytes, "png" => MaxImageBytes, _ => throw new InvalidDataException("不支持的剪贴板格式。") };
        if (length < 0 || length > limit) throw new InvalidDataException("剪贴板内容超过大小上限。");
    }

    // 后台任务的异常统一收口，保证断线通知只发出一次。
    private async Task GuardAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { if (!stop.IsCancellationRequested) Close(ex is OperationCanceledException ? "连接超时" : ex.Message); }
    }

    // 取消所有等待，并关闭两个传输通道。
    public void Close(string reason)
    {
        if (Interlocked.Exchange(ref closed, 1) != 0) return;
        stop.Cancel(); Input?.Dispose(); control.Dispose(); bulk.Dispose();
        foreach (var reply in replies.Values) reply.TrySetCanceled();
        Closed?.Invoke(reason);
    }

    // 界面退出或移除设备时复用一致的断开流程。
    public void Dispose() => Close("已断开连接");
}
