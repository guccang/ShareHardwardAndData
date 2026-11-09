using System.Drawing.Imaging;

namespace LanShare.App;

// 上次截图预览独立持有图片副本，复制和保存可以反复进行。
internal sealed class ScreenshotPreview : Form
{
    private readonly Bitmap image;
    private readonly Action<Bitmap> copyImage;
    private readonly Label status = new() { AutoSize = true, Margin = new Padding(10, 9, 0, 0) };

    // 只显示用户主动截取的图片，关闭预览不会关闭后台共享程序。
    public ScreenshotPreview(Bitmap bitmap, Icon icon, Action<Bitmap> copy)
    {
        image = bitmap; copyImage = copy;
        Icon = icon; Text = "上次截图 · 预览 / 保存";
        AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96);
        ClientSize = new Size(860, 560); MinimumSize = new Size(540, 360);
        StartPosition = FormStartPosition.CenterScreen; KeyPreview = true;
        Font = new Font("Microsoft YaHei UI", 9);
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 52, Padding = new Padding(8) };
        var copyButton = new Button { Text = "复制 Ctrl+C", AutoSize = true };
        var saveButton = new Button { Text = "保存 Ctrl+S", AutoSize = true };
        copyButton.Click += (_, _) => CopyToClipboard(); saveButton.Click += (_, _) => SaveImage();
        status.Text = $"{image.Width} × {image.Height}";
        bar.Controls.AddRange([copyButton, saveButton, status]);
        Controls.Add(new PictureBox { Dock = DockStyle.Fill, Image = image, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(30, 39, 45) });
        Controls.Add(bar);
    }

    // 预览快捷键只在该窗口内生效，不与全局截图快捷键冲突。
    protected override bool ProcessCmdKey(ref System.Windows.Forms.Message message, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.C)) { CopyToClipboard(); return true; }
        if (keyData == (Keys.Control | Keys.S)) { SaveImage(); return true; }
        if (keyData == Keys.Escape) { Close(); return true; }
        return base.ProcessCmdKey(ref message, keyData);
    }

    // 再次复制进入现有剪贴板同步流程，不创建第二条上传路径。
    private void CopyToClipboard()
    {
        try { copyImage(image); status.Text = "已复制到剪贴板"; }
        catch (Exception ex) { MessageBox.Show(this, "复制失败：" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    // 保存为 PNG，不因另存文件而清空剪贴板或结束预览。
    private void SaveImage()
    {
        using var dialog = new SaveFileDialog
        {
            Filter = "PNG 图片|*.png", DefaultExt = "png", AddExtension = true,
            FileName = "截图_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".png",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try { SavePng(image, dialog.FileName); status.Text = "已保存到本地"; }
        catch (Exception ex) { MessageBox.Show(this, "保存失败：" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    // 文件编码入口同时供无桌面内容的合成图片测试使用。
    internal static void SavePng(Bitmap bitmap, string path) => bitmap.Save(path, ImageFormat.Png);

    // 窗口拥有图片副本，关闭后立即释放 GDI 位图。
    protected override void Dispose(bool disposing) { base.Dispose(disposing); if (disposing) image.Dispose(); }
}
