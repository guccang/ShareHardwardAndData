using System.Drawing.Imaging;

namespace LanShare.App;

// 截图控制器串行处理冻结桌面、选区、剪贴板和预览，沿用参考项目的轻量交互。
internal sealed class ScreenshotController : IDisposable
{
    private readonly Icon icon;
    private readonly CancellationTokenSource stop = new();
    private Bitmap? lastImage;
    private ScreenshotPreview? preview;
    private ScreenshotSelection? selection;
    private bool disposed;
    public bool IsCapturing { get; private set; }
    public bool HasImage => lastImage is not null;
    public event Action<string>? Notice;

    // 窗口图标由主程序拥有，不重复创建新的托盘实例。
    public ScreenshotController(Icon appIcon) => icon = appIcon;

    // 先等待菜单和主窗口隐藏，截图完成后普通剪贴板监听自动将图片同步给已配对电脑。
    public async Task CaptureAsync()
    {
        if (disposed || IsCapturing) return;
        IsCapturing = true;
        bool restorePreview = preview is { Visible: true };
        preview?.Hide();
        try
        {
            await Task.Delay(200, stop.Token);
            Rectangle bounds = SystemInformation.VirtualScreen;
            ScreenshotGeometry.ValidateDesktop(bounds);
            using var desktop = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(desktop))
                graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);
            using var overlay = new ScreenshotSelection(desktop, bounds);
            selection = overlay;
            DialogResult result = overlay.ShowDialog();
            selection = null; stop.Token.ThrowIfCancellationRequested();
            if (result != DialogResult.OK) { Notice?.Invoke("已取消截图，剪贴板保持不变"); return; }
            Bitmap cropped = Crop(desktop, overlay.Selected);
            lastImage?.Dispose(); lastImage = cropped;
            try { CopyImage(cropped); Notice?.Invoke($"截图已复制：{cropped.Width} × {cropped.Height}，可直接粘贴"); }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                Notice?.Invoke("剪贴板被占用，截图已保留；可在预览中重新复制或保存");
                ShowLast(); restorePreview = false;
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception ex) { Notice?.Invoke("截图失败：" + ex.Message); }
        finally
        {
            selection = null; IsCapturing = false;
            if (restorePreview && !disposed && preview is { IsDisposed: false }) preview.Show();
        }
    }

    // 精确裁剪物理像素，不将预览缩放结果用作最终图片。
    internal static Bitmap Crop(Bitmap source, Rectangle rectangle)
    {
        if (rectangle.Width < 2 || rectangle.Height < 2 || !new Rectangle(Point.Empty, source.Size).Contains(rectangle))
            throw new ArgumentException("截图选区超出桌面或过小。");
        return source.Clone(rectangle, PixelFormat.Format32bppArgb);
    }

    // 持久化 Windows 剪贴板数据，退出程序后截图仍可粘贴；只通过正常剪贴板事件同步。
    internal static void CopyImage(Bitmap bitmap)
    {
        var data = new DataObject(); data.SetData(DataFormats.Bitmap, true, bitmap);
        Clipboard.SetDataObject(data, true, 5, 100);
    }

    // 每次预览都持有独立副本，下一次截图不会销毁正在显示的图片。
    public void ShowLast()
    {
        if (disposed || lastImage is null) { Notice?.Invoke("还没有截图，请先使用截图快捷键"); return; }
        preview?.Close();
        var window = new ScreenshotPreview(new Bitmap(lastImage), icon, CopyImage);
        preview = window;
        window.FormClosed += (_, _) => { if (ReferenceEquals(preview, window)) preview = null; };
        window.Show(); window.Activate();
    }

    // 真正退出时终止尚未完成的框选，避免后台计时结束后再次写入剪贴板。
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; stop.Cancel(); selection?.Close(); preview?.Close();
        lastImage?.Dispose(); lastImage = null;
    }
}
