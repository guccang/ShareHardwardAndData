namespace LanShare.App;

// 框选坐标使用截图位图的物理像素；独立逻辑便于覆盖负坐标显示器及反向拖动。
internal static class ScreenshotGeometry
{
    // 将全局物理坐标转换为位图坐标并裁到虚拟桌面范围。
    internal static Rectangle Selection(Point start, Point end, Rectangle desktop)
    {
        int sx = (int)Math.Clamp((long)start.X - desktop.X, 0, desktop.Width);
        int sy = (int)Math.Clamp((long)start.Y - desktop.Y, 0, desktop.Height);
        int ex = (int)Math.Clamp((long)end.X - desktop.X, 0, desktop.Width);
        int ey = (int)Math.Clamp((long)end.Y - desktop.Y, 0, desktop.Height);
        return Rectangle.FromLTRB(Math.Min(sx, ex), Math.Min(sy, ey), Math.Max(sx, ex), Math.Max(sy, ey));
    }

    // 对异常显示配置设置内存上限；截图位图只在本次框选期间存在。
    internal static void ValidateDesktop(Rectangle desktop)
    {
        if (desktop.Width <= 0 || desktop.Height <= 0 || desktop.Width > 32768 || desktop.Height > 32768 ||
            (long)desktop.Width * desktop.Height > 80_000_000)
            throw new InvalidOperationException("当前桌面尺寸过大，无法安全截图；请减少显示器或分辨率后重试。");
    }
}
