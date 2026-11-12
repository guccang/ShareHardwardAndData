using System.Diagnostics;

namespace LanShare.App;

// 助手从已经验签的暂存副本运行，等待旧进程正常结束后替换原路径，再等待新版就绪。
internal static class PortableUpdate
{
    // 启动具体的 EXE，所有参数独立传递，兼容包含空格的安装路径。
    private static Process Start(string file, params string[] args)
    {
        var info = new ProcessStartInfo(file) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(file)! };
        foreach (string arg in args) info.ArgumentList.Add(arg);
        return Process.Start(info) ?? throw new IOException("无法启动更新进程。");
    }

    // 使用每次更新独立的本地事件区分助手就绪与新版主窗体就绪。
    private static string EventName(string stage, string phase) => @"Local\SimpleLanShare." + Path.GetFileName(stage) + "." + phase;

    // 确认助手能独立运行且已打开父进程句柄，再请求主窗体正常退出。
    internal static void Arm(string stage)
    {
        using var parent = Process.GetCurrentProcess();
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, EventName(stage, "armed"));
        using var worker = Start(Path.Combine(stage, UpdatePackage.Exe), "--apply-update", parent.Id.ToString(),
            parent.StartTime.ToUniversalTime().Ticks.ToString());
        if (!WaitReady(ready, worker, 30))
        {
            if (!worker.HasExited) { worker.Kill(); worker.WaitForExit(); }
            throw new IOException("更新助手未能就绪，当前程序继续运行。");
        }
    }

    // 助手只接受同一安装目录下正在运行的旧程序，不接受网络或清单指定的安装路径。
    internal static void RunWorker(int parentId, long parentStart)
    {
        string stage = Path.GetDirectoryName(Environment.ProcessPath!)!;
        if (!UpdatePackage.IsStage(stage)) throw new InvalidDataException("更新暂存目录无效。");
        string target = Path.Combine(Directory.GetParent(stage)!.FullName, UpdatePackage.Exe);
        var release = UpdatePackage.VerifyStage(stage);
        using var parent = Process.GetProcessById(parentId);
        if (!string.Equals(parent.MainModule?.FileName, target, StringComparison.OrdinalIgnoreCase) ||
            parent.StartTime.ToUniversalTime().Ticks != parentStart)
            throw new InvalidDataException("原程序身份不匹配。");
        var previousVersion = FileVersionInfo.GetVersionInfo(target).ProductVersion?.Split('+')[0];
        if (!Version.TryParse(previousVersion, out var old) || Version.Parse(release.Version) <= old)
            throw new InvalidDataException("更新版本未高于已安装版本。");
        using var released = new EventWaitHandle(false, EventResetMode.ManualReset, EventName(stage, "released"));
        using (var armed = EventWaitHandle.OpenExisting(EventName(stage, "armed"))) armed.Set();
        if (WaitReady(released, parent, 120)) parent.WaitForExit(1500); // 给正常退出的映像一小段卸载时间。
        else if (!parent.HasExited) throw new IOException("等待原程序退出超时。");
        try
        {
            Install(stage, () => StartAndCheck(target, stage));
        }
        catch
        {
            // 启动已恢复的原版本；备份恢复失败时保留暂存目录供手动恢复。
            if (File.Exists(target)) Start(target, "--background").Dispose();
            throw;
        }
    }

    // 主窗体已关闭、设置已保存且单实例锁已释放，此时允许助手接手原目录。
    internal static void NotifyReleased(string stage)
    {
        if (EventWaitHandle.TryOpenExisting(EventName(stage, "released"), out var released))
            using (released) released.Set();
    }

    // 每个文件单独原子替换。写入或启动失败时按逆序恢复已完成的更改。
    internal static void Install(string stage, Action startAndCheck)
    {
        string install = Directory.GetParent(stage)!.FullName;
        var changed = new List<(string Target, string? Backup)>();
        try
        {
            foreach (string name in UpdatePackage.Files)
            {
                string target = Path.Combine(install, name), staged = Path.Combine(stage, name);
                string pending = staged + ".new";
                File.Copy(staged, pending);
                string? backup = File.Exists(target) ? staged + ".bak" : null;
                if (backup is not null) File.Replace(pending, target, backup);
                else File.Move(pending, target);
                changed.Add((target, backup));
            }
            startAndCheck();
        }
        catch
        {
            foreach (var (target, backup) in changed.AsEnumerable().Reverse())
            {
                if (backup is null) File.Delete(target);
                else File.Replace(backup, target, null);
            }
            throw;
        }
    }

    // 新版必须从原目录启动，并在托盘运行时初始化完成后回报就绪。
    internal static void StartAndCheck(string target, string stage, bool selfTest = false)
    {
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, EventName(stage, "started"));
        using var child = Start(target, selfTest ? "--self-test-restart" : "--updated", Path.GetFileName(stage), Environment.ProcessId.ToString());
        if (WaitReady(ready, child, 60)) return;
        if (!child.HasExited) { child.Kill(); child.WaitForExit(); }
        throw new IOException("新版未能正常启动，正在恢复旧版。");
    }

    // 进程若提前结束，不必等到就绪超时。
    private static bool WaitReady(EventWaitHandle ready, Process process, int seconds)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            if (ready.WaitOne(100)) return true;
            if (process.HasExited) break;
        }
        return false;
    }

    // 新实例完成初始化后应答；等助手退出再删除仍被它占用的暂存 EXE。
    internal static void Started(string name, int workerId)
    {
        if (Path.GetFileName(name) != name || !UpdatePackage.IsStage(name)) return;
        string stage = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath!)!, name);
        if (!EventWaitHandle.TryOpenExisting(EventName(stage, "started"), out var ready)) return;
        using (ready) ready.Set();
        _ = Task.Run(() =>
        {
            try
            {
                try { using var worker = Process.GetProcessById(workerId); if (!worker.WaitForExit(30000)) return; }
                catch (ArgumentException) { /* 助手已经退出。 */ }
                UpdatePackage.Cleanup(stage);
            }
            catch (IOException) { /* 完成更新后占用的文件可以随后手动清理。 */ }
            catch (UnauthorizedAccessException) { }
        });
    }
}
