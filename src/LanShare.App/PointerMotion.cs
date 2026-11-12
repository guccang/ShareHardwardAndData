namespace LanShare.App;

// 跟踪低级鼠标事件的实际位置，包含系统完成回中或跨屏返回时产生的定位事件。
internal sealed class PointerMotion
{
    private Point? last;

    // 返回此事件相对上一事件的位移；首次观察只设置基线。
    internal Point Observe(Point point, bool positioning = false)
    {
        Point previous = last ?? point;
        last = point;
        return positioning ? Point.Empty : new Point(point.X - previous.X, point.Y - previous.Y);
    }
}
