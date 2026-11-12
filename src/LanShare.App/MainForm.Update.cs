using LanShare.Core;

namespace LanShare.App;

// 收发两端共用签名校验和安装流程，验证与解压不占用界面线程。
internal sealed partial class MainForm
{
    private readonly SemaphoreSlim updateGate = new(1);
    private bool updateStarted;
    internal string? ArmedUpdateStage { get; private set; }
    internal Action? AfterRuntimeReady { get; set; }

    // 发送端保留对方已确认的文件摘要；接收端已在落盘前核对过摘要。
    private sealed record UpdateCandidate(string Path, string? SentDigest);

    // 按签名清单选择本批最高版本，停用共享、撤销来源设备信任或退出时取消安装。
    private async Task CheckUpdatesAsync(PeerNetwork connection, string? sourcePeer, IEnumerable<UpdateCandidate> packages)
    {
        // 本机主动发送的文件无需依赖某一台接收电脑继续在线。
        bool CanUpdate() => !shuttingDown && network == connection &&
            (sourcePeer is null || connection.TrustedIds.Contains(sourcePeer));

        await updateGate.WaitAsync();
        string? stage = null;
        bool ownsSendGate = false;
        try
        {
            if (updateStarted || !CanUpdate()) return;
            string program = Environment.ProcessPath!;
            if (!Path.GetFileName(program).Equals(UpdatePackage.Exe, StringComparison.OrdinalIgnoreCase) ||
                File.Exists(Path.ChangeExtension(program, ".runtimeconfig.json")))
                throw new IOException("自动更新需要从便携版 SimpleLanShare.exe 启动。");
            var bestVersion = BuildIdentity.Version;
            foreach (var package in packages)
            {
                try
                {
                    var prepared = await Task.Run(() => UpdatePackage.Prepare(package.Path,
                        Path.GetDirectoryName(program)!, bestVersion, package.SentDigest));
                    if (prepared is { } newer)
                    {
                        string? previous = stage;
                        stage = newer.Directory;
                        bestVersion = newer.Version;
                        if (previous is not null) UpdatePackage.Cleanup(previous);
                    }
                    else if (stage is null) Log("软件更新", "更新包为同版或旧版，已保留当前版本。");
                }
                catch (Exception ex) { ReportUpdateError(Path.GetFileName(package.Path) + " · " + ex.Message); }
                if (!CanUpdate()) return;
            }
            if (stage is null) return;
            // 等候已排队的文件和录屏结束，再独占发送队列直至助手接手。
            if (recording.IsBusy || sends.CurrentCount == 0) Log("软件更新", "新版已验证，等待录屏或文件发送结束。");
            while (recording.IsBusy || !sends.Wait(0))
            {
                await Task.Delay(250);
                if (!CanUpdate()) return;
            }
            ownsSendGate = true;
            updateStarted = true;
            try { await Task.Run(() => PortableUpdate.Arm(stage)); }
            catch { updateStarted = false; throw; }
            ArmedUpdateStage = stage;
            stage = null; // 助手已接手暂存目录，等待当前实例正常退出。
            Log("软件更新", "新版已验证，正在原目录安装并重启。");
            ExitApplication();
        }
        catch (Exception ex) { ReportUpdateError(ex.Message); }
        finally
        {
            try { if (stage is not null) UpdatePackage.Cleanup(stage); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            if (ownsSendGate) sends.Release();
            updateGate.Release();
        }
    }

    // 将校验或安装错误显示在活动记录和托盘中。
    private void ReportUpdateError(string reason)
    {
        if (shuttingDown) return;
        Log("软件更新", "未更新：" + reason);
        if (!smoke) tray.ShowBalloonTip(4000, "局域共享 · 未更新", reason, ToolTipIcon.Warning);
    }
}
