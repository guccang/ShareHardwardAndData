using System.Runtime.InteropServices;

namespace LanShare.App;

// 录屏交互验证不读取真实桌面：自动命名、框外工具条、透明选区和取消收尾使用合成数据。
internal static class RecordingInteractionTests
{
    // 每项测试只验证可观察行为，不触碰真实录屏目录和用户快捷键。
    public static IEnumerable<string> Run(string directory)
    {
        var result = new List<string>();
        string[] paths = Enumerable.Range(0, 100).Select(_ => ScreenRecordingController.CreateOutputPath(directory)).ToArray();
        Assert(paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() == paths.Length && paths.All(p => Path.GetExtension(p) == ".mp4" && Path.GetDirectoryName(p) == directory), "automatic MP4 names are unique and stay in save folder", result);
        var screen = new Rectangle(-1920, 0, 1920, 1080);
        var area = new Rectangle(-1700, 100, 640, 480);
        var bar = RecordingOverlay.PlaceToolbar(area, screen, new Size(332, 48));
        Assert(screen.Contains(bar) && !area.IntersectsWith(bar), "recording toolbar stays outside selection on negative-coordinate monitor", result);
        bar = RecordingOverlay.PlaceToolbar(screen, screen, new Size(332, 48));
        Assert(screen.Contains(bar), "full-screen recording toolbar remains accessible", result);
        int stopClicks = 0;
        using (var overlay = new RecordingOverlay(area, "Ctrl+Alt+R", () => stopClicks++))
        {
            Assert(overlay.Region is not null && !overlay.Region.IsVisible(100, 100) && overlay.Region.IsVisible(1, 1), "recording border has a true click-through interior", result);
            overlay.Toolbar.Opacity = 0; overlay.Toolbar.Show(); overlay.Toolbar.PerformLayout(); Application.DoEvents();
            using var toolbar = new Bitmap(overlay.Toolbar.Width, overlay.Toolbar.Height);
            overlay.Toolbar.UpdateStatus(TimeSpan.FromSeconds(65), true, false);
            overlay.Toolbar.DrawToBitmap(toolbar, new Rectangle(Point.Empty, toolbar.Size));
            toolbar.Save(Path.Combine(directory, "recording-toolbar.png"));
            Assert(overlay.Toolbar.Controls.OfType<Label>().Any(label => label.Text.Contains("00:01:05")), "floating recording toolbar displays elapsed time", result);
            var stop = overlay.Toolbar.Controls.OfType<Button>().Single(); stop.PerformClick(); stop.PerformClick();
            Assert(stopClicks == 1 && !stop.Enabled, "floating stop button sends only one stop request", result);
            // 创建隐藏窗口即可验证系统是否接受捕获排除标记，不需要读取桌面内容。
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            {
                Assert(RecordingOverlay.SetWindowDisplayAffinity(overlay.Toolbar.Handle, 0x11) &&
                    GetWindowDisplayAffinity(overlay.Toolbar.Handle, out uint affinity) && affinity == 0x11,
                    "recording toolbar enables Windows capture exclusion", result);
            }
        }
        CheckEarlyStop(result);
        CheckExistingFile(directory, result);
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)) CheckCaptureExclusion(directory, result);
        return result;
    }

    // 只捕获本测试自己绘制的绿色窗口，实测同一 GDI 路径不会把悬浮停止条录进画面。
    private static void CheckCaptureExclusion(string directory, List<string> results)
    {
        Rectangle work = Screen.PrimaryScreen!.WorkingArea;
        using var pattern = new Form
        {
            FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, ShowInTaskbar = false,
            Bounds = new Rectangle(work.X + 40, work.Y + 40, 600, 360), BackColor = Color.Lime, TopMost = true
        };
        using var overlay = new RecordingOverlay(pattern.Bounds, "Ctrl+Alt+R", () => { });
        using var timer = new System.Windows.Forms.Timer { Interval = 350 };
        Exception? failure = null;
        bool controlsShown = false;
        pattern.Shown += (_, _) => timer.Start();
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try
            {
                using var frame = new Bitmap(pattern.Width, pattern.Height);
                using (var graphics = Graphics.FromImage(frame))
                {
                    try { graphics.CopyFromScreen(pattern.Location, Point.Empty, frame.Size, CopyPixelOperation.SourceCopy); }
                    catch (System.ComponentModel.Win32Exception ex) when (!controlsShown)
                    {
                        results.Add("SKIP: desktop capture unavailable before overlay: " + ex.Message);
                        return;
                    }
                }
                if (!controlsShown)
                {
                    overlay.ShowControls();
                    overlay.Toolbar.Location = new Point(pattern.Left + 20, pattern.Top + 20);
                    controlsShown = true; timer.Start(); return;
                }
                Color pixel = frame.GetPixel(50, 40);
                Assert(pixel.G > 240 && pixel.R < 15 && pixel.B < 15, "GDI capture omits floating controls and keeps underlying pixels", results);
                frame.Save(Path.Combine(directory, "overlay-exclusion-test.png"));
            }
            catch (Exception ex) { failure = ex; }
            finally { if (!timer.Enabled) { overlay.Close(); pattern.Close(); } }
        };
        Application.Run(pattern);
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    // 在框选开始前停止，验证不存在延迟回调再次打开选区、创建文件或遗留忙碌状态。
    private static void CheckEarlyStop(List<string> results)
    {
        using var host = new Form { Opacity = 0, ShowInTaskbar = false };
        using var controller = new ScreenRecordingController();
        Exception? failure = null;
        host.Shown += async (_, _) =>
        {
            try
            {
                Task pending = controller.StartAsync();
                await controller.StopAsync(); await pending;
                Assert(!controller.IsBusy && !controller.IsRecording && controller.LastVideo is null, "early stop cancels selection without saving or reopening", results);
            }
            catch (Exception ex) { failure = ex; }
            finally { host.Close(); }
        };
        Application.Run(host);
        if (failure is not null) throw failure;
    }

    // 极端命名冲突时保留已有文件，并保留新生成视频供找回，不做静默覆盖。
    private static void CheckExistingFile(string directory, List<string> results)
    {
        string isolated = Path.Combine(directory, "collision-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(isolated);
        string destination = Path.Combine(isolated, "existing.mp4"); File.WriteAllText(destination, "keep");
        using var recording = new ScreenRecordingSession(new Rectangle(0, 0, 160, 96), destination, bitmap =>
        { using var graphics = Graphics.FromImage(bitmap); graphics.Clear(Color.Teal); });
        recording.Ready.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
        recording.RequestStop();
        bool conflict = false;
        try { recording.Completion.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult(); } catch (IOException) { conflict = true; }
        Assert(conflict && File.ReadAllText(destination) == "keep" && Directory.EnumerateFiles(isolated, ".recording-*.mp4").Any(),
            "automatic save collision preserves existing video and recoverable new recording", results);
    }

    // 用结果文本关联实际行为，便于发布包自检定位失败。
    private static void Assert(bool condition, string label, List<string> results)
    { if (!condition) throw new Exception("FAIL: " + label); results.Add("PASS: " + label); }

    // 查询系统保存的窗口捕获策略，不捕获任何屏幕像素。
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowDisplayAffinity(IntPtr window, out uint affinity);
}
