using System.Diagnostics;
using System.IO.Compression;

namespace LanShare.App;

// 用真实发布包和临时安装目录验证升级、签名拒绝、版本比较和启动失败回滚。
internal static class UpdateTests
{
    // 仅在自检参数下运行。本机正在使用的安装目录和用户配置不参加测试。
    internal static void Run(string package, string report)
    {
        var lines = new List<string>();
        string root = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(report))!,
            "update-test-" + Guid.NewGuid().ToString("N"))).FullName;
        string target = Path.Combine(root, UpdatePackage.Exe);
        File.WriteAllText(target, "old executable");
        File.WriteAllText(Path.Combine(root, UpdatePackage.Guide), "old guide");
        File.WriteAllText(Path.Combine(root, "用户文件.txt"), "preserved");
        if (UpdatePackage.Prepare(package, root, BuildIdentity.Version) is not null ||
            UpdatePackage.Prepare(package, root, new Version(999, 0, 0)) is not null)
            throw new Exception("Same/older package was accepted");
        lines.Add("PASS: same-version and downgrade packages leave installation untouched");
        string sentDigest;
        using (var stream = File.OpenRead(package))
            sentDigest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
        try
        {
            UpdatePackage.Prepare(package, root, new Version(0, 0, 0), new string('0', 64));
            throw new Exception("Sender accepted a different package at the same path");
        }
        catch (InvalidDataException) { }
        string stage = UpdatePackage.Prepare(package, root, new Version(0, 0, 0), sentDigest)!.Value.Directory;
        lines.Add("PASS: sender stages the acknowledged signed package and rejects mismatched transfer hashes");
        var signed = UpdatePackage.VerifyStage(stage);
        if (signed.Version != BuildIdentity.Version.ToString() || signed.Commit != BuildIdentity.Commit)
            throw new Exception("Signed metadata differs from running assembly");
        lines.Add("PASS: signature, payload hashes, embedded executable version and Git commit match");
        try
        {
            PortableUpdate.Install(stage, () => throw new IOException("simulated startup failure"));
            throw new Exception("Simulated launch failure not propagated");
        }
        catch (IOException ex) when (ex.Message == "simulated startup failure") { }
        if (File.ReadAllText(target) != "old executable" || File.ReadAllText(Path.Combine(root, UpdatePackage.Guide)) != "old guide"
            || File.Exists(Path.Combine(root, UpdatePackage.Manifest)) || File.Exists(Path.Combine(root, UpdatePackage.Signature)))
            throw new Exception("Installation rollback incomplete");
        lines.Add("PASS: failed startup restores original files and removes previously absent sidecars");
        PortableUpdate.Install(stage, () =>
        {
            if (FileVersionInfo.GetVersionInfo(target).ProductVersion != BuildIdentity.Info)
                throw new Exception("Installed EXE has incorrect version");
            PortableUpdate.StartAndCheck(target, stage, selfTest: true);
        });
        UpdatePackage.VerifyStage(root);
        if (File.ReadAllText(Path.Combine(root, "用户文件.txt")) != "preserved") throw new Exception("User file changed");
        UpdatePackage.Cleanup(stage);
        lines.Add("PASS: signed files installed in place, installed EXE acknowledged tray startup in isolation, user file preserved");

        foreach (string invalid in new[] { "signature", "payload", "path" })
        {
            string path = Path.Combine(root, invalid + ".zip");
            using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                foreach (string name in UpdatePackage.Files)
                {
                    using var output = archive.CreateEntry(name).Open();
                    if (name == UpdatePackage.Exe || (name == UpdatePackage.Signature && invalid == "signature"))
                        output.WriteByte(0);
                    else { using var input = File.OpenRead(Path.Combine(root, name)); input.CopyTo(output); }
                }
                if (invalid == "path") archive.CreateEntry("../outside");
            }
            try { UpdatePackage.Prepare(path, root, new Version(0, 0, 0)); throw new Exception("Invalid update accepted: " + invalid); }
            catch (InvalidDataException) { }
        }
        if (Directory.EnumerateDirectories(root, UpdatePackage.StagePrefix + "*").Any()) throw new Exception("Staging leaked");
        lines.Add("PASS: bad signature, changed payload and extra path entries rejected without staging leftovers");
        File.WriteAllLines(report, lines);
    }
}
