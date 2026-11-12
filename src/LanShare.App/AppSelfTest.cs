using System.Drawing.Imaging;
using LanShare.Core;

namespace LanShare.App;

// 不修改系统剪贴板的 Windows 专项自检：图片编解码、DPAPI 和窗体创建。
internal static class AppSelfTest
{
    // 检查真实 GDI+ 和当前用户密钥服务，并将结果写到指定测试目录。
    public static void Run(string report)
    {
        var lines = new List<string>();
        string code = Guid.NewGuid().ToString("N");
        string protectedCode = AppSettings.Protect(code, false);
        if (protectedCode == code || AppSettings.Protect(protectedCode, true) != code) throw new Exception("DPAPI roundtrip failed");
        lines.Add("PASS: DPAPI current-user encrypted-data roundtrip");
        var settings = new AppSettings();
        using var identity = settings.ReadIdentity();
        var restored = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(System.Text.Json.JsonSerializer.Serialize(settings))!;
        using var restoredIdentity = restored.ReadIdentity();
        if (Security.Fingerprint(identity) != Security.Fingerprint(restoredIdentity)) throw new Exception("Identity persistence failed");
        lines.Add("PASS: DPAPI-protected identity survives settings roundtrip");
        using (var input = new WindowsInput())
        {
            input.Start(); input.Dispose(); input.Start();
            if (input.Width < 1 || input.Height < 1) throw new Exception("Invalid input desktop extent");
        }
        lines.Add("PASS: Windows keyboard and mouse hooks install, release and reinstall without injecting input");
        var motion = new PointerMotion();
        for (int n = 0; n < 8; n++)
        {
            if (motion.Observe(new Point(-1920, 60), true) != Point.Empty ||
                motion.Observe(new Point(-1913, 64)) != new Point(7, 4) ||
                motion.Observe(new Point(960, 540), true) != Point.Empty ||
                motion.Observe(new Point(957, 546)) != new Point(-3, 6) ||
                motion.Observe(new Point(957, 546)) != Point.Empty)
                throw new Exception("Pointer displacement drifted after recenter or re-entry");
        }
        lines.Add("PASS: eight round trips exclude warp offsets and stationary motion");
        Rectangle[] monitors = [new(-1920, 200, 1920, 1080), new(0, 0, 2560, 1440)];
        if (WindowsInput.VisiblePoint(new Point(-1500, 0), monitors) != new Point(-1500, 200) ||
            WindowsInput.VisiblePoint(new Point(-120, 400), monitors) != new Point(-120, 400))
            throw new Exception("Pointer missed visible monitor pixels");
        lines.Add("PASS: pointer targets visible pixels on staggered monitors with negative origins");
        using (var marker = new RemotePointerMarker())
        {
            marker.SetReceiving(true);
            var styles = GetWindowLong(marker.Handle, -20);
            if (!marker.Visible || (styles & 0x08080020) != 0x08080020)
                throw new Exception("Remote pointer marker must be visible, layered, click-through and non-activating");
            marker.SetReceiving(false);
            if (marker.Visible) throw new Exception("Remote pointer marker remained visible after return");
        }
        lines.Add("PASS: receiving cursor marker appears click-through and hides on return");
        using var bitmap = new Bitmap(8, 8);
        bitmap.SetPixel(3, 4, Color.FromArgb(128, 20, 100, 200));
        using var memory = new MemoryStream(); bitmap.Save(memory, ImageFormat.Png);
        using var decoded = ClipboardBridge.DecodeImage(memory.ToArray());
        if (decoded.Width != 8 || decoded.Height != 8 || decoded.GetPixel(3, 4).A != 128) throw new Exception("PNG roundtrip failed");
        lines.Add("PASS: PNG dimensions and transparency preserved in codec");
        byte[] huge = memory.ToArray();
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(huge.AsSpan(16, 4), 100000);
        bool rejected = false;
        try { using var invalid = ClipboardBridge.DecodeImage(huge); }
        catch (InvalidDataException) { rejected = true; }
        if (!rejected) throw new Exception("Oversized image accepted");
        lines.Add("PASS: oversized PNG rejected before GDI+ decode");
        using var form = new MainForm(true);
        form.SavePreview(Path.ChangeExtension(report, ".png"));
        for (int page = 1; page < 6; page++) form.SavePreview(Path.Combine(Path.GetDirectoryName(report)!, $"page-{page}.png"), page);
        form.Close(); Application.DoEvents();
        if (form.IsDisposed || form.Visible) throw new Exception("Close must hide to tray without disposal");
        lines.Add("PASS: window close keeps application alive in tray");
        form.ExitApplication();
        if (!form.IsDisposed) throw new Exception("Explicit tray exit must dispose window");
        lines.Add("PASS: explicit tray exit disposes application");
        lines.Add("PASS: WinForms layout and clipboard listener initialization");
        lines.AddRange(DesktopFeatureTests.Run(Path.GetDirectoryName(Path.GetFullPath(report))!));
        lines.AddRange(ScreenRecordingTests.Run(Path.GetDirectoryName(Path.GetFullPath(report))!));
        lines.AddRange(RecordingInteractionTests.Run(Path.GetDirectoryName(Path.GetFullPath(report))!));
        File.WriteAllLines(report, lines);
    }

    // 从实际窗口句柄读取扩展样式，验证光标标记的点击穿透和不激活行为。
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(nint window, int index);
}
