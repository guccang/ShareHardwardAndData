using System.Runtime.InteropServices;

namespace LanShare.App;

// 跨显示器框选遮罩：直接绘制冻结的物理像素桌面，不随 DPI 对图片缩放。
internal sealed class ScreenshotSelection : Form
{
    private readonly Bitmap desktopImage;
    private readonly Rectangle desktopBounds;
    private readonly string instructions;
    private Point anchor;
    private bool dragging, finishing;
    public Rectangle Selected { get; private set; }

    // 截图位图由控制器持有，遮罩关闭时不重复释放。
    public ScreenshotSelection(Bitmap image, Rectangle bounds, string instructions = "拖动框选 · Esc / 右键取消")
    {
        desktopImage = image; desktopBounds = bounds; this.instructions = instructions;
        AutoScaleMode = AutoScaleMode.None; FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual; Bounds = bounds;
        ShowInTaskbar = false; TopMost = true; DoubleBuffered = true;
        Cursor = Cursors.Cross; KeyPreview = true; Text = "框选截图";
    }

    // 使用原生物理坐标覆盖整个虚拟桌面，包括位于主屏左侧的负坐标显示器。
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        SetWindowPos(Handle, new IntPtr(-1), desktopBounds.X, desktopBounds.Y, desktopBounds.Width, desktopBounds.Height, 0x0040);
        Activate();
    }

    // 混合 DPI 环境下不采用系统建议的缩放窗口大小，保留一像素对应一像素。
    protected override void OnDpiChanged(DpiChangedEventArgs e) { e.Cancel = true; base.OnDpiChanged(e); }

    // Esc 取消本次操作，不改写剪贴板和上一张截图。
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; Finish(DialogResult.Cancel); }
        base.OnKeyDown(e);
    }

    // 左键开始框选，右键立即取消。
    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right) { Finish(DialogResult.Cancel); return; }
        if (e.Button != MouseButtons.Left) return;
        anchor = PointToScreen(e.Location); Selected = Rectangle.Empty;
        dragging = true; Capture = true; Invalidate();
    }

    // 支持任意方向拖动，选择范围始终裁在原始位图内。
    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (dragging) Selected = ScreenshotGeometry.Selection(anchor, PointToScreen(e.Location), desktopBounds);
        Invalidate();
    }

    // 过小区域不产生截图，可继续选择；鼠标捕获确保拖出窗口后仍能正确结束。
    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (!dragging || e.Button != MouseButtons.Left) return;
        Selected = ScreenshotGeometry.Selection(anchor, PointToScreen(e.Location), desktopBounds);
        dragging = false; Capture = false;
        if (Selected.Width >= 2 && Selected.Height >= 2) Finish(DialogResult.OK);
        else Invalidate();
    }

    // 锁屏、切换桌面等导致失焦时取消，避免遮罩留在不可交互状态。
    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (Visible && !IsDisposed && !finishing) Finish(DialogResult.Cancel);
    }

    // 先锁定结束状态，再改变模态结果，防止关闭引发的失焦把成功截图改成取消。
    private void Finish(DialogResult result) { finishing = true; DialogResult = result; Close(); }

    // 关闭窗口时先标记，避免失焦事件再次递归关闭。
    protected override void OnFormClosing(FormClosingEventArgs e) { finishing = true; base.OnFormClosing(e); }

    // 未选区域变暗，选区以原始像素显示，并在光标旁展示尺寸提示。
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.DrawImageUnscaled(desktopImage, 0, 0);
        using var dim = new SolidBrush(Color.FromArgb(115, Color.Black));
        e.Graphics.FillRectangle(dim, ClientRectangle);
        if (Selected.Width > 0 && Selected.Height > 0)
        {
            e.Graphics.DrawImage(desktopImage, Selected, Selected, GraphicsUnit.Pixel);
            using var outline = new Pen(Color.FromArgb(32, 208, 178), 2);
            e.Graphics.DrawRectangle(outline, Selected);
        }
        string hint = instructions + (dragging ? $"  {Selected.Width} × {Selected.Height}" : "");
        Size text = TextRenderer.MeasureText(hint, Font);
        Point pointer = PointToClient(Cursor.Position);
        var label = new Rectangle(Math.Clamp(pointer.X + 18, 0, Math.Max(0, ClientSize.Width - text.Width - 20)),
            Math.Clamp(pointer.Y + 24, 0, Math.Max(0, ClientSize.Height - text.Height - 16)), text.Width + 20, text.Height + 16);
        e.Graphics.FillRectangle(Brushes.Black, label);
        TextRenderer.DrawText(e.Graphics, hint, Font, Rectangle.Inflate(label, -10, -8), Color.White);
    }

    // 将框选窗体定位到虚拟桌面的物理像素范围。
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
