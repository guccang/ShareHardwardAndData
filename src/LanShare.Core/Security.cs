using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace LanShare.Core;

// 每次监听生成临时证书；配对密钥证明绑定该证书和双端随机数，防止中间人替换证书。
public static class Security
{
    // 128 位随机配对码只在内存使用，应用不保存明文密钥。
    public static string NewPairingCode() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    // 允许用户粘贴带空格或连字符的配对码，拒绝弱口令。
    public static byte[] ParseCode(string code)
    {
        string value = code.Replace(" ", "").Replace("-", "").Trim();
        if (value.Length != 32 || !value.All(Uri.IsHexDigit))
            throw new ArgumentException("配对码应为 32 位十六进制字符，请从等待连接的一端复制。");
        return Convert.FromHexString(value);
    }

    // 证书仅服务于本次进程内的加密连接，不写入系统证书库。
    public static X509Certificate2 CreateCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=SimpleLanShare", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(7));
        // Schannel 不支持纯内存临时私钥，通过 PFX 导入为随证书释放的用户临时密钥。
        byte[] pfx = generated.Export(X509ContentType.Pfx);
        try { return new X509Certificate2(pfx, (string?)null, X509KeyStorageFlags.UserKeySet); }
        finally { CryptographicOperations.ZeroMemory(pfx); }
    }

    // 服务端先协商 TLS，再验证对方持有配对码；认证前不接收任何业务数据。
    public static async Task<SslStream> AcceptAsync(Stream transport, X509Certificate2 certificate, byte[] key, CancellationToken ct)
    {
        var ssl = new SslStream(transport, false);
        try
        {
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = certificate,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                ClientCertificateRequired = false
            }, ct);
            byte[] serverNonce = RandomNumberGenerator.GetBytes(32);
            byte[] certificateHash = SHA256.HashData(certificate.RawData);
            await Wire.SendJsonAsync(ssl, new Challenge(Convert.ToHexString(serverNonce)), ct);
            var proof = await Wire.ReadJsonAsync<Proof>(ssl, ct);
            byte[] clientNonce = Decode(proof.Nonce, 32);
            Verify(proof.Mac, Mac(key, "client", certificateHash, serverNonce, clientNonce));
            await Wire.SendJsonAsync(ssl, new Proof("", Convert.ToHexString(Mac(key, "server", certificateHash, serverNonce, clientNonce))), ct);
            return ssl;
        }
        catch { ssl.Dispose(); throw; }
    }

    // 自签名证书仅用于启动 TLS；只有证书绑定的密钥证明通过，调用者才获得连接。
    public static async Task<SslStream> ConnectAsync(Stream transport, byte[] key, CancellationToken ct)
    {
        var ssl = new SslStream(transport, false, (_, _, _, _) => true);
        try
        {
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "SimpleLanShare", EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
            }, ct);
            byte[] certificateHash = SHA256.HashData(ssl.RemoteCertificate!.GetRawCertData());
            var challenge = await Wire.ReadJsonAsync<Challenge>(ssl, ct);
            byte[] serverNonce = Decode(challenge.Nonce, 32);
            byte[] clientNonce = RandomNumberGenerator.GetBytes(32);
            await Wire.SendJsonAsync(ssl, new Proof(Convert.ToHexString(clientNonce), Convert.ToHexString(Mac(key, "client", certificateHash, serverNonce, clientNonce))), ct);
            var proof = await Wire.ReadJsonAsync<Proof>(ssl, ct);
            Verify(proof.Mac, Mac(key, "server", certificateHash, serverNonce, clientNonce));
            return ssl;
        }
        catch { ssl.Dispose(); throw; }
    }

    // 固定长度的各段与角色标签共同参与证明，避免反射与跨角色重放。
    private static byte[] Mac(byte[] key, string role, byte[] certificate, byte[] server, byte[] client)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes("SimpleLanShare/v1/" + role).Concat(certificate).Concat(server).Concat(client).ToArray());
    }

    // 使用固定耗时比较，不向错误信息暴露配对密钥或证明。
    private static void Verify(string actual, byte[] expected)
    {
        if (!CryptographicOperations.FixedTimeEquals(Decode(actual, 32), expected))
            throw new AuthenticationException("配对码不一致，连接被拒绝。");
    }

    // 严格验证随机数和证明长度，阻止畸形握手。
    private static byte[] Decode(string hex, int size)
    {
        if (hex is null || hex.Length != size * 2 || !hex.All(Uri.IsHexDigit)) throw new AuthenticationException("无效认证消息。");
        return Convert.FromHexString(hex);
    }

    private sealed record Challenge(string Nonce);
    private sealed record Proof(string Nonce, string Mac);
}
