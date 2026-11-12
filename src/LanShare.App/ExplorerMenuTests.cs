using System.Net;
using System.Net.Sockets;
using LanShare.Core;
using Microsoft.Win32;

namespace LanShare.App;

// 在独立注册表根验证路径、设备菜单和移除操作，不修改用户现有的资源管理器菜单。
internal static class ExplorerMenuTests
{
    // 两个同名设备应有不同的指纹命令，撤销配对后旧动词也应移除。
    internal static void CheckRegistry()
    {
        string branch = @"Software\SimpleLanShare\Tests\" + Guid.NewGuid().ToString("N");
        try
        {
            using var root = Registry.CurrentUser.CreateSubKey(branch);
            string a = new('A', 64), b = new('B', 64);
            var names = new Dictionary<string, string> { [a] = "工作电脑", [b] = "工作电脑" };
            string launcher = StartupRegistration.BuildLauncher(@"C:\带 空格\局域共享.exe");
            ExplorerMenu.Update(root, launcher, launcher, true, [a, b], names);
            using var menu = root.OpenSubKey(@"*\shell\SimpleLanShare.Send")!;
            string targets = (string)menu.GetValue("ExtendedSubCommandsKey")! + @"\shell";
            foreach (string id in new[] { a, b })
            {
                using var command = root.OpenSubKey(targets + "\\" + id + @"\command");
                if (!Equals(command?.GetValue(""), launcher + " --send-file " + id + " \"%1\""))
                    throw new Exception("Invalid Explorer file command");
            }
            foreach (string context in new[] { @"Directory\Background", "DesktopBackground" })
            {
                using var command = root.OpenSubKey(context + @"\shell\SimpleLanShare.Open\command");
                if (!Equals(command?.GetValue(""), launcher + " --open-share-folder"))
                    throw new Exception("Missing Explorer background command");
            }
            ExplorerMenu.Update(root, launcher, launcher, true, [], names);
            using var remaining = root.OpenSubKey(targets)!;
            if (!remaining.GetSubKeyNames().SequenceEqual(["Pair"])) throw new Exception("Stale Explorer targets");
            ExplorerMenu.Update(root, launcher, launcher, false, [], names);
            using var removed = root.OpenSubKey(@"*\shell\SimpleLanShare.Send");
            if (removed is not null) throw new Exception("Explorer send menu not removed");
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(branch, false); }
    }
}

// 通过真实命名管道和 TLS 会话执行右键发送，验证目标选择独立于主界面下拉框。
internal sealed partial class MainForm
{
    // 创建两个隔离的回环接收端，发送后逐文件核对内容和其他接收端目录。
    internal async Task TestExplorerSendAsync(string root)
    {
        if (!smoke) throw new InvalidOperationException();
        Hide();
        string dir = Path.Combine(root, "explorer-" + Guid.NewGuid().ToString("N"));
        var target = Directory.CreateDirectory(Path.Combine(dir, "B"));
        var other = Directory.CreateDirectory(Path.Combine(dir, "C"));
        using var idB = Security.CreateCertificate();
        using var idC = Security.CreateCertificate();
        using var a = new PeerNetwork(identity, dir, [Security.Fingerprint(idB), Security.Fingerprint(idC)], port: 0);
        int portB = FreeLoopbackPort();
        using var b = new PeerNetwork(idB, target.FullName, [a.NodeId], portB);
        b.Start(false);
        int portC = FreeLoopbackPort();
        using var c = new PeerNetwork(idC, other.FullName, [a.NodeId], portC);
        c.Start(false);
        b.PeerConnected += p => p.ConfirmFileAsync = (_, _) => Task.FromResult(true);
        c.PeerConnected += p => p.ConfirmFileAsync = (_, _) => Task.FromResult(true);
        network = a; settings.TrustedDeviceIds = [b.NodeId, c.NodeId];
        a.Start(false);
        string pipe = "SimpleLanShare.Test." + Guid.NewGuid().ToString("N");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task listener = ExplorerSendPipe.ListenAsync(pipe, EnqueueExplorerSend, timeout.Token);
        try
        {
            await a.ConnectAsync("127.0.0.1", portB, timeout.Token);
            await a.ConnectAsync("127.0.0.1", portC, timeout.Token);
            string[] names = ["测试 A & B.txt", "第二个 文件.txt"];
            foreach (string name in names) File.WriteAllText(Path.Combine(dir, name), name);
            await Task.WhenAll(names.Select(name => ExplorerSendPipe.ForwardAsync(pipe, new(b.NodeId, Path.Combine(dir, name)))));
            while (!names.All(name => File.Exists(Path.Combine(target.FullName, name))))
                await Task.Delay(20, timeout.Token);
            await sends.WaitAsync(timeout.Token); sends.Release();
            if (Visible || Directory.GetFiles(other.FullName).Length != 0 ||
                names.Any(name => File.ReadAllText(Path.Combine(target.FullName, name)) != name))
                throw new Exception("Explorer transfer did not preserve target, file contents or tray state");
        }
        finally
        {
            timeout.Cancel();
            try { await listener; } catch (OperationCanceledException) { }
            network = null; settings.TrustedDeviceIds.Clear();
        }
    }

    // 仅分配测试监听端口，不使用实际应用的端口或自动发现。
    private static int FreeLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
