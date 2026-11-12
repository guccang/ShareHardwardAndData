using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

namespace LanShare.Core;

// 主动端建立双向 TLS、等待两边确认，再用随机令牌绑定文件通道。
public static class Connector
{
    public const int DefaultPort = 42656;

    // 初次确认最多等待两分钟；可选预期指纹将自动发现和实际证书绑定。
    public static async Task<PeerSession> ConnectAsync(string host, int port, X509Certificate2 certificate, string folder,
        Func<PairingRequest, CancellationToken, Task<bool>> approve, CancellationToken ct, string? expectedId = null)
    {
        string nodeId = Security.Fingerprint(certificate);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        SslStream? control = null, bulk = null, input = null;
        try
        {
            control = await OpenAsync(host, port, certificate, timeout.Token);
            string remoteId = Security.Fingerprint(control.RemoteCertificate!);
            if (expectedId is not null && remoteId != expectedId) throw new InvalidDataException("对方证书与发现的设备不一致。");
            await Wire.SendJsonAsync(control, new Hello("control", "", Environment.MachineName, NodeId: nodeId, InputVersion: 1), timeout.Token);
            var ready = await Wire.ReadJsonAsync<Ready>(control, timeout.Token);
            if (ready.Version != 2 || string.IsNullOrWhiteSpace(ready.Name) || ready.Name.Length > 64 ||
                ready.Token is null || ready.Token.Length != 64 || !ready.Token.All(Uri.IsHexDigit) ||
                ready.NodeId != remoteId || ready.NodeId == nodeId)
                throw new InvalidDataException("无效连接响应或连接到了本机。");
            await Security.ConfirmAsync(control, new(remoteId, ready.Name, host, Security.VerificationCode(nodeId, remoteId)), approve, timeout.Token);
            var paired = await Wire.ReadJsonAsync<Message>(control, timeout.Token);
            if (paired.Kind != "paired") throw new InvalidDataException("配对尚未完成。");
            bulk = await OpenAsync(host, port, certificate, timeout.Token);
            if (Security.Fingerprint(bulk.RemoteCertificate!) != remoteId) throw new InvalidDataException("文件通道证书与已确认设备不同。");
            await Wire.SendJsonAsync(bulk, new Hello("bulk", ready.Token, Environment.MachineName, NodeId: nodeId), timeout.Token);
            var bulkReady = await Wire.ReadJsonAsync<Ready>(bulk, timeout.Token);
            if (bulkReady != ready) throw new InvalidDataException("数据通道与控制通道不匹配。");
            if (ready.InputVersion == 1)
            {
                input = await OpenAsync(host, port, certificate, timeout.Token);
                if (Security.Fingerprint(input.RemoteCertificate!) != remoteId) throw new InvalidDataException("键鼠通道证书不匹配。");
                await Wire.SendJsonAsync(input, new Hello("input", ready.Token, Environment.MachineName, NodeId: nodeId), timeout.Token);
                if (await Wire.ReadJsonAsync<Ready>(input, timeout.Token) != ready) throw new InvalidDataException("键鼠通道与配对会话不匹配。");
            }
            return new PeerSession(control, bulk, ready.Name, folder, ready.NodeId, input);
        }
        catch { input?.Dispose(); control?.Dispose(); bulk?.Dispose(); throw; }
    }

    // 套接字与 TLS 握手独立限制十秒，避免未响应主机占满确认等待时间。
    private static async Task<SslStream> OpenAsync(string host, int port, X509Certificate2 certificate, CancellationToken ct)
    {
        var client = new TcpClient { NoDelay = true };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await client.ConnectAsync(host, port, timeout.Token);
            return await Security.ConnectAsync(client.GetStream(), certificate, timeout.Token);
        }
        catch { client.Dispose(); throw; }
    }
}
