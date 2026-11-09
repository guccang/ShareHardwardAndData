using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace LanShare.Core;

// 共享组采用直连网状连接，每台电脑同时监听；无需固定服务器或云账号。
public sealed class PeerNetwork : IDisposable
{
    public const int DiscoveryPort = 42657;
    private readonly CancellationTokenSource stop = new();
    private readonly TcpListener listener;
    private readonly X509Certificate2 certificate;
    private readonly byte[] key;
    private readonly string code, folder, groupTag;
    private readonly int port, discoveryPort;
    private readonly ConcurrentDictionary<string, Pending> pending = new();
    private readonly ConcurrentDictionary<string, PeerSession> sessions = new();
    private readonly ConcurrentDictionary<string, byte> dialing = new();
    private readonly SemaphoreSlim handshakes = new(12);
    private UdpClient? discovery;
    private int disposed, activeHandlers;
    private readonly object lifecycle = new();
    private bool credentialsDisposed;
    public string NodeId { get; } = Guid.NewGuid().ToString("N");
    public IReadOnlyCollection<PeerSession> Sessions => sessions.Values.ToArray();
    public event Action<PeerSession>? PeerConnected;
    public event Action<PeerSession, string>? PeerDisconnected;
    public event Action<string>? Status;

    // 共享码只保留在当前共享组生命周期中；自动发现只公开不可逆的组标记。
    public PeerNetwork(string code, string folder, int port = Connector.DefaultPort, int discoveryPort = DiscoveryPort)
    {
        this.code = code; this.folder = folder; this.port = port; this.discoveryPort = discoveryPort;
        key = Security.ParseCode(code);
        groupTag = Convert.ToHexString(SHA256.HashData(key));
        certificate = Security.CreateCertificate();
        listener = new TcpListener(IPAddress.Any, port);
    }

    // TCP 为必需，UDP 广播不可用时仍可手动输入 IP 连接。
    public void Start(bool autoDiscovery = true)
    {
        listener.Start(16);
        _ = Task.Run(AcceptLoopAsync);
        if (!autoDiscovery) return;
        try
        {
            discovery = new UdpClient(new IPEndPoint(IPAddress.Any, discoveryPort)) { EnableBroadcast = true };
            _ = Task.Run(DiscoverLoopAsync);
            _ = Task.Run(AnnounceLoopAsync);
        }
        catch (SocketException ex) { Status?.Invoke("自动发现不可用，可手动连接：" + ex.Message); }
    }

    // 手动地址与发现地址共用连接入口，按远端身份去除重复连接。
    public async Task ConnectAsync(string host, int remotePort = Connector.DefaultPort, CancellationToken ct = default)
    {
        if (sessions.Count >= 16) throw new IOException("简化版最多同时连接 16 台其他电脑。");
        string addressKey = host + ":" + remotePort;
        if (!dialing.TryAdd(addressKey, 0)) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, stop.Token);
        try
        {
            var peer = await Connector.ConnectAsync(host, remotePort, code, folder, linked.Token, NodeId);
            Register(peer);
        }
        finally { dialing.TryRemove(addressKey, out _); }
    }

    // 限制并发未认证握手，避免陌生设备耗尽连接资源。
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
                    if (stop.IsCancellationRequested) { client.Dispose(); handshakes.Release(); break; }
                    activeHandlers++;
                }
                _ = HandleClientAsync(client);
            }
        }
        catch (Exception ex) when (!stop.IsCancellationRequested) { Status?.Invoke("监听失败：" + ex.Message); }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (SocketException) when (stop.IsCancellationRequested) { }
    }

    // 已认证的数据连接必须持有待建立控制连接的随机令牌和相同节点身份。
    private async Task HandleClientAsync(TcpClient client)
    {
        SslStream? stream = null;
        bool handedOff = false;
        string? pendingToken = null;
        Pending? waiting = null;
        try
        {
            client.NoDelay = true;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            stream = await Security.AcceptAsync(client.GetStream(), certificate, key, timeout.Token);
            var hello = await Wire.ReadJsonAsync<Hello>(stream, timeout.Token);
            if (hello.Version != 1 || string.IsNullOrWhiteSpace(hello.Name) || hello.Name.Length > 64 ||
                !Guid.TryParseExact(hello.NodeId, "N", out _) || hello.NodeId == NodeId)
                throw new InvalidDataException("无效的设备身份。");
            if (hello.Channel == "bulk")
            {
                if (!pending.TryGetValue(hello.Token, out var requested) || requested.NodeId != hello.NodeId)
                    throw new InvalidDataException("无效的数据通道。");
                await Wire.SendJsonAsync(stream, requested.Ready, timeout.Token);
                if (!requested.Bulk.TrySetResult(stream)) throw new InvalidDataException("重复数据通道。");
                stream = null; handedOff = true;
                return;
            }
            if (hello.Channel != "control" || hello.Token != "" || sessions.Count >= 16 || sessions.ContainsKey(hello.NodeId))
                throw new InvalidDataException("设备已连接或连接数达到上限。");
            pendingToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var ready = new Ready(pendingToken, Environment.MachineName, NodeId: NodeId);
            waiting = new Pending(hello.NodeId, ready);
            pending[pendingToken] = waiting;
            await Wire.SendJsonAsync(stream, ready, timeout.Token);
            var bulk = await waiting.Bulk.Task.WaitAsync(timeout.Token);
            var peer = new PeerSession(stream, bulk, hello.Name, folder, hello.NodeId);
            stream = null;
            Register(peer); handedOff = true;
        }
        catch (Exception ex) when (!stop.IsCancellationRequested)
        {
            Status?.Invoke("连接未完成：" + ex.Message + (ex.InnerException is null ? "" : " · " + ex.InnerException.Message));
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (IOException) when (stop.IsCancellationRequested) { }
        finally
        {
            if (pendingToken is not null) pending.TryRemove(pendingToken, out _);
            // 等待结束与数据通道抵达竞态时，回收无人接管的流。
            if (waiting is not null && stream is not null)
            {
                if (!waiting.Bulk.TrySetCanceled() && waiting.Bulk.Task.IsCompletedSuccessfully) waiting.Bulk.Task.Result.Dispose();
            }
            if (!handedOff) { stream?.Dispose(); client.Dispose(); }
            handshakes.Release();
            lock (lifecycle) { activeHandlers--; ReleaseCredentials(); }
        }
    }

    // 连接注册先通知界面订阅事件，再启动数据循环。
    private void Register(PeerSession peer)
    {
        if (stop.IsCancellationRequested || !sessions.TryAdd(peer.PeerId, peer)) { peer.Dispose(); return; }
        peer.Closed += reason =>
        {
            if (sessions.TryRemove(new KeyValuePair<string, PeerSession>(peer.PeerId, peer))) PeerDisconnected?.Invoke(peer, reason);
        };
        PeerConnected?.Invoke(peer);
        peer.Start();
    }

    // 各网卡分别广播，兼容有线、Wi-Fi 及多网卡环境；广播中没有剪贴板或文件内容。
    private async Task AnnounceLoopAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
            do
            {
                byte[] data = JsonSerializer.SerializeToUtf8Bytes(new Advertisement("SimpleLanShare/1", groupTag, NodeId, port));
                foreach (var address in BroadcastAddresses())
                {
                    try { await discovery!.SendAsync(data, new IPEndPoint(address, discoveryPort), stop.Token); }
                    catch (SocketException) { }
                }
            } while (await timer.WaitForNextTickAsync(stop.Token));
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex) { if (!stop.IsCancellationRequested) Status?.Invoke("广播停止：" + ex.Message); }
    }

    // 仅节点 ID 较小的一端主动连接，避免自动发现产生两条相反方向的连接。
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
                if (advertised is null || advertised.Protocol != "SimpleLanShare/1" || advertised.Group != groupTag ||
                    !Guid.TryParseExact(advertised.NodeId, "N", out _) || advertised.Port is < 1 or > 65535 ||
                    string.CompareOrdinal(NodeId, advertised.NodeId) >= 0 || sessions.ContainsKey(advertised.NodeId)) continue;
                _ = TryDiscoverConnectAsync(packet.RemoteEndPoint.Address.ToString(), advertised.Port);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex) { if (!stop.IsCancellationRequested) Status?.Invoke("自动发现停止：" + ex.Message); }
    }

    // 自动发现失败允许下一轮广播重试，手动连接则直接向界面返回错误。
    private async Task TryDiscoverConnectAsync(string host, int remotePort)
    {
        try { await ConnectAsync(host, remotePort); }
        catch (Exception ex) when (!stop.IsCancellationRequested) { Status?.Invoke("自动连接失败：" + ex.Message); }
        catch (OperationCanceledException) { }
    }

    // 从活动 IPv4 网卡计算定向广播地址，同时发送通用广播。
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

    // 停止共享时关闭监听、发现与所有已认证会话。
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stop.Cancel(); listener.Stop(); discovery?.Dispose();
        foreach (var peer in sessions.Values) peer.Dispose();
        lock (lifecycle) ReleaseCredentials();
    }

    // 等待所有使用证书的握手退出后释放临时用户密钥。
    private void ReleaseCredentials()
    {
        if (disposed == 0 || activeHandlers != 0 || credentialsDisposed) return;
        credentialsDisposed = true; certificate.Dispose(); CryptographicOperations.ZeroMemory(key);
    }

    private sealed record Advertisement(string Protocol, string Group, string NodeId, int Port);
    private sealed record Pending(string NodeId, Ready Ready)
    {
        public TaskCompletionSource<SslStream> Bulk { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
