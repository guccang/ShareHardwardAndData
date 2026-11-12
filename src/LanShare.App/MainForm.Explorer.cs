using System.Diagnostics;

namespace LanShare.App;

// 资源管理器操作与主界面共享同一接收目录、设备身份和发送队列。
internal sealed partial class MainForm
{
    private readonly ExplorerSend? initialSend;
    private readonly CancellationTokenSource explorerStop = new();
    private readonly Stopwatch startupAge = Stopwatch.StartNew();

    // 正常启动及配对变化后更新当前用户的右键命令。
    private void ApplyExplorerMenu()
    {
        if (smoke) return;
        try { ExplorerMenu.Apply(settings); }
        catch (Exception ex) { Log("右键菜单", "菜单更新失败：" + ex.Message); }
    }

    // 用户停用时只移除本应用的菜单，注册或保存失败则恢复原选择。
    private void ChangeExplorerMenu(CheckBox toggle)
    {
        bool previous = settings.ExplorerMenuEnabled;
        settings.ExplorerMenuEnabled = toggle.Checked;
        if (smoke) return;
        try { ExplorerMenu.Apply(settings); settings.Save(); }
        catch (Exception ex)
        {
            settings.ExplorerMenuEnabled = previous; toggle.Checked = previous;
            ApplyExplorerMenu();
            MessageBox.Show(this, ex.Message, "右键菜单设置失败");
        }
    }

    // 先投递到已有主窗体，再确认请求；接收连接不等候文件传输结束。
    private async Task ListenForExplorerAsync()
    {
        try
        {
            await ExplorerSendPipe.ListenAsync(ExplorerSendPipe.Name, EnqueueExplorerSend, explorerStop.Token);
        }
        catch (OperationCanceledException) when (explorerStop.IsCancellationRequested) { }
        catch (Exception ex) { if (!shuttingDown) Log("右键菜单", "无法接收文件发送请求：" + ex.Message); }
    }

    // 只有成功投递到当前运行的窗口才向助手回执。
    private bool EnqueueExplorerSend(ExplorerSend request)
    {
        if (shuttingDown || !IsHandleCreated) return false;
        try { BeginInvoke(new Action(() => _ = SendFromExplorerAsync(request))); return true; }
        catch (InvalidOperationException) { return false; }
    }

    // 冷启动给已配对设备留出发现时间；目标失效时绝不发送到其他在线设备。
    private async Task SendFromExplorerAsync(ExplorerSend request)
    {
        if (shuttingDown) return;
        var current = network;
        while (current is not null && network == current && settings.TrustedDeviceIds.Contains(request.PeerId)
            && startupAge.Elapsed < TimeSpan.FromSeconds(10) && !current.Sessions.Any(p => p.IsConnected && p.PeerId == request.PeerId))
        {
            await Task.Delay(250);
            if (shuttingDown) return;
        }
        if (!settings.TrustedDeviceIds.Contains(request.PeerId) || current != network)
        {
            string text = "配对或共享连接已变更，文件未发送。";
            Log("发送队列", text);
            tray.ShowBalloonTip(3500, "局域共享 · 文件发送", text, ToolTipIcon.Warning);
            return;
        }
        await QueueFilesAsync([request.FilePath], request.PeerId);
    }
}
