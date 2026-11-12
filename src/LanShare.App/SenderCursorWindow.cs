using System.ComponentModel;
using System.Runtime.InteropServices;

namespace LanShare.App;

// 控制另一台电脑期间，用不激活的分层窗口接收本地悬停并隐藏其客户区指针。
internal sealed class SenderCursorWindow : NativeWindow, IDisposable
{
    private bool showing;
    // 确认接管后覆盖虚拟桌面。透明度取最小非零值，使鼠标命中本窗口。
    internal bool TryShow(Rectangle desktop)
    {
        if (Handle != 0) return true;
        try
        {
            CreateHandle(new CreateParams
            {
                Caption = "SimpleLanShare.SenderCursor", Style = unchecked((int)0x80000000), // WS_POPUP
                ExStyle = 0x08080088, // WS_EX_NOACTIVATE | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_TOPMOST
                X = desktop.X, Y = desktop.Y, Width = desktop.Width, Height = desktop.Height
            });
            if (SetLayeredWindowAttributes(Handle, 0, 1, 2) &&
                SetWindowPos(Handle, new IntPtr(-1), desktop.X, desktop.Y, desktop.Width, desktop.Height, 0x0050) &&
                RedrawWindow(Handle, 0, 0, 0x105))
            {
                showing = true;
                SetCursor(0);
                return true;
            }
        }
        catch (Win32Exception) { /* 窗口无法创建时撤销本次跨屏。 */ }
        Dispose();
        return false;
    }

    // 仅处理本窗口上的指针；保持原来的键盘焦点和系统指针主题。
    protected override void WndProc(ref System.Windows.Forms.Message message)
    {
        if (message.Msg == 0x0014 && GetClientRect(message.HWnd, out var rect)) // WM_ERASEBKGND
        {
            FillRect(message.WParam, ref rect, GetStockObject(4)); // BLACK_BRUSH
            message.Result = 1; return;
        }
        if (message.Msg == 0x0020) // WM_SETCURSOR
        {
            SetCursor(0); message.Result = 1; return;
        }
        if (message.Msg == 0x0021) // WM_MOUSEACTIVATE
        {
            message.Result = 3; return; // MA_NOACTIVATE
        }
        base.WndProc(ref message);
    }

    // 销毁即解除覆盖，指针随下一次定位交回实际窗口；重复清理也安全。
    public void Dispose()
    {
        if (showing)
        {
            showing = false;
            SetCursor(Cursors.Default.Handle);
        }
        if (Handle != 0) DestroyHandle();
    }

    // 设置分层窗口的整体透明度。
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(nint window, uint color, byte alpha, uint flags);
    // 置顶并显示窗口，同时保留原有前台窗口。
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    // 空句柄表示隐藏当前窗口的鼠标指针。
    [DllImport("user32.dll")] private static extern nint SetCursor(nint cursor);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    // 立即绘制分层表面，使其从第一次跨屏起便能参与命中测试。
    [DllImport("user32.dll")] private static extern bool RedrawWindow(nint window, nint rect, nint region, uint flags);
    // 获取客户区范围供背景填充使用。
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint window, out Rect rect);
    // 在系统提供的设备上下文中填充背景。
    [DllImport("user32.dll")] private static extern int FillRect(nint dc, ref Rect rect, nint brush);
    // 获取系统管理的画刷，无需由窗口释放。
    [DllImport("gdi32.dll")] private static extern nint GetStockObject(int index);
}
