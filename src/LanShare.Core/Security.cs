using System.Buffers.Binary;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace LanShare.Core;

// 每台设备拥有持久证书身份；TLS 证明密钥持有，首次连接由两端用户确认身份。
public static class Security
{
    // 证书不加入系统信任库；通过 PFX 导入兼容 Windows Schannel 的临时用户私钥。
    public static X509Certificate2 CreateCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=SimpleLanShare Device", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(10));
        byte[] pfx = generated.Export(X509ContentType.Pfx);
        try { return ImportCertificate(pfx); }
        finally { CryptographicOperations.ZeroMemory(pfx); }
    }

    // 调用者负责用 DPAPI 保存 PFX；不在磁盘上保存未加密私钥。
    public static X509Certificate2 ImportCertificate(byte[] pfx) => new(pfx, (string?)null, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);

    // 身份直接取证书指纹，名称与广播中声称的 ID 都不能冒充已信任设备。
    public static string Fingerprint(X509Certificate certificate) => Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));

    // 严格验证指纹格式，拒绝未知版本的设备标识。
    public static bool IsFingerprint(string? id) => id is { Length: 64 } && id.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');

    // 两端按相同顺序计算六位核对码，无需手工输入；证书不同则核对码随之改变。
    public static string VerificationCode(string first, string second)
    {
        string ordered = string.CompareOrdinal(first, second) < 0 ? first + second : second + first;
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes("SimpleLanShare/pair/v2/" + ordered));
        return (BinaryPrimitives.ReadUInt32BigEndian(digest) % 1_000_000).ToString("D6");
    }

    // 自签名证书在 TLS 阶段只验证持有私钥，业务授权必须在应用层确认后进行。
    public static async Task<SslStream> AcceptAsync(Stream transport, X509Certificate2 certificate, CancellationToken ct)
    {
        var ssl = new SslStream(transport, false, (_, remote, _, _) => remote is not null);
        try
        {
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = certificate, ClientCertificateRequired = true,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13, AllowRenegotiation = false
            }, ct);
            if (ssl.RemoteCertificate is null) throw new AuthenticationException("对方没有提供设备证书。");
            return ssl;
        }
        catch { ssl.Dispose(); throw; }
    }

    // 主动端同样出示本机证书，形成双向 TLS；首次连接随后等待双方确认。
    public static async Task<SslStream> ConnectAsync(Stream transport, X509Certificate2 certificate, CancellationToken ct)
    {
        var ssl = new SslStream(transport, false, (_, remote, _, _) => remote is not null);
        try
        {
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "SimpleLanShare", ClientCertificates = new X509CertificateCollection { certificate },
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13, AllowRenegotiation = false
            }, ct);
            if (ssl.RemoteCertificate is null) throw new AuthenticationException("对方没有提供设备证书。");
            return ssl;
        }
        catch { ssl.Dispose(); throw; }
    }

    // 必须双方都同意才继续创建数据通道；拒绝和超时都不会建立业务会话。
    public static async Task ConfirmAsync(SslStream stream, PairingRequest request,
        Func<PairingRequest, CancellationToken, Task<bool>> approve, CancellationToken ct)
    {
        using var decision = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var remote = Wire.ReadJsonAsync<Message>(stream, ct);
        var local = approve(request, decision.Token).WaitAsync(decision.Token);
        try
        {
            // 同时等待对端，任一方拒绝或断开时立即取消另一端仍显示的确认框。
            if (await Task.WhenAny(local, remote) == remote && (await remote).Kind != "approved")
            {
                await Wire.SendJsonAsync(stream, new Message("rejected"), ct);
                throw new PairingRejectedException("对方未确认连接。");
            }
            bool accepted = await local;
            await Wire.SendJsonAsync(stream, new Message(accepted ? "approved" : "rejected"), ct);
            if (!accepted)
            {
                // 等待对方读到拒绝并回执，避免关闭套接字时丢失拒绝消息。
                try { await remote.WaitAsync(TimeSpan.FromSeconds(3), ct); }
                catch (Exception ex) when (ex is IOException or TimeoutException) { }
                throw new PairingRejectedException("你已拒绝连接此电脑。");
            }
            if ((await remote).Kind != "approved") throw new PairingRejectedException("对方未确认连接。");
        }
        finally
        {
            decision.Cancel();
            // 另一方向仍在读时由调用者关闭流；观察最终异常，避免遗留后台故障。
            _ = remote.ContinueWith(task => { _ = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            _ = local.ContinueWith(task => { _ = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        }
    }
}

// 确认界面所需的设备信息全部来自已建立的 TLS 连接。
public sealed record PairingRequest(string PeerId, string Name, string Address, string VerificationCode);

// 与网络故障区别开，拒绝后停止自动重复弹窗，直到用户主动重试。
public sealed class PairingRejectedException(string message) : AuthenticationException(message);
