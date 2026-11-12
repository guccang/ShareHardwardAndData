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
        if (args is ["--open-share-folder"])
        {
            try { ExplorerMenu.OpenDirectory(AppSettings.Load().ReceiveFolder); }
            catch (Exception ex) { ShowShellError("无法打开共享目录。", ex); }
            return;
        }
        ExplorerSend? request = null;
        if (args.FirstOrDefault() == "--send-file")
        {
            if (args.Length != 3 || !(request = new ExplorerSend(args[1], args[2])).Valid)
            {
                MessageBox.Show("请选择一个文件和有效的共享电脑。", "局域共享");
                return;
            }
        }
        // 关闭到托盘后再次运行同一软件只恢复已有窗口，避免端口和身份配置冲突。
        using var instance = new Mutex(true, "Local\\SimpleLanShare.Desktop", out bool firstInstance);
        if (!firstInstance)
        {
            if (request is not null)
            {
                try { ExplorerSendPipe.ForwardAsync(ExplorerSendPipe.Name, request).GetAwaiter().GetResult(); }
                catch (Exception ex) { ShowShellError("无法联系托盘程序。请确认已退出旧版并启动新版。", ex); }
                return;
            }
            var window = FindWindow(null, "局域共享 · Simple LAN Share");
            if (!args.Contains("--background", StringComparer.OrdinalIgnoreCase) && window != IntPtr.Zero) PostMessage(window, 0x8201, IntPtr.Zero, IntPtr.Zero);
            return;
        }
        Application.ThreadException += (_, e) => MessageBox.Show(e.Exception.Message, "局域共享", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        try { Application.Run(new MainForm(startInTray: request is not null || !args.Contains("--show", StringComparer.OrdinalIgnoreCase), initialSend: request)); }
        finally { instance.ReleaseMutex(); }
    }
    // 命令行助手没有主窗体，直接向发起操作的用户展示失败原因。
    private static void ShowShellError(string message, Exception ex)
    {
        Environment.ExitCode = 1;
        MessageBox.Show(message + Environment.NewLine + ex.Message, "局域共享", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? className, string windowName);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
}
