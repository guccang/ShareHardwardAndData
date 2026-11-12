using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using LanShare.Core;

namespace LanShare.App;

// 设备私钥用 Windows DPAPI 加密保存；信任记录仅存储已确认设备的证书指纹。
internal sealed class AppSettings
{
    public string ProtectedIdentity { get; set; } = "";
    public List<string> TrustedDeviceIds { get; set; } = [];
    public string ReceiveFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "SimpleLanShare");
    public bool ClipboardEnabled { get; set; } = true;
    public bool AutoAccept { get; set; } = true;
    public bool LaunchAtLogin { get; set; } = true;
    public bool InputEnabled { get; set; }
    public List<string> InputOrder { get; set; } = [];
    public List<string> InputAllowed { get; set; } = [];
    public Dictionary<string, string> InputNames { get; set; } = [];
    public Keys InputReturnKey { get; set; } = Keys.Home;
    public int ScreenshotShortcut { get; set; } = (int)ScreenshotHotkey.DefaultShortcut;
    public int RecordingShortcut { get; set; } = (int)(Keys.Control | Keys.Alt | Keys.R);
    private static readonly string FilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SimpleLanShare", "settings.json");

    // 损坏或旧版本配置不会阻止启动。
    public static AppSettings Load()
    {
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    // 用临时文件替换设置，避免异常退出留下半份 JSON。
    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath + ".tmp", JsonSerializer.Serialize(this));
        File.Move(FilePath + ".tmp", FilePath, true);
    }

    // 私钥无法解密或迁移到其他用户时生成新身份，清除旧信任，必须重新确认。
    public X509Certificate2 ReadIdentity()
    {
        TrustedDeviceIds ??= [];
        InputAllowed ??= []; InputOrder ??= []; InputNames ??= [];
        InputAllowed.RemoveAll(id => !TrustedDeviceIds.Contains(id));
        try
        {
            if (!string.IsNullOrEmpty(ProtectedIdentity))
            {
                byte[] bytes = Convert.FromBase64String(Protect(ProtectedIdentity, true));
                try
                {
                    var certificate = Security.ImportCertificate(bytes);
                    if (certificate.HasPrivateKey && certificate.NotAfter.ToUniversalTime() > DateTime.UtcNow.AddDays(1)) return certificate;
                    certificate.Dispose();
                }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or System.ComponentModel.Win32Exception) { }
        TrustedDeviceIds.Clear();
        InputAllowed.Clear(); InputOrder.Clear(); InputNames.Clear();
        var fresh = Security.CreateCertificate();
        byte[] pfx = fresh.Export(X509ContentType.Pfx);
        try { ProtectedIdentity = Protect(Convert.ToBase64String(pfx), false); }
        finally { CryptographicOperations.ZeroMemory(pfx); }
        return fresh;
    }
    // DPAPI 密钥由 Windows 当前用户保管，不向其他设备发送私钥。
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
