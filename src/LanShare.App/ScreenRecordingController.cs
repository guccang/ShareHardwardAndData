using System.Diagnostics;
using System.Drawing.Imaging;

namespace LanShare.App;

// 录屏交互状态只在 UI 线程改变；退出、再次点击与取消框选共用同一完成任务。
internal sealed class ScreenRecordingController : IDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private ScreenshotSelection? selection;
    private ScreenRecordingSession? session;
    private RecordingOverlay? overlay;
    private CancellationTokenSource? runStop;
    private Task? running;
    private bool disposed;
    private readonly Stopwatch elapsed = new();
    public bool IsBusy { get; private set; }
    public bool IsRecording { get; private set; }
    public bool IsStopping { get; private set; }
    public string? LastVideo { get; private set; }
    public TimeSpan Elapsed => elapsed.Elapsed;
    public string ShortcutLabel { get; set; } = "Ctrl+Alt+R";
    public static string SaveDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "SimpleLanShare");
    public event Action? Changed;
    public event Action<string>? Notice;

    // 入口立即标记占用，避免连续点击产生多个框选或编码线程。
    public Task StartAsync()
    {
        if (disposed || IsBusy) return Task.CompletedTask;
        runStop = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        elapsed.Reset(); IsBusy = true; IsStopping = false; Changed?.Invoke();
        running = RunAsync(); return running;
    }

    // 框选后直接展示边框和停止条，自动命名保存，不弹出文件对话框。
    private async Task RunAsync()
    {
        try
        {
            CancellationToken token = runStop!.Token;
            await Task.Delay(200, token);
            Rectangle desktopBounds = SystemInformation.VirtualScreen;
            ScreenshotGeometry.ValidateDesktop(desktopBounds);
            Rectangle region;
            using (var desktop = new Bitmap(desktopBounds.Width, desktopBounds.Height, PixelFormat.Format32bppRgb))
            {
                using (var graphics = Graphics.FromImage(desktop))
                    graphics.CopyFromScreen(desktopBounds.Location, Point.Empty, desktopBounds.Size, CopyPixelOperation.SourceCopy);
                using var picker = new ScreenshotSelection(desktop, desktopBounds, "拖动框选，松开后开始录屏 · Esc / 右键取消");
                selection = picker;
                if (picker.ShowDialog() != DialogResult.OK) return;
                selection = null;
                region = new Rectangle(desktopBounds.X + picker.Selected.X, desktopBounds.Y + picker.Selected.Y, picker.Selected.Width, picker.Selected.Height);
            }
            token.ThrowIfCancellationRequested();
            Size output = ScreenRecordingSession.FitVideoSize(region.Size);
            overlay = new RecordingOverlay(region, ShortcutLabel, () => _ = StopAsync());
            overlay.ShowControls();
            await Task.Delay(200, token);
            session = new ScreenRecordingSession(region, CreateOutputPath(SaveDirectory));
            try { await session.Ready; }
            catch { await session.Completion; throw; }
            IsRecording = true; elapsed.Restart(); Changed?.Invoke(); UpdateOverlay();
            Notice?.Invoke($"正在录屏：{output.Width}×{output.Height}；点击选区旁的停止按钮或按 {ShortcutLabel}，自动保存 MP4。");
            RecordingResult result = await session.Completion;
            LastVideo = result.Path;
            Notice?.Invoke(result.Warning is null ? "录屏已保存：" + result.Path : result.Warning + "\n" + result.Path);
        }
        catch (OperationCanceledException) { Notice?.Invoke("录屏已取消"); }
        catch (Exception ex)
        {
            Notice?.Invoke("录屏失败：" + ex.Message + (ex is DllNotFoundException or System.Runtime.InteropServices.COMException ? "\n请确认 Windows 媒体功能可用（N 版需安装媒体功能包）。" : ""));
        }
        finally
        {
            selection = null; overlay?.Dispose(); overlay = null; session?.Dispose(); session = null; elapsed.Stop();
            runStop?.Dispose(); runStop = null;
            IsRecording = IsBusy = IsStopping = false; Changed?.Invoke();
        }
    }

    // 真正退出前取消框选或等待视频封装；正常停止录制不会禁用下次录制。
    public async Task StopAsync(bool exiting = false)
    {
        if (exiting) lifetime.Cancel();
        if (!IsBusy) return;
        IsStopping = true; Changed?.Invoke();
        UpdateOverlay(); runStop?.Cancel();
        selection?.Close(); session?.RequestStop();
        if (running is not null) await running;
    }

    // 时间戳便于查找，随机后缀避免同一毫秒多次录制重名；实际保存仍拒绝覆盖。
    internal static string CreateOutputPath(string directory) => Path.Combine(Path.GetFullPath(directory),
        "录屏_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + "_" + Guid.NewGuid().ToString("N")[..6] + ".mp4");

    // 主窗体计时器负责刷新浮层，隐藏主窗体时也持续更新。
    public void UpdateOverlay() => overlay?.UpdateStatus(Elapsed, IsRecording, IsStopping);

    // 固定保存目录可随时打开，首次使用时自动建立。
    public void OpenSaveDirectory()
    {
        try { Directory.CreateDirectory(SaveDirectory); Process.Start(new ProcessStartInfo(SaveDirectory) { UseShellExecute = true }); }
        catch (Exception ex) { Notice?.Invoke("打开录屏目录失败：" + ex.Message); }
    }

    // 在资源管理器定位结果，用户可继续通过现有文件投递区域发送视频。
    public void ShowLast()
    {
        if (LastVideo is null || !File.Exists(LastVideo)) { Notice?.Invoke("暂无已保存的录屏视频"); return; }
        try { Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + LastVideo + "\"") { UseShellExecute = true }); }
        catch (Exception ex) { Notice?.Invoke("打开视频位置失败：" + ex.Message); }
    }

    // 主窗口先等待 StopAsync，再释放控制器；异常退出也会尽早发出停止信号。
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; lifetime.Cancel(); selection?.Close(); overlay?.Dispose(); overlay = null; session?.RequestStop();
        if (!IsBusy) lifetime.Dispose();
    }
}
