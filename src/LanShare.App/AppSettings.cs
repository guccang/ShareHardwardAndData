using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace LanShare.App;

// 设置只保存到当前用户目录；共享码通过 Windows DPAPI 加密后落盘。
internal sealed class AppSettings
{
    public string ProtectedCode { get; set; } = "";
    public string ReceiveFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "SimpleLanShare");
    public bool ClipboardEnabled { get; set; } = true;
    public bool AutoAccept { get; set; } = true;
    private static readonly string FilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SimpleLanShare", "settings.json");

    // 损坏或旧版本配置不会阻止启动。
    public static AppSettings Load()
    {
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    // 用临时文件替换设置，避免异常退出留下半份 JSON。
    public void Save(string code)
    {
        ProtectedCode = Protect(code, false);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath + ".tmp", JsonSerializer.Serialize(this));
        File.Move(FilePath + ".tmp", FilePath, true);
    }

    // 设置移到另一 Windows 用户或电脑时，无法解密就生成新共享码。
    public string ReadCode()
    {
        try { return ProtectedCode.Length == 0 ? LanShare.Core.Security.NewPairingCode() : Protect(ProtectedCode, true); }
        catch { return LanShare.Core.Security.NewPairingCode(); }
    }

    // DPAPI 密钥由 Windows 当前用户保管；界面仍可显式复制共享码供其他设备加入。
    internal static string Protect(string value, bool decrypt)
    {
        byte[] bytes = decrypt ? Convert.FromBase64String(value) : Encoding.UTF8.GetBytes(value);
        var input = new Blob { Size = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        Blob output = default;
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            bool success = decrypt ? CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!success) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            byte[] result = new byte[output.Size]; Marshal.Copy(output.Data, result, 0, result.Length);
            return decrypt ? Encoding.UTF8.GetString(result) : Convert.ToBase64String(result);
        }
        finally { Marshal.FreeHGlobal(input.Data); if (output.Data != IntPtr.Zero) LocalFree(output.Data); Array.Clear(bytes); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)] private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
