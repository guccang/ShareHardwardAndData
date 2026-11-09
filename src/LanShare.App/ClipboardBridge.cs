using System.Buffers.Binary;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using LanShare.Core;

namespace LanShare.App;

// 剪贴板操作全部留在 STA 界面线程，通过系统序号防止远端更新被再次广播。
internal sealed class ClipboardBridge : NativeWindow, IDisposable
{
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 250 };
    private uint lastSequence;
    private ClipboardContent? pending;
    private int attempts;
    private bool enabled;
    public event Action<ClipboardContent>? Changed;
    public event Action<string>? Notice;

    // 创建仅用于系统通知的隐藏窗口，不轮询剪贴板内容。
    public ClipboardBridge()
    {
        CreateHandle(new CreateParams { Caption = "SimpleLanShare.Clipboard", Parent = new IntPtr(-3) });
        if (!AddClipboardFormatListener(Handle)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        lastSequence = GetClipboardSequenceNumber();
        timer.Tick += OnTimer;
    }

    // 开启时不广播历史剪贴板，防止加入共享组时覆盖正在使用的其他电脑。
    public bool Enabled
    {
        get => enabled;
        set { enabled = value; lastSequence = GetClipboardSequenceNumber(); pending = null; timer.Stop(); }
    }

    // 合并短时间内重复的系统消息，给占用剪贴板的应用留出释放时间。
    protected override void WndProc(ref System.Windows.Forms.Message message)
    {
        if (message.Msg == 0x031D && enabled && GetClipboardSequenceNumber() != lastSequence)
        {
            pending = null; attempts = 0; timer.Stop(); timer.Start();
        }
        base.WndProc(ref message);
    }

    // 收到网络数据后在 STA 上排队，失败时短暂重试而不阻塞网络接收。
    public void Receive(ClipboardContent content)
    {
        if (!enabled) return;
        pending = content; attempts = 0; timer.Stop(); timer.Start();
    }

    // 用户点击同步按钮时显式读取当前内容。
    public void ShareNow()
    {
        if (!enabled) return;
        pending = null; lastSequence = 0; attempts = 0; timer.Start();
    }

    // 重试次数有限；远端写入成功后记住系统序号，抑制回声同步。
    private void OnTimer(object? sender, EventArgs args)
    {
        timer.Stop();
        if (!enabled) return;
        try
        {
            if (pending is not null)
            {
                var content = pending;
                if (content.Format == "text") Clipboard.SetText(new UTF8Encoding(false, true).GetString(content.Data));
                else
                {
                    using var image = DecodeImage(content.Data);
                    Clipboard.SetImage(image);
                }
                pending = null; lastSequence = GetClipboardSequenceNumber();
                Notice?.Invoke(content.Format == "text" ? "文字剪贴板已更新" : "图片剪贴板已更新");
            }
            else Capture();
        }
        catch (ExternalException)
        {
            if (++attempts <= 8) timer.Start();
            else { pending = null; Notice?.Invoke("剪贴板正被其他应用占用，本次同步已跳过"); }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or DecoderFallbackException or OutOfMemoryException)
        { pending = null; lastSequence = GetClipboardSequenceNumber(); Notice?.Invoke("剪贴板未同步：" + ex.Message); }
    }

    // 优先读取图片，普通文字以 UTF-8 编码传输；资源管理器中的文件复制不自动发送。
    private void Capture()
    {
        uint sequence = GetClipboardSequenceNumber();
        if (sequence == lastSequence) return;
        ClipboardContent? content = null;
        if (Clipboard.ContainsImage())
        {
            using var image = Clipboard.GetImage();
            if (image is not null)
            {
                ValidateDimensions(image.Width, image.Height);
                using var memory = new MemoryStream(); image.Save(memory, ImageFormat.Png);
                PeerSession.ValidateClipboard("png", memory.Length);
                content = new("png", memory.ToArray());
            }
        }
        else if (Clipboard.ContainsText(TextDataFormat.UnicodeText))
        {
            string text = Clipboard.GetText(TextDataFormat.UnicodeText);
            int size = Encoding.UTF8.GetByteCount(text);
            PeerSession.ValidateClipboard("text", size);
            content = new("text", Encoding.UTF8.GetBytes(text));
        }
        // 读取期间被其他应用改写时丢弃旧快照，重新读取最新序号。
        if (GetClipboardSequenceNumber() != sequence) { timer.Start(); return; }
        lastSequence = sequence;
        if (content is not null) Changed?.Invoke(content);
    }

    // 在 GDI+ 解码前检查 PNG 尺寸，避免小压缩文件展开为超大位图。
    internal static Bitmap DecodeImage(byte[] data)
    {
        PeerSession.ValidateClipboard("png", data.Length);
        byte[] signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (data.Length < 33 || !data.AsSpan(0, 8).SequenceEqual(signature) ||
            !data.AsSpan(12, 4).SequenceEqual("IHDR"u8)) throw new InvalidDataException("无效 PNG 图片。");
        int width = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(16, 4));
        int height = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(20, 4));
        ValidateDimensions(width, height);
        using var memory = new MemoryStream(data, false);
        using var decoded = Image.FromStream(memory, true, true);
        return new Bitmap(decoded);
    }

    // 图片像素数另设上限，控制解码和剪贴板复制的内存占用。
    private static void ValidateDimensions(int width, int height)
    {
        if (width <= 0 || height <= 0 || width > 16000 || height > 16000 || (long)width * height > 32_000_000)
            throw new InvalidDataException("图片不能超过 3200 万像素，单边不能超过 16000 像素。");
    }

    // 随主窗口退出注销系统监听。
    public void Dispose()
    {
        timer.Dispose(); RemoveClipboardFormatListener(Handle); DestroyHandle();
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
}
