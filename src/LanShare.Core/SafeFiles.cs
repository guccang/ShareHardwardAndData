namespace LanShare.Core;

// 文件只保存到用户选择的接收目录，不允许远端指定路径或覆盖已有文件。
public static class SafeFiles
{
    public const long MaxFileSize = 10L * 1024 * 1024 * 1024;
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
      "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "CONIN$", "CONOUT$" };

    // 使用显式 Windows 规则，测试即使运行在其他平台也能拒绝路径穿越和设备名。
    public static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 180 || name is "." or ".." ||
            name.EndsWith('.') || name.EndsWith(' ') || name.Any(c => c < 32 || "<>:\"/\\|?*".Contains(c)) ||
            Reserved.Contains(name.Split('.')[0].TrimEnd()))
            throw new InvalidDataException("文件名无效或过长。");
    }

    // 原子重命名完成文件，重名时自动添加序号；中间文件不会被当作完成文件显示。
    public static string Commit(string temporary, string folder, string name)
    {
        ValidateName(name);
        for (int number = 0; number < 10000; number++)
        {
            string candidate = number == 0 ? name : $"{Path.GetFileNameWithoutExtension(name)} ({number}){Path.GetExtension(name)}";
            string path = Path.Combine(folder, candidate);
            try { File.Move(temporary, path, false); return path; }
            catch (IOException) when (File.Exists(path) || Directory.Exists(path)) { }
        }
        throw new IOException("同名文件过多，无法保存。");
    }
}
