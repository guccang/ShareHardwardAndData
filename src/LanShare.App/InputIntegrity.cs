using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace LanShare.App;

// 比较前台窗口与局域共享的实际完整性级别，不按程序名称推测权限。
internal static class InputIntegrity
{
    internal static readonly int? Local = ReadProcess(Environment.ProcessId);

    // 只在接管、心跳或系统拒绝输入时查询，不在每个鼠标移动事件里打开进程。
    internal static bool ForegroundIsHigher()
    {
        nint window = GetForegroundWindow();
        if (window == 0 || GetWindowThreadProcessId(window, out int pid) == 0 || pid == Environment.ProcessId || Local is null)
            return false;
        return ReadProcess(pid) > Local;
    }

    // 只读查询目标令牌；进程退出或无法查询时保留原有 SendInput 错误处理。
    internal static int? ReadProcess(int pid)
    {
        using var process = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
        if (process.IsInvalid || !OpenProcessToken(process, TokenAccessLevels.Query, out var token)) return null;
        using (token) return ReadLevel(token);
    }

    // TOKEN_MANDATORY_LABEL 的首字段指向 S-1-16-RID；SID 最大长度及结构头可放入该缓冲区。
    private static unsafe int? ReadLevel(SafeAccessTokenHandle token)
    {
        byte* buffer = stackalloc byte[128];
        if (!GetTokenInformation(token, 25, (nint)buffer, 128, out _)) return null;
        nint sid = *(nint*)buffer;
        if (sid == 0 || Marshal.ReadByte(GetSidSubAuthorityCount(sid)) != 1) return null;
        return Marshal.ReadInt32(GetSidSubAuthority(sid, 0));
    }

    // 获取当前前台窗口及其进程编号。
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out int pid);
    // 用查询权限打开进程及令牌，安全句柄负责释放。
    [DllImport("kernel32.dll")] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("advapi32.dll")] private static extern bool OpenProcessToken(SafeProcessHandle process, TokenAccessLevels access, out SafeAccessTokenHandle token);
    // 读取完整性标签和它的相对标识符。
    [DllImport("advapi32.dll")] private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int type, nint data, int length, out int used);
    [DllImport("advapi32.dll")] private static extern nint GetSidSubAuthorityCount(nint sid);
    [DllImport("advapi32.dll")] private static extern nint GetSidSubAuthority(nint sid, uint index);
}
