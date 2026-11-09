using System.Diagnostics;
using System.Drawing.Imaging;

namespace LanShare.App;

// 录屏交互状态只在 UI 线程改变；退出、再次点击与取消框选共用同一完成任务。
internal sealed class ScreenRecordingController : IDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private ScreenshotSelection? selection;
    private ScreenRecordingSession? session;
    private Task? running;
    private bool disposed;
    private readonly Stopwatch elapsed = new();
    public bool IsBusy { get; private set; }
    public bool IsRecording { get; private set; }
    public bool IsStopping { get; private set; }
    public string? LastVideo { get; private set; }
    public TimeSpan Elapsed => elapsed.Elapsed;
    public event Action? Changed;
    public event Action<string>? Notice;

    // 入口立即标记占用，避免连续点击产生多个框选或编码线程。
    public Task StartAsync()
    {
        if (disposed || IsBusy) return Task.CompletedTask;
        IsBusy = true; IsStopping = false; Changed?.Invoke();
        running = RunAsync(); return running;
    }

    // 框选后由用户选择输出位置，取消任一步都不会改动剪贴板或创建视频。
    private async Task RunAsync()
    {
        try
        {
            await Task.Delay(200, lifetime.Token);
            Rectangle desktopBounds = SystemInformation.VirtualScreen;
            ScreenshotGeometry.ValidateDesktop(desktopBounds);
            Rectangle region;
            using (var desktop = new Bitmap(desktopBounds.Width, desktopBounds.Height, PixelFormat.Format32bppRgb))
            {
                using (var graphics = Graphics.FromImage(desktop))
                    graphics.CopyFromScreen(desktopBounds.Location, Point.Empty, desktopBounds.Size, CopyPixelOperation.SourceCopy);
                using var overlay = new ScreenshotSelection(desktop, desktopBounds, "拖动选择录屏区域 · Esc / 右键取消");
                selection = overlay;
                if (overlay.ShowDialog() != DialogResult.OK) return;
                selection = null;
                region = new Rectangle(desktopBounds.X + overlay.Selected.X, desktopBounds.Y + overlay.Selected.Y, overlay.Selected.Width, overlay.Selected.Height);
            }
            lifetime.Token.ThrowIfCancellationRequested();
            Size output = ScreenRecordingSession.FitVideoSize(region.Size);
            using var save = new SaveFileDialog
            {
                Title = $"保存录屏 · 无声 MP4 · {output.Width}×{output.Height}", Filter = "MP4 视频|*.mp4",
                DefaultExt = "mp4", AddExtension = true, OverwritePrompt = true,
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
                FileName = "录屏_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".mp4"
            };
            if (save.ShowDialog() != DialogResult.OK) return;
            await Task.Delay(300, lifetime.Token);
            session = new ScreenRecordingSession(region, save.FileName);
            try { await session.Ready; }
            catch { await session.Completion; throw; }
            IsRecording = true; elapsed.Restart(); Changed?.Invoke();
            Notice?.Invoke($"正在录屏：{output.Width}×{output.Height}，无声 MP4；从托盘选择“停止录屏并保存”结束。");
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
            selection = null; session?.Dispose(); session = null; elapsed.Stop();
            IsRecording = IsBusy = IsStopping = false; Changed?.Invoke();
        }
    }

    // 真正退出前取消框选或等待视频封装；正常停止录制不会禁用下次录制。
    public async Task StopAsync(bool exiting = false)
    {
        if (exiting) lifetime.Cancel();
        if (!IsBusy) return;
        IsStopping = true; Changed?.Invoke();
        selection?.Close(); session?.RequestStop();
        if (running is not null) await running;
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
        disposed = true; lifetime.Cancel(); selection?.Close(); session?.RequestStop();
        if (!IsBusy) lifetime.Dispose();
    }
}
