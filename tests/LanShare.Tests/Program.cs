using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using LanShare.Core;

// 不依赖第三方测试框架，运行真实 TCP/TLS 会话验证协议和传输行为。
internal static class Program
{
    private static int assertions;

    // 测试在独立临时目录运行，结束时只清理本次创建的路径。
    private static async Task<int> Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "LanShareTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await ProtocolTestsAsync();
            await ThreePeerTestsAsync(root);
            await DiscoveryTestsAsync(root);
            Console.WriteLine($"PASS: {assertions} assertions; framing, authentication, three peers, clipboard, files, cancellation.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { Directory.Delete(root, true); }
    }

    // 覆盖畸形帧、长度上限、危险文件名及配对码解析。
    private static async Task ProtocolTestsAsync()
    {
        using var stream = new MemoryStream();
        await Wire.SendJsonAsync(stream, new Message("hello", Text: "中文"), default);
        stream.Position = 0;
        Check((await Wire.ReadJsonAsync<Message>(stream, default)).Text == "中文", "UTF-8 JSON roundtrip");
        byte[] invalid = new byte[5]; invalid[0] = Wire.Blob;
        BinaryPrimitives.WriteInt32BigEndian(invalid.AsSpan(1), int.MaxValue);
        await ThrowsAsync<InvalidDataException>(() => Wire.ReadAsync(new MemoryStream(invalid), default), "oversize rejected before allocation");
        BinaryPrimitives.WriteInt32BigEndian(invalid.AsSpan(1), -1);
        await ThrowsAsync<InvalidDataException>(() => Wire.ReadAsync(new MemoryStream(invalid), default), "negative length");
        await ThrowsAsync<EndOfStreamException>(() => Wire.ReadAsync(new MemoryStream([1, 0]), default), "truncated header");
        foreach (string name in new[] { "../a", "..\\a", "C:\\x", "x:stream", "CON.txt", "nul", "LPT1.log", "bad.", "bad ", "a/b", "", "a\0b" })
            Throws<InvalidDataException>(() => SafeFiles.ValidateName(name), "unsafe file name " + name);
        SafeFiles.ValidateName("报告 2026.png"); Check(true, "unicode file name");
        Throws<ArgumentException>(() => Security.ParseCode("123456"), "weak pairing code");
        Check(Security.ParseCode(Security.NewPairingCode()).Length == 16, "128-bit pairing code");
        Throws<InvalidDataException>(() => PeerSession.ValidateClipboard("html", 5), "unknown clipboard format");
        Throws<InvalidDataException>(() => PeerSession.ValidateClipboard("text", PeerSession.MaxTextBytes + 1L), "text size limit");
        Throws<InvalidDataException>(() => PeerSession.ValidateClipboard("png", PeerSession.MaxImageBytes + 1L), "image size limit");
    }

    // 三个实例模拟三台电脑，使用回环网络的真实加密连接进行双向验证。
    private static async Task ThreePeerTestsAsync(string root)
    {
        string group = Security.NewPairingCode();
        int[] ports = [FreePort(), FreePort(), FreePort()];
        while (ports.Distinct().Count() != 3) ports = [FreePort(), FreePort(), FreePort()];
        string aDir = Path.Combine(root, "A"), bDir = Path.Combine(root, "B"), cDir = Path.Combine(root, "C");
        Directory.CreateDirectory(aDir); Directory.CreateDirectory(bDir); Directory.CreateDirectory(cDir);
        using var a = new PeerNetwork(group, aDir, ports[0]);
        using var b = new PeerNetwork(group, bDir, ports[1]);
        using var c = new PeerNetwork(group, cDir, ports[2]);
        foreach (var peerNetwork in new[] { a, b, c }) peerNetwork.PeerConnected += peer => peer.ConfirmFileAsync = (_, _) => Task.FromResult(true);
        a.Status += text => Console.WriteLine("A: " + text); b.Status += text => Console.WriteLine("B: " + text); c.Status += text => Console.WriteLine("C: " + text);
        a.Start(false); b.Start(false); c.Start(false);
        await a.ConnectAsync("127.0.0.1", ports[1]);
        await a.ConnectAsync("127.0.0.1", ports[2]);
        await b.ConnectAsync("127.0.0.1", ports[2]);
        await UntilAsync(() => a.Sessions.Count == 2 && b.Sessions.Count == 2 && c.Sessions.Count == 2);
        Check(true, "three-node mesh connected");
        await ThrowsAsync<Exception>(async () =>
        {
            using var invalid = await Connector.ConnectAsync("127.0.0.1", ports[0], Security.NewPairingCode(), root, default);
        }, "wrong pairing code rejected");
        Check(a.Sessions.Count == 2, "wrong code does not disconnect valid peers");

        var ab = a.Sessions.Single(s => s.PeerId == b.NodeId);
        var ac = a.Sessions.Single(s => s.PeerId == c.NodeId);
        var ba = b.Sessions.Single(s => s.PeerId == a.NodeId);
        var ca = c.Sessions.Single(s => s.PeerId == a.NodeId);
        var bc = b.Sessions.Single(s => s.PeerId == c.NodeId);
        var cb = c.Sessions.Single(s => s.PeerId == b.NodeId);
        var atB = NextClipboard(ba); var atC = NextClipboard(ca);
        byte[] text = Encoding.UTF8.GetBytes("多电脑剪贴板\r\nHello 🌏");
        await Task.WhenAll(ab.SendClipboardAsync(new("text", text)), ac.SendClipboardAsync(new("text", text)));
        Check((await atB.WaitAsync(TimeSpan.FromSeconds(5))).Data.SequenceEqual(text), "clipboard A to B");
        Check((await atC.WaitAsync(TimeSpan.FromSeconds(5))).Data.SequenceEqual(text), "clipboard A to C");
        byte[] png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aWQAAAABJRU5ErkJggg==");
        var imageAtB = NextClipboard(bc);
        await cb.SendClipboardAsync(new("png", png));
        var image = await imageAtB.WaitAsync(TimeSpan.FromSeconds(5));
        Check(image.Format == "png" && image.Data.SequenceEqual(png), "binary PNG C to B");

        string source = Path.Combine(root, "文件.bin"); byte[] payload = RandomNumberGenerator.GetBytes(3 * 1024 * 1024 + 27);
        await File.WriteAllBytesAsync(source, payload);
        var bFile = NextFile(ba); var cFile = NextFile(ca);
        await Task.WhenAll(ab.SendFileAsync(source), ac.SendFileAsync(source));
        string bPath = await bFile.WaitAsync(TimeSpan.FromSeconds(5)), cPath = await cFile.WaitAsync(TimeSpan.FromSeconds(5));
        Check(SHA256.HashData(await File.ReadAllBytesAsync(bPath)).SequenceEqual(SHA256.HashData(payload)), "streamed file A to B hash");
        Check((await File.ReadAllBytesAsync(cPath)).SequenceEqual(payload), "streamed file A to C hash");
        var duplicate = NextFile(ba); await ab.SendFileAsync(source);
        string duplicatePath = await duplicate.WaitAsync(TimeSpan.FromSeconds(5));
        Check(duplicatePath != bPath && File.Exists(bPath), "duplicate filename never overwrites");
        string empty = Path.Combine(root, "empty.txt"); await File.WriteAllBytesAsync(empty, []);
        var emptyAtA = NextFile(ab); await ba.SendFileAsync(empty);
        Check(new FileInfo(await emptyAtA.WaitAsync(TimeSpan.FromSeconds(5))).Length == 0, "zero-byte reverse transfer");

        ba.ConfirmFileAsync = (_, _) => Task.FromResult(false);
        await ThrowsAsync<IOException>(() => ab.SendFileAsync(source), "receive rejection");
        Check(ab.IsConnected && ba.IsConnected, "rejection keeps session usable");
        ba.ConfirmFileAsync = (_, _) => Task.FromResult(true);
        var afterReject = NextClipboard(ba); await ab.SendClipboardAsync(new("text", text));
        Check((await afterReject.WaitAsync(TimeSpan.FromSeconds(5))).Data.SequenceEqual(text), "protocol aligned after rejection");

        var accepting = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ba.ConfirmFileAsync = async (_, ct) => { accepting.TrySetResult(true); await Task.Delay(Timeout.Infinite, ct); return true; };
        var interrupted = ab.SendFileAsync(source);
        await accepting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var duringFile = NextClipboard(ba);
        await ab.SendClipboardAsync(new("text", text)).WaitAsync(TimeSpan.FromSeconds(5));
        Check((await duringFile.WaitAsync(TimeSpan.FromSeconds(5))).Data.SequenceEqual(text), "clipboard bypasses blocked file channel");
        ab.Close("test cancellation");
        await ThrowsAsync<Exception>(() => interrupted, "disconnect cancels pending receive decision");
        await UntilAsync(() => a.Sessions.Count == 1 && b.Sessions.Count == 1);
        Check(!Directory.EnumerateFiles(bDir, "*.partial").Any(), "no partial file after cancellation");
        await a.ConnectAsync("127.0.0.1", ports[1]);
        await UntilAsync(() => a.Sessions.Count == 2 && b.Sessions.Count == 2);
        Check(true, "reconnect after disconnection");

        // 中途关闭数据通道，必须清理已创建的临时文件并保留原有完成文件。
        var reAB = a.Sessions.Single(s => s.PeerId == b.NodeId);
        var reBA = b.Sessions.Single(s => s.PeerId == a.NodeId);
        reBA.Progress += progress => { if (!progress.Sending && progress.Completed == 0) reBA.Close("interrupt after acceptance"); };
        await ThrowsAsync<Exception>(() => reAB.SendFileAsync(source), "disconnect during file stream");
        await UntilAsync(() => !Directory.EnumerateFiles(bDir, "*.partial").Any());
        Check(File.Exists(bPath), "completed file survives interrupted new transfer");
        a.Dispose(); b.Dispose(); c.Dispose();
        await UntilAsync(() => a.Sessions.Count == 0 && b.Sessions.Count == 0 && c.Sessions.Count == 0);
    }

    // 向隔离 UDP 端口注入发现报文，验证同组自动建连与不同组隔离。
    private static async Task DiscoveryTestsAsync(string root)
    {
        string group = Security.NewPairingCode();
        int portA = FreePort(), portB = FreePort(), discovery = FreePort();
        while (new[] { portA, portB, discovery }.Distinct().Count() != 3) { portA = FreePort(); portB = FreePort(); discovery = FreePort(); }
        using var a = new PeerNetwork(group, root, portA, discovery);
        using var b = new PeerNetwork(group, root, portB, discovery);
        var lower = string.CompareOrdinal(a.NodeId, b.NodeId) < 0 ? a : b;
        var upper = lower == a ? b : a;
        int upperPort = upper == a ? portA : portB;
        lower.Status += text => Console.WriteLine("DISCOVERY: " + text);
        upper.Status += text => Console.WriteLine("UPPER: " + text);
        upper.Start(false); lower.Start();
        using var udp = new UdpClient();
        byte[] WrongPacket() => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { Protocol = "SimpleLanShare/1", Group = "wrong", NodeId = upper.NodeId, Port = upperPort });
        await udp.SendAsync(WrongPacket(), new IPEndPoint(IPAddress.Loopback, discovery));
        await Task.Delay(250);
        Check(lower.Sessions.Count == 0, "different discovery group ignored");
        byte[] packet = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
        {
            Protocol = "SimpleLanShare/1", Group = Convert.ToHexString(SHA256.HashData(Security.ParseCode(group))), NodeId = upper.NodeId, Port = upperPort
        });
        // UDP 允许丢包，测试按实际广播行为重发，而不假设单个报文必达。
        for (int retry = 0; retry < 12 && (lower.Sessions.Count != 1 || upper.Sessions.Count != 1); retry++)
        {
            await udp.SendAsync(packet, new IPEndPoint(IPAddress.Loopback, discovery));
            await Task.Delay(500);
        }
        await UntilAsync(() => lower.Sessions.Count == 1 && upper.Sessions.Count == 1);
        Check(true, "UDP discovery creates authenticated connection");
        await udp.SendAsync(packet, new IPEndPoint(IPAddress.Loopback, discovery));
        await Task.Delay(250);
        Check(lower.Sessions.Count == 1 && upper.Sessions.Count == 1, "repeated discovery does not duplicate peers");
    }
    // 事件只订阅一次，避免后续传输满足前一次测试的等待条件。
    private static Task<ClipboardContent> NextClipboard(PeerSession peer)
    {
        var completion = new TaskCompletionSource<ClipboardContent>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(ClipboardContent content) { peer.ClipboardReceived -= Handler; completion.TrySetResult(content); }
        peer.ClipboardReceived += Handler; return completion.Task;
    }

    // 等待实际落盘完成事件，而非只依据发送端写入成功。
    private static Task<string> NextFile(PeerSession peer)
    {
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(string path) { peer.FileReceived -= Handler; completion.TrySetResult(path); }
        peer.FileReceived += Handler; return completion.Task;
    }

    // 获取本机临时空闲端口，避免测试使用正式产品端口。
    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port;
    }

    // 网络事件异步交付，使用有截止时间的条件等待替代固定长休眠。
    private static async Task UntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!predicate()) await Task.Delay(20, timeout.Token);
    }

    // 断言失败保留具体场景名称。
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception("FAIL: " + label);
        assertions++; Console.WriteLine("  OK " + label);
    }

    // 同步错误路径断言。
    private static void Throws<T>(Action action, string label) where T : Exception
    {
        try { action(); } catch (T) { Check(true, label); return; }
        throw new Exception("FAIL: expected " + typeof(T).Name + " for " + label);
    }

    // 异步错误路径断言。
    private static async Task ThrowsAsync<T>(Func<Task> action, string label) where T : Exception
    {
        try { await action().WaitAsync(TimeSpan.FromSeconds(15)); } catch (T ex) when (ex is not TimeoutException) { Check(true, label); return; }
        throw new Exception("FAIL: expected " + typeof(T).Name + " for " + label);
    }
}
