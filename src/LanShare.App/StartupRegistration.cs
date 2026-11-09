using Microsoft.Win32;

namespace LanShare.App;

// 只为当前用户注册登录启动，不需要管理员权限，也不触碰其他软件的启动项。
internal static class StartupRegistration
{
    internal const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string ValueName = "SimpleLanShare";

    // 自包含程序直接启动 EXE；开发时若使用 dotnet 托管，也完整引用 DLL 路径。
    internal static string BuildCommand(string executable, string? managedAssembly = null)
    {
        if (!Path.IsPathFullyQualified(executable) || executable.Contains('"')) throw new ArgumentException("启动程序路径无效。");
        string command = "\"" + executable + "\"";
        if (managedAssembly is not null)
        {
            if (!Path.IsPathFullyQualified(managedAssembly) || managedAssembly.Contains('"')) throw new ArgumentException("托管程序集路径无效。");
            command += " \"" + managedAssembly + "\"";
        }
        return command + " --background";
    }

    // 计算当前程序的真实位置，便携程序换目录后重新运行即可更新注册路径。
    internal static string CurrentCommand()
    {
        string process = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定程序路径。");
        string? assembly = Path.GetFileNameWithoutExtension(process).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(AppContext.BaseDirectory, "SimpleLanShare.dll") : null;
        return BuildCommand(process, assembly);
    }

    // 测试可指定隔离子键，生产代码始终使用当前用户 Run 键下唯一的应用值。
    internal static void SetEnabled(bool enabled, string command, string registryPath = RunKey)
    {
        using var key = Registry.CurrentUser.CreateSubKey(registryPath, true) ?? throw new IOException("无法访问启动设置。");
        if (enabled) key.SetValue(ValueName, command, RegistryValueKind.String);
        else key.DeleteValue(ValueName, false);
    }
}
