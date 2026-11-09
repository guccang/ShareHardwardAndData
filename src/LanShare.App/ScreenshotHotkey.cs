using System.Runtime.InteropServices;

namespace LanShare.App;

// 截图和录屏各持有独立的全局快捷键窗口，断网或停止共享后仍可使用。
internal sealed class ScreenshotHotkey : NativeWindow, IDisposable
{
    internal const Keys DefaultShortcut = Keys.Control | Keys.Alt | Keys.A;
    private int activeId;
    private bool disposed;
    private readonly string actionName;
    public Keys Shortcut { get; private set; }
    public bool Registered => activeId != 0;
    public string? Error { get; private set; }
    public string Label => Describe(Shortcut);
    public event Action? Pressed;

    // 隐藏消息窗口接收 WM_HOTKEY，测试和预览模式可跳过真实注册。
    public ScreenshotHotkey(Keys shortcut, bool register = true, string actionName = "截图", Keys fallback = DefaultShortcut)
    {
        this.actionName = actionName;
        CreateHandle(new CreateParams { Caption = "SimpleLanShare.Hotkey." + actionName, Parent = new IntPtr(-3) });
        Shortcut = IsValid(shortcut) ? shortcut : fallback;
        if (register) Error = Apply(Shortcut);
    }

    // 限制为修饰键加字母、数字或功能键，避免占用普通输入。
    internal static bool IsValid(Keys value)
    {
        Keys key = value & Keys.KeyCode, modifiers = value & Keys.Modifiers;
        return modifiers != Keys.None && (modifiers & ~(Keys.Control | Keys.Alt | Keys.Shift)) == 0 &&
            ((key >= Keys.A && key <= Keys.Z) || (key >= Keys.D0 && key <= Keys.D9) || (key >= Keys.F1 && key <= Keys.F24));
    }

    // 固定快捷键展示格式，避免不同语言系统显示不同的枚举名字。
    internal static string Describe(Keys value)
    {
        var parts = new List<string>();
        if ((value & Keys.Control) != 0) parts.Add("Ctrl");
        if ((value & Keys.Alt) != 0) parts.Add("Alt");
        if ((value & Keys.Shift) != 0) parts.Add("Shift");
        var key = value & Keys.KeyCode;
        parts.Add(key is >= Keys.D0 and <= Keys.D9 ? ((int)key - (int)Keys.D0).ToString() : key.ToString());
        return string.Join("+", parts);
    }

    // 先注册并保存新快捷键，再释放旧键；冲突或保存失败时旧快捷键继续有效。
    public string? Apply(Keys value, Action<Keys>? persist = null)
    {
        if (disposed) return actionName + "快捷键已关闭。";
        if (!IsValid(value)) return "请选择 Ctrl / Alt / Shift 中至少一个修饰键，以及一个字母、数字或功能键。";
        if (Registered && value == Shortcut) { Error = null; return null; }
        int next = activeId == 1 ? 2 : 1;
        uint modifiers = 0x4000;
        if ((value & Keys.Alt) != 0) modifiers |= 1;
        if ((value & Keys.Control) != 0) modifiers |= 2;
        if ((value & Keys.Shift) != 0) modifiers |= 4;
        if (!RegisterHotKey(Handle, next, modifiers, (uint)(value & Keys.KeyCode)))
            return Error = "快捷键已被占用，请更换组合；仍可从托盘菜单" + actionName + "。";
        try { persist?.Invoke(value); }
        catch (Exception ex) { UnregisterHotKey(Handle, next); return "无法保存快捷键：" + ex.Message; }
        if (Registered) UnregisterHotKey(Handle, activeId);
        activeId = next; Shortcut = value; Error = null;
        return null;
    }

    // 系统消息只触发对应的本机操作，不进入远程键鼠通道。
    protected override void WndProc(ref System.Windows.Forms.Message message)
    {
        if (message.Msg == 0x0312 && message.WParam.ToInt32() == activeId && Registered) Pressed?.Invoke();
        base.WndProc(ref message);
    }

    // 明确退出时释放系统热键，其他软件可立即使用该组合。
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (Registered) UnregisterHotKey(Handle, activeId);
        activeId = 0; DestroyHandle();
    }

    // 向 Windows 登记全局组合键，由隐藏消息窗口接收触发事件。
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    // 释放指定的系统热键注册，不影响其他程序的组合键。
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr window, int id);
}
