namespace LanShare.App;

// 只在本机被控制时用细环标出系统的真实光标热点，系统隐藏箭头时也能看见点击位置。
internal sealed class RemotePointerMarker : Form
{
    private readonly System.Windows.Forms.Timer follow = new() { Interval = 16 };
    private const int Radius = 10;

    // 透明、穿透点击且不抢焦点，避免标记本身影响鼠标命中。
    internal RemotePointerMarker()
    {
        AutoScaleMode = AutoScaleMode.None;
        StartPosition = FormStartPosition.Manual;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false; TopMost = true;
        BackColor = TransparencyKey = Color.Magenta;
        ClientSize = new Size(2 * Radius + 1, 2 * Radius + 1);
        follow.Tick += (_, _) => FollowHotspot();
    }

    // 标记不能成为目标窗口或占用任务栏位置。
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get { var p = base.CreateParams; p.ExStyle |= 0x080800A0; return p; }
    }

    // 进入接收状态才显示，返回、断线和锁屏均停止跟踪。
    internal void SetReceiving(bool receiving)
    {
        if (!receiving) { follow.Stop(); Hide(); return; }
        FollowHotspot();
        if (!Visible)
        {
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
                RecordingOverlay.SetWindowDisplayAffinity(Handle, 0x11);
            Show();
        }
        follow.Start();
    }

    // 从系统读取实际点击热点，而非按网络消息预测目标位置。
    private void FollowHotspot()
    {
        Point p = Cursor.Position;
        var origin = new Point(p.X - Radius, p.Y - Radius);
        if (Location != origin) Location = origin;
    }

    // 黑白双轮廓保证浅色和深色背景上都可辨认，中心保持透明。
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.DrawEllipse(Pens.Black, 1, 1, 18, 18);
        e.Graphics.DrawEllipse(Pens.White, 2, 2, 16, 16);
        e.Graphics.DrawEllipse(Pens.Black, 3, 3, 14, 14);
    }

    // 使用物理像素标记热点，跨 DPI 显示器时不接受系统建议的缩放矩形。
    protected override void OnDpiChanged(DpiChangedEventArgs e) { e.Cancel = true; base.OnDpiChanged(e); }

    // 释放跟踪定时器及置顶窗口。
    protected override void Dispose(bool disposing) { if (disposing) follow.Dispose(); base.Dispose(disposing); }
}
