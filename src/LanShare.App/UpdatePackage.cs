using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LanShare.App;

// 页面和更新比较共用发布二进制内嵌的版本与提交号。
internal static class BuildIdentity
{
    internal static readonly string Info = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
    internal static readonly Version Version = Version.Parse(Info.Split('+')[0]);
    internal static readonly string Commit = Info.Split('+').ElementAtOrDefault(1) ?? "未知";
    internal static string ShortCommit => Commit[..Math.Min(12, Commit.Length)];
}

// 只有固定发布公钥签名的清单可指定新版；所有输出文件名均由本程序决定。
internal static class UpdatePackage
{
    internal const string Exe = "SimpleLanShare.exe", Guide = "使用说明.md", Manifest = "update.json", Signature = "update.sig";
    internal static readonly string[] Files = [Guide, Manifest, Signature, Exe];
    internal const string StagePrefix = ".SimpleLanShare-update-";
    private const int ExeLimit = 220 * 1024 * 1024, GuideLimit = 1024 * 1024;
    internal sealed record Release(string Product, string Platform, string Version, string Commit, string ExeSha256, string GuideSha256);

    // 兼容接收目录发生重名时追加的序号，普通压缩包不参与更新。
    internal static bool IsCandidate(string path) => Regex.IsMatch(Path.GetFileName(path),
        @"\ASimpleLanShare-\d+\.\d+\.\d+-win-x64(?: \(\d+\))?\.zip\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // 在原安装目录的独立子目录中解包；先验证签名，再解压固定成员，不采用 ZIP 内的路径。
    internal static string? Prepare(string zipPath, string installDir, Version installed) =>
        Prepare(zipPath, installDir, installed, null)?.Directory;

    // 在同一个只读文件句柄上核对发送摘要、验证签名和解包；返回清单中的真实版本。
    internal static (string Directory, Version Version)? Prepare(string zipPath, string installDir, Version installed, string? sentDigest)
    {
        using var source = File.OpenRead(zipPath);
        if (source.Length > ExeLimit + 2L * GuideLimit) throw new InvalidDataException("更新包过大。");
        if (sentDigest is not null)
        {
            if (!Convert.ToHexString(SHA256.HashData(source)).Equals(sentDigest, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("更新包在发送后已变化。");
            source.Position = 0;
        }
        using var zip = new ZipArchive(source, ZipArchiveMode.Read);
        if (zip.Entries.Count != Files.Length || !Files.All(n => zip.GetEntry(n) is not null))
            throw new InvalidDataException("更新包文件列表不完整或含有额外文件。");
        byte[] manifest = ReadEntry(zip.GetEntry(Manifest)!, 8192);
        byte[] signature = ReadEntry(zip.GetEntry(Signature)!, 1024);
        var release = Authenticate(manifest, signature);
        var version = Version.Parse(release.Version);
        if (version <= installed) return null;

        string stage = Directory.CreateDirectory(Path.Combine(installDir, StagePrefix + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            File.WriteAllBytes(Path.Combine(stage, Manifest), manifest);
            File.WriteAllBytes(Path.Combine(stage, Signature), signature);
            foreach (string name in new[] { Exe, Guide })
            {
                using var output = new FileStream(Path.Combine(stage, name), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                CopyEntry(zip.GetEntry(name)!, output, name == Exe ? ExeLimit : GuideLimit);
            }
            VerifyPayload(stage, release);
            return (stage, version);
        }
        catch { Cleanup(stage); throw; }
    }

    // 助手接手前独立校验磁盘上的清单、程序和说明文件。
    internal static Release VerifyStage(string stage)
    {
        var release = Authenticate(File.ReadAllBytes(Path.Combine(stage, Manifest)), File.ReadAllBytes(Path.Combine(stage, Signature)));
        VerifyPayload(stage, release);
        return release;
    }

    // 清单签名使用编入程序的公钥验证，包内数据不能更换受信发布者。
    private static Release Authenticate(byte[] data, byte[] signature)
    {
        using var resource = typeof(UpdatePackage).Assembly.GetManifestResourceStream("LanShare.UpdatePublisher")!;
        using var certBytes = new MemoryStream();
        resource.CopyTo(certBytes);
        using var cert = new X509Certificate2(certBytes.ToArray());
        using var rsa = cert.GetRSAPublicKey()!;
        if (!rsa.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            throw new InvalidDataException("发布签名校验失败。");
        var release = JsonSerializer.Deserialize<Release>(data) ?? throw new InvalidDataException("缺少发布清单。");
        if (release.Product != "SimpleLanShare" || release.Platform != "win-x64" ||
            !Version.TryParse(release.Version, out var version) || version.Build < 0 || version.Revision != -1 ||
            release.Commit is not { Length: 40 } || !release.Commit.All(Uri.IsHexDigit))
            throw new InvalidDataException("发布信息无效。");
        return release;
    }

    // 二进制自身的版本与 Git 提交也必须和签名清单一致。
    private static void VerifyPayload(string stage, Release release)
    {
        foreach (var (name, hash) in new[] { (Exe, release.ExeSha256), (Guide, release.GuideSha256) })
        {
            using var file = File.OpenRead(Path.Combine(stage, name));
            if (!Convert.ToHexString(SHA256.HashData(file)).Equals(hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(name + " 的 SHA-256 校验失败。");
        }
        if (FileVersionInfo.GetVersionInfo(Path.Combine(stage, Exe)).ProductVersion != release.Version + "+" + release.Commit)
            throw new InvalidDataException("程序版本与发布清单不一致。");
    }

    // 小型元数据也受实际解压字节数限制。
    private static byte[] ReadEntry(ZipArchiveEntry entry, int limit)
    {
        using var bytes = new MemoryStream();
        CopyEntry(entry, bytes, limit);
        return bytes.ToArray();
    }

    // 同时限制 ZIP 声明和实际输出，拒绝截断、条目膨胀及无效长度。
    private static void CopyEntry(ZipArchiveEntry entry, Stream target, int limit)
    {
        if (entry.Length > limit) throw new InvalidDataException("更新文件过大。");
        using var input = entry.Open();
        byte[] buffer = new byte[65536];
        long total = 0;
        int count;
        while ((count = input.Read(buffer)) != 0)
        {
            total += count;
            if (total > entry.Length) throw new InvalidDataException("解压长度不符。");
            target.Write(buffer, 0, count);
        }
        if (total != entry.Length) throw new InvalidDataException("更新包不完整。");
    }

    // 只清理本次暂存目录内的已知文件，不递归删除安装目录或用户数据。
    internal static void Cleanup(string stage)
    {
        if (!IsStage(stage) || !Directory.Exists(stage) || File.GetAttributes(stage).HasFlag(FileAttributes.ReparsePoint)) return;
        foreach (var name in Files)
            foreach (var suffix in new[] { "", ".new", ".bak" }) DeleteStagedFile(Path.Combine(stage, name + suffix));
        Directory.Delete(stage);
    }

    // 刚启动新版后，Windows 有时仍短暂占用暂存 EXE；只重试访问冲突，超时则保留文件。
    private static void DeleteStagedFile(string path)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { File.Delete(path); return; }
            catch (Exception ex) when (attempt < 20 && (ex is IOException or UnauthorizedAccessException)
                && (ex.HResult & 0xffff) is 5 or 32 or 33)
            {
                Thread.Sleep(200);
            }
        }
    }

    // 暂存目录必须带固定前缀和独立的事务编号。
    internal static bool IsStage(string stage)
    {
        string name = Path.GetFileName(stage);
        return name.StartsWith(StagePrefix, StringComparison.Ordinal) && Guid.TryParseExact(name[StagePrefix.Length..], "N", out _);
    }
}
