using Microsoft.Win32;

namespace LanShare.App;

// 新功能测试使用隔离注册表、合成位图和隐藏窗体，不改写用户剪贴板或真实启动项。
internal static class DesktopFeatureTests
{
    // 返回逐项结果，由应用现有自检入口一起输出。
    public static IEnumerable<string> Run(string directory)
    {
        var results = new List<string>();
        CheckStartup(results);
        CheckScreenshotPixels(directory, results);
        CheckHotkeys(results);
        CheckHiddenStartup(results);
        return results;
    }

    // 真实注册表读写只作用于本次唯一测试子键，验证启用、升级路径和移除。
    private static void CheckStartup(List<string> results)
    {
        const string path = @"C:\测试 软件\SimpleLanShare.exe";
        string command = StartupRegistration.BuildCommand(path);
        Assert(command == "\"" + path + "\" --background", "startup command preserves spaces and background flag", results);
        string managed = StartupRegistration.BuildCommand(@"C:\Program Files\dotnet\dotnet.exe", @"C:\测试 软件\SimpleLanShare.dll");
        Assert(managed.Contains("\"C:\\测试 软件\\SimpleLanShare.dll\" --background"), "managed startup quotes DLL path", results);
        string testKey = @"Software\SimpleLanShare\Tests\" + Guid.NewGuid().ToString("N");
        try
        {
            StartupRegistration.SetEnabled(true, command, testKey);
            using (var key = Registry.CurrentUser.OpenSubKey(testKey))
                Assert((string?)key?.GetValue(StartupRegistration.ValueName) == command, "startup enable writes expected command", results);
            string moved = StartupRegistration.BuildCommand(@"D:\New Version\SimpleLanShare.exe");
            StartupRegistration.SetEnabled(true, moved, testKey);
            using (var key = Registry.CurrentUser.OpenSubKey(testKey))
                Assert((string?)key?.GetValue(StartupRegistration.ValueName) == moved, "startup command follows portable app relocation", results);
            using (var key = Registry.CurrentUser.OpenSubKey(testKey, true)) key!.SetValue("Unrelated", "keep");
            StartupRegistration.SetEnabled(false, moved, testKey);
            using (var key = Registry.CurrentUser.OpenSubKey(testKey))
                Assert(key?.GetValue(StartupRegistration.ValueName) is null && (string?)key?.GetValue("Unrelated") == "keep", "startup disable removes only this app value", results);
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(testKey, false); }
    }

    // 使用已知像素图验证反向拖动、负坐标屏幕、边界裁剪和 PNG 保存，不读取真实桌面。
    private static void CheckScreenshotPixels(string directory, List<string> results)
    {
        var desktop = new Rectangle(-24, -8, 48, 24);
        Rectangle normal = ScreenshotGeometry.Selection(new(-12, -4), new(16, 12), desktop);
        Rectangle reverse = ScreenshotGeometry.Selection(new(16, 12), new(-12, -4), desktop);
        Assert(normal == new Rectangle(12, 4, 28, 16) && reverse == normal, "negative monitor coordinates and reverse selection", results);
        Assert(ScreenshotGeometry.Selection(new(-100, -100), new(100, 100), desktop) == new Rectangle(0, 0, 48, 24), "selection clamps to virtual screen", results);
        bool tooLarge = false;
        try { ScreenshotGeometry.ValidateDesktop(new Rectangle(0, 0, 32768, 32768)); } catch (InvalidOperationException) { tooLarge = true; }
        Assert(tooLarge, "oversized virtual desktop rejected before allocation", results);
        using var image = new Bitmap(48, 24);
        for (int y = 0; y < image.Height; y++) for (int x = 0; x < image.Width; x++) image.SetPixel(x, y, Color.FromArgb(255, x * 4, y * 8, 80));
        using var cropped = ScreenshotController.Crop(image, normal);
        Assert(cropped.Size == normal.Size && cropped.GetPixel(0, 0) == image.GetPixel(12, 4) && cropped.GetPixel(27, 15) == image.GetPixel(39, 19), "crop preserves exact selected pixels", results);
        string png = Path.Combine(directory, "synthetic-screenshot.png");
        ScreenshotPreview.SavePng(cropped, png);
        using var loaded = new Bitmap(png);
        Assert(loaded.Size == cropped.Size && loaded.GetPixel(10, 5) == cropped.GetPixel(10, 5), "saved screenshot PNG preserves pixels", results);
        bool rejected = false;
        try { using var invalid = ScreenshotController.Crop(image, new Rectangle(40, 0, 20, 10)); } catch (ArgumentException) { rejected = true; }
        Assert(rejected, "out-of-bounds crop rejected", results);
    }

    // 选择不常用的空闲测试组合，验证真实注册冲突、事务回滚和退出后的释放。
    private static void CheckHotkeys(List<string> results)
    {
        Assert(!ScreenshotHotkey.IsValid(Keys.A) && ScreenshotHotkey.IsValid(ScreenshotHotkey.DefaultShortcut), "hotkey requires modifier and valid main key", results);
        using var first = new ScreenshotHotkey(ScreenshotHotkey.DefaultShortcut, false);
        using var second = new ScreenshotHotkey(ScreenshotHotkey.DefaultShortcut, false);
        Keys chosen = Keys.None;
        for (int key = (int)Keys.F24; key >= (int)Keys.F18; key--)
        {
            Keys candidate = (Keys)key | Keys.Control | Keys.Alt | Keys.Shift;
            if (first.Apply(candidate) is null) { chosen = candidate; break; }
        }
        if (chosen == Keys.None) throw new Exception("No free test hotkey available");
        Assert(first.Registered, "global screenshot hotkey registers", results);
        Assert(second.Apply(chosen) is not null && !second.Registered, "hotkey conflict does not replace existing registration", results);
        Keys next = Keys.None;
        for (int key = (int)Keys.F17; key >= (int)Keys.F10; key--)
        {
            Keys candidate = (Keys)key | Keys.Control | Keys.Alt | Keys.Shift;
            if (second.Apply(candidate) is null) { next = candidate; break; }
        }
        if (next == Keys.None) throw new Exception("No second test hotkey available");
        second.Dispose();
        bool persistCalled = false;
        string? failure = first.Apply(next, _ => { persistCalled = true; throw new IOException("test persistence failure"); });
        Assert(persistCalled && failure is not null && first.Shortcut == chosen && first.Registered, "failed settings save keeps old hotkey", results);
        using var third = new ScreenshotHotkey(ScreenshotHotkey.DefaultShortcut, false);
        Assert(third.Apply(next) is null, "failed hotkey update releases provisional registration", results);
        first.Dispose();
        Assert(third.Apply(chosen) is null, "exit releases hotkey for reuse", results);
    }

    // 运行真实 WinForms 消息循环，证明启动从未显示主窗口，恢复及关闭仍正确工作。
    private static void CheckHiddenStartup(List<string> results)
    {
        using var hidden = new MainForm(smoke: true, startInTray: true);
        int visibleCount = 0; Exception? failure = null;
        hidden.VisibleChanged += (_, _) => { if (hidden.Visible) visibleCount++; };
        using var timer = new System.Windows.Forms.Timer { Interval = 100 };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try
            {
                Assert(hidden.IsHandleCreated && !hidden.Visible && !hidden.ShowInTaskbar && visibleCount == 0, "background startup never shows main window", results);
                hidden.Opacity = 0;
                hidden.RestoreWindow();
                Assert(hidden.Visible && !hidden.IsDisposed, "tray restore opens existing hidden window", results);
                hidden.Close();
                Assert(!hidden.Visible && !hidden.IsDisposed, "restored window close returns to background", results);
            }
            catch (Exception ex) { failure = ex; }
            finally { hidden.ExitApplication(); }
        };
        timer.Start(); Application.Run(hidden);
        if (failure is not null) throw failure;
        Assert(hidden.IsDisposed, "explicit exit ends hidden application message loop", results);
    }

    // 每个断言描述可观察行为，便于区分窗口、系统设置和图片处理故障。
    private static void Assert(bool condition, string label, List<string> results)
    {
        if (!condition) throw new Exception("FAIL: " + label);
        results.Add("PASS: " + label);
    }
}
