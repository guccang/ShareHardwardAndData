using System.Runtime.InteropServices;

namespace LanShare.App;

// 应用入口：剪贴板和文件拖拽均要求 STA 消息循环。
internal static class Program
{
    // 根据参数运行无副作用自检或启动正常桌面消息循环。
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Length == 2 && args[0] == "--self-test")
        {
            try { AppSelfTest.Run(Path.GetFullPath(args[1])); }
            catch (Exception ex) { File.WriteAllText(args[1], ex.ToString()); Environment.ExitCode = 1; }
            return;
        }
        if (args.Length == 2 && args[0] == "--preview")
        {
            using var preview = new MainForm(true);
            preview.SavePreview(Path.GetFullPath(args[1]));
            preview.ExitApplication();
            return;
        }
        // 关闭到托盘后再次运行同一软件只恢复已有窗口，避免端口和身份配置冲突。
        using var instance = new Mutex(true, "Local\\SimpleLanShare.Desktop", out bool firstInstance);
        if (!firstInstance)
        {
            var window = FindWindow(null, "局域共享 · Simple LAN Share");
            if (!args.Contains("--background", StringComparer.OrdinalIgnoreCase) && window != IntPtr.Zero) PostMessage(window, 0x8201, IntPtr.Zero, IntPtr.Zero);
            return;
        }
        Application.ThreadException += (_, e) => MessageBox.Show(e.Exception.Message, "局域共享", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        try { Application.Run(new MainForm(startInTray: !args.Contains("--show", StringComparer.OrdinalIgnoreCase))); }
        finally { instance.ReleaseMutex(); }
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? className, string windowName);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
}
