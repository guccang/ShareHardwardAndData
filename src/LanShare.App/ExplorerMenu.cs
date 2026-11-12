using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using LanShare.Core;

namespace LanShare.App;

// 仅为当前用户注册资源管理器动词；设备以配对指纹寻址，菜单文字只用于显示。
internal static class ExplorerMenu
{
    private const string Send = @"*\shell\SimpleLanShare.Send";
    private const string Targets = @"SimpleLanShare.FileTargets";
    private const string Folder = @"Directory\Background\shell\SimpleLanShare.Open";
    private const string Desktop = @"DesktopBackground\Shell\SimpleLanShare.Open";

    // 将程序路径和设备列表同步到独立的注册表项，移动便携程序或撤销配对后也能更新。
    internal static void Update(RegistryKey classes, string launcher, string icon, bool enabled,
        IEnumerable<string> trusted, IReadOnlyDictionary<string, string> names)
    {
        if (!enabled)
        {
            foreach (string path in new[] { Send, Folder, Desktop, Targets }) classes.DeleteSubKeyTree(path, false);
            return;
        }
        var ids = trusted.Where(Security.IsFingerprint).Distinct().ToArray();
        using var children = classes.CreateSubKey(Targets + @"\shell");
        foreach (string id in ids)
        {
            string name = names.GetValueOrDefault(id, "已配对电脑").Replace("&", "&&");
            WriteVerb(children, id, name + " · " + id[..6], launcher + " --send-file " + id + " \"%1\"");
        }
        if (ids.Length == 0) WriteVerb(children, "Pair", "打开局域共享并配对…", launcher + " --show");
        foreach (string key in children.GetSubKeyNames())
            if (!ids.Contains(key) && !(key == "Pair" && ids.Length == 0)) children.DeleteSubKeyTree(key);

        using var send = classes.CreateSubKey(Send);
        send.SetValue("MUIVerb", "发送到共享电脑");
        send.SetValue("ExtendedSubCommandsKey", Targets);
        send.SetValue("MultiSelectModel", "Document");
        send.SetValue("Icon", icon);
        foreach (string path in new[] { Folder, Desktop })
        {
            WriteVerb(classes, path, "打开共享目录", launcher + " --open-share-folder");
            using var verb = classes.OpenSubKey(path, true)!;
            verb.SetValue("Icon", icon);
        }
    }

    // 特殊字符由资源管理器按独立参数传递，不经过命令解释器。
    private static void WriteVerb(RegistryKey root, string path, string title, string command)
    {
        using var key = root.CreateSubKey(path);
        key.SetValue("MUIVerb", title);
        key.SetValue("MultiSelectModel", "Document");
        using var action = key.CreateSubKey("command");
        action.SetValue("", command);
    }

    // 通知资源管理器刷新菜单，无须重启桌面进程。
    internal static void Apply(AppSettings settings)
    {
        using var classes = Registry.CurrentUser.CreateSubKey(@"Software\Classes");
        Update(classes, StartupRegistration.CurrentLauncher(), "\"" + Environment.ProcessPath + "\",0",
            settings.ExplorerMenuEnabled, settings.TrustedDeviceIds, settings.InputNames);
        SHChangeNotify(0x08000000, 0, 0, 0);
    }

    // 与主界面的打开按钮共用同一目录处理逻辑。
    internal static void OpenDirectory(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "open" });
    }

    // 广播文件关联变更。
    [DllImport("shell32.dll")] private static extern void SHChangeNotify(uint change, uint flags, nint item1, nint item2);
}
