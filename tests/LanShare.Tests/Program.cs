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
            InputTests.Run(Check);
            TestInputQueue();
            TestWheelBurst();
            await PairingTestsAsync(root);
            await LegacyInputNegotiationAsync(root);
            await ThreePeerTestsAsync(root);
            await DiscoveryTestsAsync(root);
            Console.WriteLine($"PASS: {assertions} assertions; framing, authentication, three peers, clipboard, files, cancellation, shared input.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { Directory.Delete(root, true); }
    }

    // 模拟界面繁忙期间的高频移动、双击与输入，验证位置合并、事件屏障和背压。
    private static void TestInputQueue()
    {
        var callbacks = new Queue<Action>();
        var received = new List<InputPacket>();
        using var inbox = new DesktopInputQueue(callbacks.Enqueue, received.Add, () => throw new Exception("UI dispatch failed"));
        var lease = Guid.NewGuid();
        for (int i = 0; i < 1000; i++) inbox.Add(new(InputOp.Move, lease, i, 20));
        foreach (int down in new[] { 1, 0, 1, 0 }) inbox.Add(new(InputOp.Button, lease, 1, down));
        inbox.Add(new(InputOp.Move, lease, 40, 40)); inbox.Add(new(InputOp.Key, lease, 65, 30));
        Check(callbacks.Count == 1, "one UI wakeup for burst of mouse and keyboard input");
        while (callbacks.TryDequeue(out var callback)) callback();
        Check(received.Count == 7 && received[0].A == 999 && received[5].A == 40 &&
            received.Skip(1).Take(4).Select(p => p.B).SequenceEqual([1, 0, 1, 0]) && received[6].Op == InputOp.Key,
            "latest position arrives before double click and keyboard");
        Task ready = Task.CompletedTask;
        for (int i = 0; i < 128; i++) ready = inbox.Add(new(InputOp.Key, lease, 65, 30));
        Check(!ready.IsCompleted, "full UI queue applies backpressure");
        while (callbacks.TryDequeue(out var callback)) callback();
        Check(ready.IsCompletedSuccessfully && received.Count == 135, "drain resumes reader without losing a key");
    }

    // 模拟输入线程短暂繁忙后收到的连续小幅滚动，检查滚动量、方向和操作边界。
    private static void TestWheelBurst()
    {
        var callbacks = new Queue<Action>();
        var received = new List<InputPacket>();
        using var inbox = new DesktopInputQueue(callbacks.Enqueue, received.Add, () => throw new Exception("Wheel dispatch failed"));
        using var sender = new InputLink(Stream.Null);
        var tiny = new InputPacket(InputOp.Wheel, Guid.NewGuid(), -1);
        for (int i = 0; i < 967; i++)
        {
            if (!sender.TrySend(tiny)) throw new Exception("Fine wheel input filled the sender queue");
            if (!inbox.Add(tiny).IsCompletedSuccessfully) throw new Exception("Fine wheel input stalled the receiver");
        }
        InputPacket[] barriers = [tiny with { A = 3 }, tiny with { A = 6, B = 1 },
            tiny with { A = 6, B = 1, Lease = Guid.NewGuid() }, new(InputOp.Key, tiny.Lease, 17, 29),
            tiny, new(InputOp.Move, tiny.Lease, 40, 50), tiny, new(InputOp.Button, tiny.Lease, 1, 1),
            tiny, tiny with { A = -120 }, tiny with { A = -120 }];
        foreach (var packet in barriers) inbox.Add(packet);
        while (callbacks.TryDequeue(out var callback)) callback();
        var expected = Enumerable.Repeat(tiny with { A = -120 }, 8).Append(tiny with { A = -7 }).Concat(barriers);
        Check(received.SequenceEqual(expected), "967 fine wheel deltas drain in 9 events with exact distance, barriers and detents preserved");
        received.Clear();
        inbox.Add(tiny);
        while (callbacks.TryDequeue(out var callback)) callback();
        Check(received.SequenceEqual([tiny]), "a single partial wheel delta is dispatched without waiting for another");
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
        Check(!Security.IsFingerprint("123456"), "invalid device fingerprint rejected");
        using var identity = Security.CreateCertificate();
        Check(Security.IsFingerprint(Security.Fingerprint(identity)), "certificate fingerprint identity");
        Throws<InvalidDataException>(() => PeerSession.ValidateClipboard("html", 5), "unknown clipboard format");
        Throws<InvalidDataException>(() => PeerSession.ValidateClipboard("text", PeerSession.MaxTextBytes + 1L), "text size limit");
        Throws<InvalidDataException>(() => PeerSession.ValidateClipboard("png", PeerSession.MaxImageBytes + 1L), "image size limit");
    }

    // 真实双向 TLS 验证确认前隔离、双方同意、持久信任、冒充拒绝及取消。
    private static async Task PairingTestsAsync(string root)
    {
        using var identityA = Security.CreateCertificate(); using var identityB = Security.CreateCertificate();
        int portA = FreePort(), portB = FreePort(); while (portA == portB) portB = FreePort();
        using var a = new PeerNetwork(identityA, root, port: portA);
        using var b = new PeerNetwork(identityB, root, port: portB);
        var promptA = new TaskCompletionSource<PairingRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var promptB = new TaskCompletionSource<PairingRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var decisionA = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var decisionB = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        a.ConfirmPeerAsync = (request, ct) => { promptA.TrySetResult(request); return decisionA.Task.WaitAsync(ct); };
        b.ConfirmPeerAsync = (request, ct) => { promptB.TrySetResult(request); return decisionB.Task.WaitAsync(ct); };
        a.Start(false); b.Start(false);
        var connect = a.ConnectAsync("127.0.0.1", portB);
        var requestA = await promptA.Task.WaitAsync(TimeSpan.FromSeconds(8));
        var requestB = await promptB.Task.WaitAsync(TimeSpan.FromSeconds(8));
        Check(requestA.VerificationCode == requestB.VerificationCode && requestA.VerificationCode.Length == 6, "matching six-digit verification code on both ends");
        Check(a.Sessions.Count == 0 && b.Sessions.Count == 0 && a.TrustedIds.Count == 0 && b.TrustedIds.Count == 0, "no data session or trust before confirmation");
        decisionA.SetResult(true); await Task.Delay(150);
        Check(a.Sessions.Count == 0 && b.Sessions.Count == 0, "one-sided approval cannot enable sharing");
        decisionB.SetResult(true); await connect.WaitAsync(TimeSpan.FromSeconds(8));
        await UntilAsync(() => a.Sessions.Count == 1 && b.Sessions.Count == 1);
        Check(a.TrustedIds.Contains(b.NodeId) && b.TrustedIds.Contains(a.NodeId), "mutual approval remembers certificate identities");
        string[] trustA = a.TrustedIds.ToArray(), trustB = b.TrustedIds.ToArray();
        a.Dispose(); b.Dispose();
        using var restoredA = new PeerNetwork(identityA, root, trustA, portA);
        using var restoredB = new PeerNetwork(identityB, root, trustB, portB);
        int prompts = 0;
        restoredA.ConfirmPeerAsync = (_, _) => { Interlocked.Increment(ref prompts); return Task.FromResult(false); };
        restoredB.ConfirmPeerAsync = (_, _) => { Interlocked.Increment(ref prompts); return Task.FromResult(false); };
        restoredA.Start(false); restoredB.Start(false);
        await restoredA.ConnectAsync("127.0.0.1", portB);
        await UntilAsync(() => restoredB.Sessions.Count == 1);
        Check(prompts == 0 && restoredA.Sessions.Count == 1, "persisted trust reconnects without prompts");
        var inputEvent = new TaskCompletionSource<InputPacket>(TaskCreationOptions.RunContinuationsAsynchronously);
        var inputSender = restoredA.Sessions.Single().Input!;
        restoredB.Sessions.Single().Input!.Received = packet => { inputEvent.TrySetResult(packet); return Task.CompletedTask; };
        var wheel = new InputPacket(InputOp.Wheel, Guid.NewGuid(), -120, 1);
        Check(inputSender.TrySend(wheel) && await inputEvent.Task.WaitAsync(TimeSpan.FromSeconds(5)) == wheel, "independent certificate-bound input stream roundtrip");

        // 通过真实 TLS 连续发送超过一个写批次的整刻度，核对解帧顺序及队列末尾的按键。
        var burst = Enumerable.Range(0, 96).Select(i => wheel with { A = i % 2 == 0 ? 120 : -120, B = i % 3 == 0 ? 1 : 0 })
            .Append(new(InputOp.Key, wheel.Lease, 17, 29)).ToArray();
        var delivered = new List<InputPacket>();
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        restoredB.Sessions.Single().Input!.Received = packet =>
        {
            delivered.Add(packet);
            if (delivered.Count == burst.Length) finished.SetResult();
            return Task.CompletedTask;
        };
        foreach (var packet in burst)
            if (!inputSender.TrySend(packet)) throw new Exception("Wheel burst could not be queued");
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(delivered.SequenceEqual(burst), "real TLS delivers full detents across batches and the following key in order");

        using var strangerIdentity = Security.CreateCertificate();
        using var stranger = new PeerNetwork(strangerIdentity, root, port: FreePort());
        stranger.ConfirmPeerAsync = (_, _) => Task.FromResult(true);
        await ThrowsAsync<PairingRejectedException>(() => stranger.ConnectAsync("127.0.0.1", portB), "new certificate needs approval even with same machine name");
        Check(prompts == 1 && stranger.TrustedIds.Count == 0 && restoredB.Sessions.Count == 1, "rejection stores no trust and preserves other sessions");
        await ThrowsAsync<PairingRejectedException>(() => stranger.ConnectAsync("127.0.0.1", portB), "rejected peer remains blocked");
        Check(prompts == 1, "rejected peer does not repeat confirmation prompt");
        restoredB.RetryPairing();
        restoredB.ConfirmPeerAsync = (_, _) => Task.FromResult(true);
        await stranger.ConnectAsync("127.0.0.1", portB);
        await UntilAsync(() => restoredB.Sessions.Count == 2);
        Check(stranger.Sessions.Count == 1, "explicit retry can pair a previously refused device");

        await ThrowsAsync<InvalidDataException>(async () =>
        {
            using var mismatch = await Connector.ConnectAsync("127.0.0.1", portB, strangerIdentity, root,
                (_, _) => throw new Exception("Mismatched discovery must not prompt"), default, new string('0', 64));
        }, "discovery identity is pinned to TLS certificate");
        using var attacker = Security.CreateCertificate();
        using (var tcp = new TcpClient())
        {
            await tcp.ConnectAsync(IPAddress.Loopback, portB);
            using var tls = await Security.ConnectAsync(tcp.GetStream(), attacker, default);
            await Wire.SendJsonAsync(tls, new Hello("control", "", Environment.MachineName, NodeId: restoredA.NodeId), default);
            await ThrowsAsync<IOException>(() => Wire.ReadJsonAsync<Ready>(tls, default), "claimed trusted identity cannot override real certificate");
        }
        using (var tcp = new TcpClient())
        {
            await tcp.ConnectAsync(IPAddress.Loopback, portB);
            using var tls = await Security.ConnectAsync(tcp.GetStream(), attacker, default);
            await Wire.SendJsonAsync(tls, new Hello("bulk", new string('0',64), Environment.MachineName, NodeId: Security.Fingerprint(attacker)), default);
            await ThrowsAsync<IOException>(() => Wire.ReadJsonAsync<Ready>(tls, default), "unconfirmed bulk channel rejected");
        }

        using var refusingIdentity = Security.CreateCertificate();
        int refusalPort = FreePort();
        using var refusing = new PeerNetwork(refusingIdentity, root, port: refusalPort);
        var remoteCancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        refusing.ConfirmPeerAsync = (_, _) => Task.FromResult(false); refusing.Start(false);
        await ThrowsAsync<PairingRejectedException>(async () =>
        {
            using var notConnected = await Connector.ConnectAsync("127.0.0.1", refusalPort, attacker, root, async (_, ct) =>
            {
                try { await Task.Delay(Timeout.Infinite, ct); return true; }
                finally { remoteCancelled.TrySetResult(true); }
            }, default);
        }, "peer rejection closes a still-pending local confirmation");
        Check(await remoteCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5)), "pending dialog cancellation is delivered immediately");
        using var cancelA = new PeerNetwork(attacker, root, port: FreePort());
        int cancelPort = FreePort();
        // 独立等待方只开启一台监听，避免取消验证影响已经建立的连接。
        using var waiting = new PeerNetwork(identityB, root, port: cancelPort);
        var asked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        waiting.ConfirmPeerAsync = async (_, ct) => { asked.TrySetResult(true); await Task.Delay(Timeout.Infinite, ct); return false; };
        cancelA.ConfirmPeerAsync = (_, _) => Task.FromResult(true);
        waiting.Start(false);
        using var cancellation = new CancellationTokenSource();
        var cancelled = cancelA.ConnectAsync("127.0.0.1", cancelPort, cancellation.Token);
        await asked.Task.WaitAsync(TimeSpan.FromSeconds(8)); cancellation.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => cancelled, "cancelling pending pairing closes the handshake");
        Check(cancelA.TrustedIds.Count == 0 && waiting.TrustedIds.Count == 0 && waiting.Sessions.Count == 0, "cancelled pairing has no trust or data access");
    }
    // 旧版握手不含键鼠能力字段，仍可只建立原来的两条 TLS 通道。
    private static async Task LegacyInputNegotiationAsync(string root)
    {
        using var serverId = Security.CreateCertificate(); using var oldId = Security.CreateCertificate();
        string oldFingerprint = Security.Fingerprint(oldId);
        int port = FreePort();
        using var server = new PeerNetwork(serverId, root, [oldFingerprint], port);
        server.Start(false);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = timeout.Token;
        using var controlClient = new TcpClient(); await controlClient.ConnectAsync(IPAddress.Loopback, port, ct);
        using var control = await Security.ConnectAsync(controlClient.GetStream(), oldId, ct);
        await Wire.SendJsonAsync(control, new { Channel = "control", Token = "", Name = "旧版电脑", Version = 2, NodeId = oldFingerprint }, ct);
        var ready = await Wire.ReadJsonAsync<Ready>(control, ct);
        await Security.ConfirmAsync(control, new(server.NodeId, ready.Name, "127.0.0.1", ""), (_, _) => Task.FromResult(true), ct);
        Check((await Wire.ReadJsonAsync<Message>(control, ct)).Kind == "paired" && ready.InputVersion == 0, "old handshake negotiates data sharing only");
        using var bulkClient = new TcpClient(); await bulkClient.ConnectAsync(IPAddress.Loopback, port, ct);
        using var bulk = await Security.ConnectAsync(bulkClient.GetStream(), oldId, ct);
        await Wire.SendJsonAsync(bulk, new { Channel = "bulk", ready.Token, Name = "旧版电脑", Version = 2, NodeId = oldFingerprint }, ct);
        await Wire.ReadJsonAsync<Ready>(bulk, ct);
        using var old = new PeerSession(control, bulk, ready.Name, root, server.NodeId);
        old.Start(); await UntilAsync(() => server.Sessions.Count == 1);
        var peer = server.Sessions.Single(); var clip = NextClipboard(peer);
        await old.SendClipboardAsync(new("text", [65]), ct);
        Check(peer.Input == null && (await clip.WaitAsync(ct)).Data.SequenceEqual(new byte[] { 65 }), "older peer shares clipboard without opening an input stream");
    }

    // 三个实例模拟三台电脑，使用回环网络的真实加密连接进行双向验证。
    private static async Task ThreePeerTestsAsync(string root)
    {
        using var identityA = Security.CreateCertificate();
        using var identityB = Security.CreateCertificate();
        int[] ports = [FreePort(), FreePort(), FreePort()];
        while (ports.Distinct().Count() != 3) ports = [FreePort(), FreePort(), FreePort()];
        string aDir = Path.Combine(root, "A"), bDir = Path.Combine(root, "B"), cDir = Path.Combine(root, "C");
        Directory.CreateDirectory(aDir); Directory.CreateDirectory(bDir); Directory.CreateDirectory(cDir);
        using var identityC = Security.CreateCertificate();
        using var a = new PeerNetwork(identityA, aDir, port: ports[0]);
        using var b = new PeerNetwork(identityB, bDir, port: ports[1]);
        using var c = new PeerNetwork(identityC, cDir, port: ports[2]);
        foreach (var peerNetwork in new[] { a, b, c })
        {
            peerNetwork.ConfirmPeerAsync = (_, _) => Task.FromResult(true);
            peerNetwork.PeerConnected += peer => peer.ConfirmFileAsync = (_, _) => Task.FromResult(true);
        }
        a.Status += text => Console.WriteLine("A: " + text); b.Status += text => Console.WriteLine("B: " + text); c.Status += text => Console.WriteLine("C: " + text);
        a.Start(false); b.Start(false); c.Start(false);
        await a.ConnectAsync("127.0.0.1", ports[1]);
        await a.ConnectAsync("127.0.0.1", ports[2]);
        await b.ConnectAsync("127.0.0.1", ports[2]);
        await UntilAsync(() => a.Sessions.Count == 2 && b.Sessions.Count == 2 && c.Sessions.Count == 2);
        Check(true, "three-node mesh connected");
        var ab = a.Sessions.Single(s => s.PeerId == b.NodeId);
        var ac = a.Sessions.Single(s => s.PeerId == c.NodeId);
        var ba = b.Sessions.Single(s => s.PeerId == a.NodeId);
        var ca = c.Sessions.Single(s => s.PeerId == a.NodeId);
        var bc = b.Sessions.Single(s => s.PeerId == c.NodeId);
        var cb = c.Sessions.Single(s => s.PeerId == b.NodeId);
        var atB = NextClipboard(ba); var atC = NextClipboard(ca);
        var layout = DesktopLayout.Edit(null, a.NodeId, [c.NodeId, a.NodeId, b.NodeId]);
        var atLayoutB = new TaskCompletionSource<DesktopLayout>(TaskCreationOptions.RunContinuationsAsynchronously);
        ba.LayoutReceived += received => atLayoutB.TrySetResult(received);
        await ab.SendLayoutAsync(layout);
        var receivedLayout = await atLayoutB.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var reachedC = new TaskCompletionSource<DesktopLayout>(TaskCreationOptions.RunContinuationsAsynchronously);
        cb.LayoutReceived += received => reachedC.TrySetResult(received);
        await bc.SendLayoutAsync(receivedLayout);
        Check((await reachedC.Task.WaitAsync(TimeSpan.FromSeconds(5))).Order.SequenceEqual(layout.Order), "third paired peer receives relayed arrangement");
        Check(receivedLayout.Order.SequenceEqual(layout.Order) && receivedLayout.Author == a.NodeId, "layout order survives paired TLS transport");
        Check(receivedLayout.ForKnownDevices([b.NodeId, a.NodeId, c.NodeId]).SequenceEqual(layout.Order), "paired computers project the same arrangement");
        Check(receivedLayout.ForKnownDevices([b.NodeId, a.NodeId]).SequenceEqual([a.NodeId, b.NodeId]), "layout cannot pair an unknown device");
        var next = DesktopLayout.Edit(receivedLayout, b.NodeId, [b.NodeId, a.NodeId]);
        Check(next.Order.SequenceEqual([c.NodeId, b.NodeId, a.NodeId]) && next.IsNewerThan(layout) && !layout.IsNewerThan(next), "offline member survives an edit and stale revisions are ignored");
        var concurrent = next with { Author = a.NodeId };
        Check(next.IsNewerThan(concurrent) != concurrent.IsNewerThan(next) && !next.IsNewerThan(next), "concurrent edits converge without echo");
        Check(!(next with { Order = [a.NodeId, a.NodeId] }).IsValid(), "duplicate devices cannot form a layout");
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
        var receipts = await Task.WhenAll(ab.SendFileWithReceiptAsync(source), ac.SendFileWithReceiptAsync(source));
        Check(receipts.All(value => value == Convert.ToHexString(SHA256.HashData(payload))), "each delivery receipt matches sent file bytes");
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
        var duringInput = new TaskCompletionSource<InputPacket>(TaskCreationOptions.RunContinuationsAsynchronously);
        ba.Input!.Received = p => { duringInput.TrySetResult(p); return Task.CompletedTask; };
        var key = new InputPacket(InputOp.Key, Guid.NewGuid(), 65, 30);
        Check(ab.Input!.TrySend(key) && await duringInput.Task.WaitAsync(TimeSpan.FromSeconds(5)) == key, "input bypasses blocked file channel");
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
        var caughtUp = new TaskCompletionSource<DesktopLayout>(TaskCreationOptions.RunContinuationsAsynchronously);
        reAB.LayoutReceived += received => caughtUp.TrySetResult(received);
        await reBA.SendLayoutAsync(next);
        var reconnectedLayout = await caughtUp.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(reconnectedLayout.IsNewerThan(layout) && reconnectedLayout.Order.SequenceEqual(next.Order), "reconnected peer can adopt the latest persisted layout");
        reBA.Progress += progress => { if (!progress.Sending && progress.Completed == 0) reBA.Close("interrupt after acceptance"); };
        await ThrowsAsync<Exception>(() => reAB.SendFileAsync(source), "disconnect during file stream");
        await UntilAsync(() => !Directory.EnumerateFiles(bDir, "*.partial").Any());
        Check(File.Exists(bPath), "completed file survives interrupted new transfer");
        a.Dispose(); b.Dispose(); c.Dispose();
        await UntilAsync(() => a.Sessions.Count == 0 && b.Sessions.Count == 0 && c.Sessions.Count == 0);
    }

    // 向隔离 UDP 端口注入发现报文，验证新版发现自动触发已确认的连接。
    private static async Task DiscoveryTestsAsync(string root)
    {
        using var identityA = Security.CreateCertificate();
        using var identityB = Security.CreateCertificate();
        int portA = FreePort(), portB = FreePort(), discovery = FreePort();
        while (new[] { portA, portB, discovery }.Distinct().Count() != 3) { portA = FreePort(); portB = FreePort(); discovery = FreePort(); }
        using var a = new PeerNetwork(identityA, root, port: portA, discoveryPort: discovery);
        using var b = new PeerNetwork(identityB, root, port: portB, discoveryPort: discovery);
        var lower = string.CompareOrdinal(a.NodeId, b.NodeId) < 0 ? a : b;
        var upper = lower == a ? b : a;
        int upperPort = upper == a ? portA : portB;
        lower.Status += text => Console.WriteLine("DISCOVERY: " + text);
        upper.Status += text => Console.WriteLine("UPPER: " + text);
        a.ConfirmPeerAsync = (_, _) => Task.FromResult(true); b.ConfirmPeerAsync = (_, _) => Task.FromResult(true);
        upper.Start(false); lower.Start();
        using var udp = new UdpClient();
        byte[] WrongPacket() => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { Protocol = "SimpleLanShare/1", NodeId = upper.NodeId, Port = upperPort });
        await udp.SendAsync(WrongPacket(), new IPEndPoint(IPAddress.Loopback, discovery));
        await Task.Delay(250);
        Check(lower.Sessions.Count == 0, "legacy discovery protocol ignored");
        byte[] packet = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
        {
            Protocol = "SimpleLanShare/2", NodeId = upper.NodeId, Port = upperPort
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
