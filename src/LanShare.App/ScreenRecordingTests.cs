using System.Buffers.Binary;
using System.Drawing.Imaging;
using System.Text;

namespace LanShare.App;

// 用合成画面验证真实系统编码器，不录制用户桌面，不改写剪贴板或真实启动配置。
internal static class ScreenRecordingTests
{
    // 编码后检查容器、文件释放、停止和异常路径；测试视频可另用独立解码器验证。
    public static IEnumerable<string> Run(string directory)
    {
        var results = new List<string>();
        Assert(ScreenRecordingSession.FitVideoSize(new Size(3840, 2160)) == new Size(1920, 1080), "4K selection scales to 1080p", results);
        Assert(ScreenRecordingSession.FitVideoSize(new Size(101, 55)) == new Size(100, 54), "odd dimensions become H264-compatible even dimensions", results);
        Assert(ScreenRecordingSession.FitVideoSize(new Size(1080, 1920)) == new Size(606, 1080), "portrait selection preserves aspect ratio within encoder limit", results);
        string sample = Path.Combine(directory, "synthetic-recording.mp4");
        using (var writer = new MediaFoundationVideoWriter(sample, new Size(160, 96)))
        using (var bitmap = new Bitmap(160, 96, PixelFormat.Format32bppRgb))
        {
            for (int i = 0; i < 40; i++)
            {
                PaintFrame(bitmap, i);
                writer.WriteFrame(bitmap, i * MediaFoundationVideoWriter.FrameDuration);
            }
            writer.Complete(); writer.Complete();
        }
        Assert(HasFinalizedMp4(sample), "real H264 encoder finalizes MP4 containing ftyp mdat moov", results);
        using (var exclusive = File.Open(sample, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert(exclusive.Length > 1000, "encoder releases output handle after finalization", results);

        string stoppedPath = Path.Combine(directory, "stopped-recording.mp4");
        using (var session = new ScreenRecordingSession(new Rectangle(-40, -30, 160, 96), stoppedPath, frame => PaintFrame(frame, 0)))
        {
            session.Ready.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
            // 允许后台线程写入数帧后请求停止，验证任务能及时结束且视频可封装。
            Thread.Sleep(250);
            session.RequestStop(); session.RequestStop();
            var result = session.Completion.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
            Assert(result.Path == stoppedPath && result.Duration > TimeSpan.Zero && HasFinalizedMp4(stoppedPath), "stop completes recording without UI thread or desktop capture", results);
        }
        string interruptedPath = Path.Combine(directory, "interrupted-recording.mp4");
        int captures = 0;
        using (var session = new ScreenRecordingSession(new Rectangle(0, 0, 160, 96), interruptedPath, frame =>
        {
            if (++captures > 3) throw new IOException("模拟显示器断开");
            PaintFrame(frame, captures);
        }))
        {
            session.Ready.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
            var result = session.Completion.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
            Assert(result.Warning is not null && HasFinalizedMp4(interruptedPath), "capture interruption preserves frames already recorded", results);
        }
        string preserved = Path.Combine(directory, "preserved-recording.mp4");
        File.WriteAllText(preserved, "existing video sentinel");
        using (var failed = new ScreenRecordingSession(new Rectangle(0, 0, 160, 96), preserved, _ => throw new IOException("模拟首帧失败")))
        {
            try { failed.Ready.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult(); } catch (IOException) { }
            bool rejected = false;
            try { failed.Completion.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult(); } catch (IOException) { rejected = true; }
            Assert(rejected && File.ReadAllText(preserved) == "existing video sentinel", "failed capture never replaces existing destination", results);
        }
        Assert(!Directory.EnumerateFiles(directory, ".recording-*.mp4").Any(), "normal stop and failed start leave no incomplete recording files", results);
        return results;
    }

    // 上半红色、下半蓝色，后半段上方变绿，白色方块移动，供独立解码检查方向、颜色和时序。
    private static void PaintFrame(Bitmap bitmap, int frame)
    {
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Blue);
        graphics.FillRectangle(frame < 20 ? Brushes.Red : Brushes.Lime, 0, 0, bitmap.Width, bitmap.Height / 2);
        graphics.FillRectangle(Brushes.White, (frame * 3) % (bitmap.Width - 16), 32, 16, 16);
    }

    // 按 MP4 顶层 box 长度逐项读取，拒绝截断文件和缺少索引的未完成录像。
    private static bool HasFinalizedMp4(string path)
    {
        using var file = File.OpenRead(path);
        var boxes = new HashSet<string>();
        Span<byte> header = stackalloc byte[16];
        while (file.Position < file.Length)
        {
            long start = file.Position;
            file.ReadExactly(header[..8]);
            ulong length = BinaryPrimitives.ReadUInt32BigEndian(header);
            string type = Encoding.ASCII.GetString(header.Slice(4, 4));
            int headerLength = 8;
            if (length == 1) { file.ReadExactly(header[8..]); length = BinaryPrimitives.ReadUInt64BigEndian(header[8..]); headerLength = 16; }
            if (length == 0) length = (ulong)(file.Length - start);
            if (length < (ulong)headerLength || length > (ulong)(file.Length - start)) return false;
            boxes.Add(type); file.Position = start + (long)length;
        }
        return boxes.IsSupersetOf(["ftyp", "mdat", "moov"]);
    }

    // 保留行为描述，失败时能直接定位到编码、停止或文件保护环节。
    private static void Assert(bool condition, string name, List<string> results)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        results.Add("PASS: " + name);
    }
}
