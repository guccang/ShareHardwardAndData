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
            preview.Close();
            return;
        }
        Application.ThreadException += (_, e) => MessageBox.Show(e.Exception.Message, "局域共享", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        Application.Run(new MainForm());
    }
}
