using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace LanShare.Core;

// 主动连接单个设备；共享组使用同一认证方式建立多台设备之间的直接连接。
public static class Connector
{
    public const int DefaultPort = 42656;

    // 两条连接分别认证，并通过服务端随机令牌绑定为同一会话。
    public static async Task<PeerSession> ConnectAsync(string host, int port, string code, string folder, CancellationToken ct, string nodeId = "")
    {
        if (nodeId.Length == 0) nodeId = Guid.NewGuid().ToString("N");
        byte[] key = Security.ParseCode(code);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        SslStream? control = null, bulk = null;
        try
        {
            control = await OpenAsync(host, port, key, timeout.Token);
            await Wire.SendJsonAsync(control, new Hello("control", "", Environment.MachineName, NodeId: nodeId), timeout.Token);
            var ready = await Wire.ReadJsonAsync<Ready>(control, timeout.Token);
            if (ready.Version != 1 || string.IsNullOrWhiteSpace(ready.Name) || ready.Name.Length > 64 ||
                ready.Token is null || ready.Token.Length != 64 || !ready.Token.All(Uri.IsHexDigit) ||
                !Guid.TryParseExact(ready.NodeId, "N", out _) || ready.NodeId == nodeId)
                throw new InvalidDataException("无效连接响应或连接到了本机。");
            bulk = await OpenAsync(host, port, key, timeout.Token);
            await Wire.SendJsonAsync(bulk, new Hello("bulk", ready.Token, Environment.MachineName, NodeId: nodeId), timeout.Token);
            var bulkReady = await Wire.ReadJsonAsync<Ready>(bulk, timeout.Token);
            if (bulkReady != ready) throw new InvalidDataException("数据通道与控制通道不匹配。");
            return new PeerSession(control, bulk, ready.Name, folder, ready.NodeId);
        }
        catch { control?.Dispose(); bulk?.Dispose(); throw; }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    // 连接失败时回收套接字，成功后由加密流负责套接字生命周期。
    private static async Task<SslStream> OpenAsync(string host, int port, byte[] key, CancellationToken ct)
    {
        var client = new TcpClient { NoDelay = true };
        try
        {
            await client.ConnectAsync(host, port, ct);
            return await Security.ConnectAsync(client.GetStream(), key, ct);
        }
        catch { client.Dispose(); throw; }
    }
}
