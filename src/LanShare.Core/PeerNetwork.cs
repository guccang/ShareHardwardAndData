using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace LanShare.Core;

// 自动发现运行本软件的设备；首次双方确认，之后依据持久证书指纹自动重连。
public sealed class PeerNetwork : IDisposable
{
    public const int DiscoveryPort = 42657;
    private readonly CancellationTokenSource stop = new();
    private readonly TcpListener listener;
    private readonly X509Certificate2 certificate;
    private readonly string folder;
    private readonly int port, discoveryPort;
    private readonly ConcurrentDictionary<string, Pending> pending = new();
    private readonly ConcurrentDictionary<string, PeerSession> sessions = new();
    private readonly ConcurrentDictionary<string, byte> dialing = new(), trusted = new(), denied = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<bool>>> approvals = new();
    private readonly SemaphoreSlim handshakes = new(12);
    private readonly object lifecycle = new();
    private UdpClient? discovery;
    private int disposed, activeHandlers;
    private long pairingGeneration;
    private bool credentialsDisposed;
    public string NodeId { get; }
    public IReadOnlyCollection<PeerSession> Sessions => sessions.Values.ToArray();
    public IReadOnlyCollection<string> TrustedIds => trusted.Keys.ToArray();
    public Func<PairingRequest, CancellationToken, Task<bool>>? ConfirmPeerAsync { get; set; }
    public event Action<PeerSession>? PeerConnected;
    public event Action<PeerSession, string>? PeerDisconnected;
    public event Action<string>? Status;

    // 复制证书句柄使网络独立管理其生命周期，已信任的指纹由本机配置提供。
    public PeerNetwork(X509Certificate2 identity, string folder, IEnumerable<string>? trustedIds = null,
        int port = Connector.DefaultPort, int discoveryPort = DiscoveryPort)
    {
        this.folder = folder; this.port = port; this.discoveryPort = discoveryPort;
        byte[] pfx = identity.Export(X509ContentType.Pfx);
        try { certificate = Security.ImportCertificate(pfx); }
        finally { CryptographicOperations.ZeroMemory(pfx); }
        NodeId = Security.Fingerprint(certificate);
        foreach (string id in trustedIds ?? []) if (Security.IsFingerprint(id)) trusted.TryAdd(id, 0);
        listener = new TcpListener(IPAddress.Any, port);
    }

    // 启用本地监听和每三秒一次的发现；广播受限时仍可输入 IP。
    public void Start(bool autoDiscovery = true)
    {
        listener.Start(16);
        _ = Task.Run(AcceptLoopAsync);
        if (!autoDiscovery) return;
        try
        {
            discovery = new UdpClient(new IPEndPoint(IPAddress.Any, discoveryPort)) { EnableBroadcast = true };
            _ = Task.Run(DiscoverLoopAsync); _ = Task.Run(AnnounceLoopAsync);
        }
        catch (SocketException ex) { Status?.Invoke("自动发现不可用，可手动连接：" + ex.Message); }
    }

    // 手动连接允许重新尝试已拒绝设备，自动连接则保持拒绝结果避免反复弹窗。
    public async Task ConnectAsync(string host, int remotePort = Connector.DefaultPort, CancellationToken ct = default, string? expectedId = null)
    {
        if (expectedId is null) RetryPairing();
        long generation = Interlocked.Read(ref pairingGeneration);
        if (sessions.Count >= 16) throw new IOException("最多同时连接 16 台其他电脑。");
        string dialKey = expectedId ?? host + ":" + remotePort;
        if (!dialing.TryAdd(dialKey, 0)) return;
        lock (lifecycle)
        {
            if (disposed != 0) { dialing.TryRemove(dialKey, out _); throw new OperationCanceledException(); }
            activeHandlers++;
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, stop.Token);
        try
        {
            var peer = await Connector.ConnectAsync(host, remotePort, certificate, folder, ApproveAsync, linked.Token, expectedId);
            Register(peer);
        }
        catch (PairingRejectedException) { if (expectedId is not null && generation == Interlocked.Read(ref pairingGeneration)) denied.TryAdd(expectedId, 0); throw; }
        finally
        {
            dialing.TryRemove(dialKey, out _);
            lock (lifecycle) { activeHandlers--; ReleaseCredentials(); }
        }
    }

    // 用户可显式重新发起被拒绝或超时的配对，不改变已信任设备。
    public void RetryPairing() { Interlocked.Increment(ref pairingGeneration); denied.Clear(); approvals.Clear(); }

    // 同一身份的并发连接共享一个确认结果，防止多个网卡触发重复确认窗。
    private Task<bool> ApproveAsync(PairingRequest request, CancellationToken ct)
    {
        if (trusted.ContainsKey(request.PeerId)) return Task.FromResult(true);
        if (denied.ContainsKey(request.PeerId)) return Task.FromResult(false);
        long generation = Interlocked.Read(ref pairingGeneration);
        return approvals.GetOrAdd(request.PeerId, _ => new Lazy<Task<bool>>(() => AskUserAsync(request, ct, generation))).Value;
    }

    // 没有确认回调时默认拒绝；超时、关闭和拒绝都不会自动信任。
    private async Task<bool> AskUserAsync(PairingRequest request, CancellationToken ct, long generation)
    {
        try
        {
            bool accepted = ConfirmPeerAsync is not null && await ConfirmPeerAsync(request, ct).WaitAsync(ct);
            if (!accepted && generation == Interlocked.Read(ref pairingGeneration)) denied.TryAdd(request.PeerId, 0);
            return accepted;
        }
        catch (OperationCanceledException) { if (generation == Interlocked.Read(ref pairingGeneration)) denied.TryAdd(request.PeerId, 0); throw; }
    }

    // 限制未经用户确认的并发连接，后台运行避免阻塞桌面消息循环。
    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stop.Token);
                if (!handshakes.Wait(0)) { client.Dispose(); continue; }
                lock (lifecycle)
                {
                    if (disposed != 0) { client.Dispose(); handshakes.Release(); break; }
                    activeHandlers++;
                }
                _ = HandleClientAsync(client);
            }
        }
        catch (Exception ex) { if (!stop.IsCancellationRequested) Status?.Invoke("监听失败：" + ex.Message); }
    }

    // 控制连接双方同意后才登记随机令牌；文件通道还必须出示同一设备证书。
    private async Task HandleClientAsync(TcpClient client)
    {
        long generation = Interlocked.Read(ref pairingGeneration);
        SslStream? stream = null;
        bool handedOff = false;
        string? pendingToken = null, peerId = null;
        Pending? waiting = null;
        try
        {
            client.NoDelay = true;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            stream = await Security.AcceptAsync(client.GetStream(), certificate, timeout.Token);
            var hello = await Wire.ReadJsonAsync<Hello>(stream, timeout.Token);
            peerId = Security.Fingerprint(stream.RemoteCertificate!);
            if (hello.Version != 2 || string.IsNullOrWhiteSpace(hello.Name) || hello.Name.Length > 64 || hello.NodeId != peerId || peerId == NodeId)
                throw new InvalidDataException("设备身份不匹配或版本不兼容。");
            if (hello.Channel is "bulk" or "input")
            {
                if (!pending.TryGetValue(hello.Token, out var requested) || requested.NodeId != peerId)
                    throw new InvalidDataException("文件通道未经过双方确认。");
                if (hello.Channel == "input" && requested.Ready.InputVersion != 1) throw new InvalidDataException("未协商键鼠通道。");
                var channel = hello.Channel == "input" ? requested.Input : requested.Bulk;
                await Wire.SendJsonAsync(stream, requested.Ready, timeout.Token);
                if (!channel.TrySetResult(stream)) throw new InvalidDataException("重复数据通道。");
                stream = null; handedOff = true; return;
            }
            if (hello.Channel != "control" || hello.Token != "" || sessions.Count >= 16 || sessions.ContainsKey(peerId))
                throw new InvalidDataException("设备已连接或连接数达到上限。");
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            pendingToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var ready = new Ready(pendingToken, Environment.MachineName, NodeId: NodeId, InputVersion: hello.InputVersion == 1 ? 1 : 0);
            await Wire.SendJsonAsync(stream, ready, timeout.Token);
            string address = ((IPEndPoint)client.Client.RemoteEndPoint!).Address.ToString();
            await Security.ConfirmAsync(stream, new(peerId, hello.Name, address, Security.VerificationCode(NodeId, peerId)), ApproveAsync, timeout.Token);
            waiting = new Pending(peerId, ready);
            pending[pendingToken] = waiting;
            await Wire.SendJsonAsync(stream, new Message("paired"), timeout.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            var bulk = await waiting.Bulk.Task.WaitAsync(timeout.Token);
            var input = ready.InputVersion == 1 ? await waiting.Input.Task.WaitAsync(timeout.Token) : null;
            var peer = new PeerSession(stream, bulk, hello.Name, folder, peerId, input);
            stream = null; Register(peer); handedOff = true;
        }
        catch (PairingRejectedException ex)
        {
            if (peerId is not null && generation == Interlocked.Read(ref pairingGeneration)) { denied.TryAdd(peerId, 0); approvals.TryRemove(peerId, out _); }
            if (!stop.IsCancellationRequested) Status?.Invoke(ex.Message);
        }
        catch (Exception ex) { if (!stop.IsCancellationRequested) Status?.Invoke(ex is OperationCanceledException ? "连接或确认已超时，可点击重新配对" : "连接未完成：" + ex.Message); }
        finally
        {
            if (pendingToken is not null) pending.TryRemove(pendingToken, out _);
            if (waiting is not null && stream is not null)
            {
                if (!waiting.Bulk.TrySetCanceled() && waiting.Bulk.Task.IsCompletedSuccessfully) waiting.Bulk.Task.Result.Dispose();
                if (!waiting.Input.TrySetCanceled() && waiting.Input.Task.IsCompletedSuccessfully) waiting.Input.Task.Result.Dispose();
            }
            if (!handedOff) { stream?.Dispose(); client.Dispose(); }
            handshakes.Release();
            lock (lifecycle) { activeHandlers--; ReleaseCredentials(); }
        }
    }

    // 只有完整连接成功才保存信任；确认被取消、对端拒绝和失败握手不会写入信任表。
    private void Register(PeerSession peer)
    {
        if (stop.IsCancellationRequested || !sessions.TryAdd(peer.PeerId, peer)) { peer.Dispose(); return; }
        trusted.TryAdd(peer.PeerId, 0); approvals.TryRemove(peer.PeerId, out _);
        peer.Closed += reason =>
        {
            if (sessions.TryRemove(new KeyValuePair<string, PeerSession>(peer.PeerId, peer))) PeerDisconnected?.Invoke(peer, reason);
        };
        PeerConnected?.Invoke(peer); peer.Start();
    }

    // 发现广播只包含协议、证书指纹与端口，没有文件和剪贴板正文。
    private async Task AnnounceLoopAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
            do
            {
                byte[] data = JsonSerializer.SerializeToUtf8Bytes(new Advertisement("SimpleLanShare/2", NodeId, port));
                foreach (var address in BroadcastAddresses())
                {
                    try { await discovery!.SendAsync(data, new IPEndPoint(address, discoveryPort), stop.Token); }
                    catch (SocketException) { }
                }
            } while (await timer.WaitForNextTickAsync(stop.Token));
        }
        catch (Exception ex) { if (!stop.IsCancellationRequested) Status?.Invoke("广播停止：" + ex.Message); }
    }

    // 身份较小的一方发起连接，阻止两边同时自动配对；身份必须与实际 TLS 证书吻合。
    private async Task DiscoverLoopAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var packet = await discovery!.ReceiveAsync(stop.Token);
                if (packet.Buffer.Length > 1024) continue;
                Advertisement? advertised;
                try { advertised = JsonSerializer.Deserialize<Advertisement>(packet.Buffer); }
                catch (JsonException) { continue; }
                if (advertised is null || advertised.Protocol != "SimpleLanShare/2" || !Security.IsFingerprint(advertised.NodeId) ||
                    advertised.Port is < 1 or > 65535 || string.CompareOrdinal(NodeId, advertised.NodeId) >= 0 ||
                    sessions.ContainsKey(advertised.NodeId) || denied.ContainsKey(advertised.NodeId)) continue;
                _ = TryDiscoverConnectAsync(packet.RemoteEndPoint.Address.ToString(), advertised.Port, advertised.NodeId);
            }
        }
        catch (Exception ex) { if (!stop.IsCancellationRequested) Status?.Invoke("自动发现停止：" + ex.Message); }
    }

    // 网络暂时不通时下一轮继续尝试；明确拒绝的设备在本轮共享期间不重复打扰。
    private async Task TryDiscoverConnectAsync(string host, int remotePort, string expectedId)
    {
        try { await ConnectAsync(host, remotePort, expectedId: expectedId); }
        catch (Exception ex) { if (!stop.IsCancellationRequested) Status?.Invoke("自动连接：" + ex.Message); }
    }

    // 为所有活动 IPv4 网卡计算广播地址，兼容有线与 Wi-Fi 并存。
    private static IEnumerable<IPAddress> BroadcastAddresses()
    {
        var addresses = new HashSet<IPAddress> { IPAddress.Broadcast };
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up))
        foreach (var unicast in adapter.GetIPProperties().UnicastAddresses)
        {
            if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(unicast.Address)) continue;
            byte[] ip = unicast.Address.GetAddressBytes(), mask = unicast.IPv4Mask.GetAddressBytes();
            addresses.Add(new IPAddress(ip.Zip(mask, (a, b) => (byte)(a | ~b)).ToArray()));
        }
        return addresses;
    }

    // 停止所有网络和确认等待，已信任设备由调用者保存在本机设置。
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stop.Cancel(); listener.Stop(); discovery?.Dispose();
        foreach (var peer in sessions.Values) peer.Dispose();
        lock (lifecycle) ReleaseCredentials();
    }

    // 等待握手结束后释放原生证书句柄，避免取消过程访问已释放密钥。
    private void ReleaseCredentials()
    {
        if (disposed == 0 || activeHandlers != 0 || credentialsDisposed) return;
        credentialsDisposed = true; certificate.Dispose();
    }

    private sealed record Advertisement(string Protocol, string NodeId, int Port);
    private sealed record Pending(string NodeId, Ready Ready)
    {
        public TaskCompletionSource<SslStream> Bulk { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<SslStream> Input { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
