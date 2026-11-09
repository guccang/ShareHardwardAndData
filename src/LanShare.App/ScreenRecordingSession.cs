using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace LanShare.App;

// 单次录制在专用线程捕获和编码；界面线程只发停止信号，不阻塞配对、剪贴板和文件传输。
internal sealed class ScreenRecordingSession : IDisposable
{
    private readonly CancellationTokenSource stop = new();
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool disposed;
    public Task<RecordingResult> Completion { get; }
    public Task Ready => ready.Task;
    public Size VideoSize { get; }
    public string OutputPath { get; }

    // 测试可注入合成帧，生产只捕获用户明确选择的区域；不读取麦克风或系统音频。
    public ScreenRecordingSession(Rectangle region, string path, Action<Bitmap>? capture = null)
    {
        ScreenshotGeometry.ValidateDesktop(region);
        VideoSize = FitVideoSize(region.Size);
        OutputPath = Path.GetFullPath(path);
        Completion = Task.Factory.StartNew(() => Record(region, capture), CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    // H.264 使用偶数尺寸；超大选区保持比例缩至 1080p 范围，避免编码器尺寸不支持。
    internal static Size FitVideoSize(Size source)
    {
        if (source.Width < 2 || source.Height < 2) throw new ArgumentException("录屏区域至少为 2×2 像素。");
        double ratio = Math.Min(1, Math.Min(1920.0 / source.Width, 1080.0 / source.Height));
        return new Size(Math.Max(2, (int)(source.Width * ratio) & ~1), Math.Max(2, (int)(source.Height * ratio) & ~1));
    }

    // 仅在成功封装 MP4 后公布目标文件；自动命名也不允许覆盖任何已有视频。
    private RecordingResult Record(Rectangle region, Action<Bitmap>? capture)
    {
        string directory = Path.GetDirectoryName(OutputPath)!;
        string temporary = Path.Combine(directory, ".recording-" + Guid.NewGuid().ToString("N") + ".mp4");
        bool finalized = false;
        try
        {
            stop.Token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(directory);
            var clock = new Stopwatch();
            long frames = 0, lastTimestamp = -MediaFoundationVideoWriter.FrameDuration;
            string? warning = null;
            using (var writer = new MediaFoundationVideoWriter(temporary, VideoSize))
            using (var source = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppRgb))
            using (var frame = new Bitmap(VideoSize.Width, VideoSize.Height, PixelFormat.Format32bppRgb))
            using (var sourceGraphics = Graphics.FromImage(source))
            using (var frameGraphics = Graphics.FromImage(frame))
            {
                frameGraphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
                clock.Start();
                while (!stop.IsCancellationRequested)
                {
                    // 时间使用真实经过的时长，低性能电脑丢帧时不会把视频加速播放。
                    long timestamp = frames == 0 ? 0 : Math.Max(lastTimestamp + MediaFoundationVideoWriter.FrameDuration, clock.Elapsed.Ticks);
                    try
                    {
                        if (capture is not null) capture(source);
                        else sourceGraphics.CopyFromScreen(region.Location, Point.Empty, region.Size, CopyPixelOperation.SourceCopy);
                        frameGraphics.DrawImage(source, new Rectangle(Point.Empty, VideoSize), new Rectangle(Point.Empty, source.Size), GraphicsUnit.Pixel);
                    }
                    catch (Exception ex) when (frames > 0)
                    {
                        warning = "屏幕捕获中断，已保存此前内容：" + ex.Message; break;
                    }
                    writer.WriteFrame(frame, timestamp);
                    lastTimestamp = timestamp; frames++;
                    ready.TrySetResult();
                    int delay = (int)Math.Clamp((timestamp + MediaFoundationVideoWriter.FrameDuration - clock.Elapsed.Ticks) / TimeSpan.TicksPerMillisecond, 0, 50);
                    if (delay > 0) stop.Token.WaitHandle.WaitOne(delay);
                }
                if (frames == 0) throw new OperationCanceledException(stop.Token);
                writer.Complete(); finalized = true;
            }
            // 完成后释放文件句柄，再公布最终文件名，极端重名也保留两个文件。
            File.Move(temporary, OutputPath, false);
            return new RecordingResult(OutputPath, TimeSpan.FromTicks(lastTimestamp + MediaFoundationVideoWriter.FrameDuration), warning);
        }
        catch (Exception ex)
        {
            ready.TrySetException(ex);
            if (finalized) throw new IOException("视频已生成，但无法保存到目标位置。可从此处取回：" + temporary, ex);
            try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    // 停止信号可重复发送，正在写入的帧完成后封装文件。
    public void RequestStop() { if (!disposed) stop.Cancel(); }

    // 控制器在 Completion 结束后调用，确保等待句柄不会在捕获线程使用时被释放。
    public void Dispose()
    {
        if (disposed) return;
        RequestStop(); disposed = true;
        if (Completion.IsCompleted) stop.Dispose();
        else _ = Completion.ContinueWith(_ => stop.Dispose(), TaskScheduler.Default);
    }
}

// 记录最终文件和实际时长，捕获中断时额外给出已保留视频的说明。
internal sealed record RecordingResult(string Path, TimeSpan Duration, string? Warning);
