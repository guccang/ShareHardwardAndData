using LanShare.Core;

namespace LanShare.App;

// 已经通过配对并完整落盘的发布包才进入更新流程，验证与解压不占用界面线程。
internal sealed partial class MainForm
{
    private readonly SemaphoreSlim updateGate = new(1);
    private bool updateStarted;
    internal Action? AfterRuntimeReady { get; set; }

    // 串行准备收到的更新；停用共享、清除信任或退出期间不开始替换。
    private async Task CheckUpdateAsync(PeerNetwork connection, string peerId, string path)
    {
        if (!UpdatePackage.IsCandidate(path)) return;
        await updateGate.WaitAsync();
        string? stage = null;
        try
        {
            if (updateStarted || shuttingDown || network != connection || !connection.TrustedIds.Contains(peerId)) return;
            string program = Environment.ProcessPath!;
            if (!Path.GetFileName(program).Equals(UpdatePackage.Exe, StringComparison.OrdinalIgnoreCase) ||
                File.Exists(Path.ChangeExtension(program, ".runtimeconfig.json")))
                throw new IOException("自动更新需要从便携版 SimpleLanShare.exe 启动。");
            stage = await Task.Run(() => UpdatePackage.Prepare(path, Path.GetDirectoryName(program)!, BuildIdentity.Version));
            if (shuttingDown || network != connection || !connection.TrustedIds.Contains(peerId)) return;
            if (stage is null) { Log("软件更新", "收到同版或旧版更新包，已保留当前版本。"); return; }
            // 等待正在进行的录屏和本机文件发送完成，避免为了升级中断用户操作。
            if (recording.IsBusy || sends.CurrentCount == 0) Log("软件更新", "新版已验证，等待录屏或文件发送结束。");
            while (recording.IsBusy || sends.CurrentCount == 0)
            {
                await Task.Delay(500);
                if (shuttingDown || network != connection || !connection.TrustedIds.Contains(peerId)) return;
            }
            await Task.Run(() => PortableUpdate.Arm(stage));
            updateStarted = true;
            stage = null; // 助手已接手暂存目录，等待当前实例正常退出。
            Log("软件更新", "新版已验证，正在原目录安装并重启。");
            ExitApplication();
        }
        catch (Exception ex)
        {
            if (!shuttingDown)
            {
                Log("软件更新", "未更新：" + ex.Message);
                tray.ShowBalloonTip(4000, "局域共享 · 未更新", ex.Message, ToolTipIcon.Warning);
            }
        }
        finally
        {
            try { if (stage is not null) UpdatePackage.Cleanup(stage); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            updateGate.Release();
        }
    }
}
