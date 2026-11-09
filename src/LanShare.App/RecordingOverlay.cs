using System.Runtime.InteropServices;

namespace LanShare.App;

// 录制边框优先位于选区外侧，贴屏时向内收；中间镂空，鼠标可继续操作被录制窗口。
internal sealed class RecordingOverlay : Form
{
    private readonly RecordingToolbar toolbar;
    private readonly Rectangle area;
    private const int Border = 3;
    internal RecordingToolbar Toolbar => toolbar;

    // 悬浮条与边框都保持置顶，停止事件由控制器统一处理。
    public RecordingOverlay(Rectangle region, string shortcut, Action stop)
    {
        area = region;
        AutoScaleMode = AutoScaleMode.None; FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual; ShowInTaskbar = false; TopMost = true;
        BackColor = Color.FromArgb(210, 66, 66);
        SetFrameBounds(Rectangle.Inflate(region, Border, Border));
        toolbar = new RecordingToolbar(shortcut, stop);
    }

    // 不抢走当前程序焦点，也不在任务栏创建额外窗口。
    protected override bool ShowWithoutActivation => true;

    // 工具窗口不激活；透明扩展样式让边框不影响边缘鼠标操作。
    protected override CreateParams CreateParams
    {
        get { var value = base.CreateParams; value.ExStyle |= 0x08000000 | 0x00000080 | 0x00000020; return value; }
    }

    // 显示在物理坐标，工具条优先放在选区外；全屏选区时使用系统捕获排除能力。
    public void ShowControls()
    {
        Rectangle frame = Rectangle.Intersect(Rectangle.Inflate(area, Border, Border), SystemInformation.VirtualScreen);
        SetFrameBounds(frame);
        Show();
        SetWindowPos(Handle, new IntPtr(-1), frame.X, frame.Y, frame.Width, frame.Height, 0x0010);
        bool frameExcluded = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041) && SetWindowDisplayAffinity(Handle, 0x11);
        if (!frameExcluded && frame != Rectangle.Inflate(area, Border, Border))
            throw new InvalidOperationException("当前系统无法隐藏贴屏边框，请将录屏选区稍微缩小后重试。");
        Rectangle screen = Screen.FromRectangle(area).Bounds;
        toolbar.Bounds = PlaceToolbar(area, screen, toolbar.Size);
        toolbar.Show(this);
        toolbar.Bounds = PlaceToolbar(area, screen, toolbar.Size);
        bool excluded = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041) && SetWindowDisplayAffinity(toolbar.Handle, 0x11);
        if (!excluded && toolbar.Bounds.IntersectsWith(area))
            throw new InvalidOperationException("当前系统无法从录屏中排除控制条，请缩小选区，为停止按钮留出屏幕空间。");
    }

    // 靠屏幕边缘时边框向内收，避免全屏选区的四条边全部落在屏幕外看不见。
    private void SetFrameBounds(Rectangle frame)
    {
        Bounds = frame;
        var outline = new Region(new Rectangle(Point.Empty, frame.Size));
        Rectangle inner = Rectangle.Intersect(area, Rectangle.Inflate(frame, -Border, -Border));
        inner.Offset(-frame.X, -frame.Y);
        if (inner.Width > 0 && inner.Height > 0) outline.Exclude(inner);
        Region? previous = Region; Region = outline; previous?.Dispose();
    }

    // 位置计算兼容负坐标副屏和紧贴屏幕边缘的选区。
    internal static Rectangle PlaceToolbar(Rectangle area, Rectangle screen, Size size)
    {
        int x = Math.Clamp(area.Right - size.Width, screen.Left, Math.Max(screen.Left, screen.Right - size.Width));
        int y = area.Top - size.Height - 10;
        if (y < screen.Top) y = area.Bottom + 10;
        if (y + size.Height > screen.Bottom) y = screen.Top + 10;
        return new Rectangle(x, Math.Clamp(y, screen.Top, Math.Max(screen.Top, screen.Bottom - size.Height)), size.Width, size.Height);
    }

    // 始终用物理像素保持边框与原始录屏区域完全对齐。
    protected override void OnDpiChanged(DpiChangedEventArgs e) { e.Cancel = true; base.OnDpiChanged(e); }

    // 控制器更新录制和收尾状态，不将停止按钮隐藏得过早。
    public void UpdateStatus(TimeSpan elapsed, bool ready, bool stopping) => toolbar.UpdateStatus(elapsed, ready, stopping);

    // 结束、失败和取消都关闭两个悬浮窗口。
    protected override void Dispose(bool disposing) { if (disposing) toolbar.Dispose(); base.Dispose(disposing); }

    // 调用系统捕获排除能力，让控制条留在屏幕上而不进入视频画面。
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetWindowDisplayAffinity(IntPtr window, uint affinity);
    // 在混合 DPI 环境下用物理像素定位边框。
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}

// 独立小工具条显示持续时间和停止入口，不需要切回主窗口或托盘。
internal sealed class RecordingToolbar : Form
{
    private readonly Label clock = new() { AutoSize = false, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Button stopButton = new() { Text = "■  停止", FlatStyle = FlatStyle.Flat, TabStop = true };
    private bool stopping;

    // 工具条按 DPI 缩放，默认摆放由外层根据选区决定。
    public RecordingToolbar(string shortcut, Action stop)
    {
        AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(332, 48); FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual; ShowInTaskbar = false; TopMost = true;
        BackColor = Color.White; ForeColor = Color.FromArgb(37, 51, 57);
        Font = new Font("Microsoft YaHei UI", 9);
        clock.SetBounds(14, 9, 135, 30); clock.Text = "●  准备录制"; clock.ForeColor = Color.FromArgb(190, 48, 48);
        stopButton.SetBounds(153, 8, 78, 32); stopButton.BackColor = Color.FromArgb(190, 48, 48);
        stopButton.ForeColor = Color.White; stopButton.FlatAppearance.BorderSize = 0;
        var key = new Label { Text = shortcut, AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, Bounds = new Rectangle(236, 9, 91, 30) };
        stopButton.Click += (_, _) => { if (!stopping) { stopping = true; stopButton.Enabled = false; stop(); } };
        Controls.AddRange([clock, stopButton, key]);
    }

    // 点击停止不激活工具窗，避免中断录制目标程序的焦点。
    protected override bool ShowWithoutActivation => true;
    // 保留按钮交互同时避免抢占输入焦点。
    protected override CreateParams CreateParams { get { var value = base.CreateParams; value.ExStyle |= 0x08000000 | 0x80; return value; } }

    // 编码器准备阶段也允许停止；封装期间显示明确状态并防止重复触发。
    public void UpdateStatus(TimeSpan elapsed, bool ready, bool isStopping)
    {
        stopping = isStopping; stopButton.Enabled = !stopping;
        clock.Text = stopping ? "正在保存…" : ready ? $"●  {elapsed:hh\\:mm\\:ss}" : "●  准备录制";
    }
}
