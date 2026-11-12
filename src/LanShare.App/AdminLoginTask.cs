using System.ComponentModel;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Xml.Linq;

namespace LanShare.App;

// 当前用户的交互式登录任务。只有设置页的显式操作才会申请 UAC 或修改任务。
internal static class AdminLoginTask
{
    private const string Source = "SimpleLanShare.AdminLogin";
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    // 使用 SID 区分同名用户、域用户和本机用户。
    private static string UserSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value ?? throw new InvalidOperationException("无法识别当前 Windows 账号。");
    }

    // 构造原生登录触发器。只使用已登录用户的桌面令牌，不保存账号密码。
    internal static string Definition(string sid, string exe)
    {
        XElement E(string name, object value) => new(Ns + name, value);
        return new XElement(Ns + "Task", new XAttribute("version", "1.2"),
            E("RegistrationInfo", E("Source", Source)),
            E("Triggers", E("LogonTrigger", E("UserId", sid))),
            E("Principals", new XElement(Ns + "Principal", new XAttribute("id", "User"),
                E("UserId", sid), E("LogonType", "InteractiveToken"), E("RunLevel", "HighestAvailable"))),
            new XElement(Ns + "Settings", E("MultipleInstancesPolicy", "IgnoreNew"),
                E("DisallowStartIfOnBatteries", false), E("StopIfGoingOnBatteries", false),
                E("ExecutionTimeLimit", "PT0S")),
            new XElement(Ns + "Actions", new XAttribute("Context", "User"),
                new XElement(Ns + "Exec", E("Command", exe), E("Arguments", "--background"),
                    E("WorkingDirectory", Path.GetDirectoryName(exe)!)))).ToString();
    }

    // 不把未来会以最高权限运行的任务指向下载目录、可改写的 EXE 或目录联接。
    internal static void CheckLocation(string exe)
    {
        string programs = Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        if (programs.Length == 0 || !Path.GetFileName(exe).Equals(UpdatePackage.Exe, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFullPath(exe).StartsWith(programs + "\\", StringComparison.OrdinalIgnoreCase) ||
            File.Exists(Path.ChangeExtension(exe, ".runtimeconfig.json")))
            throw new InvalidOperationException($"请先从托盘退出，将完整发布包解压到 {Path.Combine(programs, "SimpleLanShare")}，从该目录启动新版，再开启管理员自启动。");
        FileSystemInfo item = new FileInfo(exe);
        while (true)
        {
            if ((item.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("管理员启动目录不能经过目录联接或符号链接。");
            var acl = item is FileInfo file ? (FileSystemSecurity)file.GetAccessControl() : ((DirectoryInfo)item).GetAccessControl();
            if (!Trusted(acl.GetOwner(typeof(SecurityIdentifier)))) throw new IOException("程序或安装目录的所有者需要是管理员、SYSTEM 或 TrustedInstaller。");
            const FileSystemRights writes = FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles
                | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership | (FileSystemRights)0x50000000;
            foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                if (rule.AccessControlType == AccessControlType.Allow && !rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly)
                    && (rule.FileSystemRights & writes) != 0 && !Trusted(rule.IdentityReference))
                    throw new IOException("程序或安装目录允许普通用户写入，请使用 Program Files 的默认权限。");
            if (item.FullName.Equals(programs, StringComparison.OrdinalIgnoreCase)) break;
            item = new DirectoryInfo(Path.GetDirectoryName(item.FullName)!);
        }
    }

    // 只有系统安装与管理主体可更改定时启动的程序。
    private static bool Trusted(IdentityReference? id) => id?.Value is "S-1-5-18" or "S-1-5-32-544"
        or "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

    // 只连接本机计划任务服务；不存在的任务属于正常未开启状态。
    private static dynamic Root()
    {
        dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", true)!)!;
        service.Connect();
        return service.GetFolder("\\");
    }

    // 检查命名任务是否属于本软件，避免覆盖用户或其他程序的同名任务。
    private static dynamic? Find(dynamic root, string sid)
    {
        try
        {
            dynamic task = root.GetTask(Source + "." + sid);
            var xml = XElement.Parse((string)task.Xml);
            if ((string?)xml.Element(Ns + "RegistrationInfo")?.Element(Ns + "Source") != Source)
                throw new InvalidOperationException("同名计划任务已被占用。");
            return task;
        }
        catch (Exception ex) when (ex.HResult == unchecked((int)0x80070002)) { return null; }
    }

    // 任务是否存在决定由它还是普通 Run 项管理登录启动。
    internal static bool Registered() => Find(Root(), UserSid()) is not null;

    // UAC 助手不接收可执行路径、命令或账号凭据。其他管理员代输凭据时拒绝切换配对身份。
    internal static void Configure(bool enable, string expectedSid)
    {
        string sid = UserSid();
        using var identity = WindowsIdentity.GetCurrent();
        if (sid != expectedSid || !new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new InvalidOperationException("请用当前登录的管理员账号确认 UAC，以便继续使用该账号的托盘和配对记录。");
        dynamic root = Root();
        dynamic? existing = Find(root, sid);
        string name = Source + "." + sid;
        if (!enable)
        {
            if (existing is not null) root.DeleteTask(name, 0);
            return;
        }
        string exe = Environment.ProcessPath!;
        CheckLocation(exe);
        UpdatePackage.VerifyStage(Path.GetDirectoryName(exe)!);
        // 所有者固定为管理员组；普通令牌可以查看任务，但无法改写高权限启动命令。
        string acl = $"O:BAG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;FR;;;{sid})";
        root.RegisterTask(name, Definition(sid, exe), (existing is null ? 2 : 4) | 0x10, sid, null, 3, acl);
    }

    // 当前进程已经提升时直接配置，否则通过 Windows 标准 UAC 对话框授权一次。
    internal static async Task ChangeAsync(bool enable)
    {
        string exe = Environment.ProcessPath!;
        if (enable) CheckLocation(exe);
        string sid = UserSid();
        if (InputIntegrity.Local >= 0x3000) { await Task.Run(() => Configure(enable, sid)); return; }
        var info = new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas", WorkingDirectory = Path.GetDirectoryName(exe)! };
        info.ArgumentList.Add("--configure-admin-login");
        info.ArgumentList.Add(enable ? "enable" : "disable");
        info.ArgumentList.Add(sid);
        try
        {
            using var helper = Process.Start(info) ?? throw new IOException("未能启动授权窗口。");
            await helper.WaitForExitAsync();
            if (helper.ExitCode != 0) throw new IOException("未能更改管理员登录任务。");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { throw new OperationCanceledException("已取消 Windows 授权。"); }
    }
}
