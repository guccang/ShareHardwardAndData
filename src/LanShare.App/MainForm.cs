using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using LanShare.Core;
using System.Security.Cryptography.X509Certificates;

namespace LanShare.App;

// 原生 Windows 桌面界面：设备配对、文件投递区和接收列表集中在同一窗口。
internal sealed partial class MainForm : Form
{
    private readonly AppSettings settings;
    private readonly ClipboardBridge clipboard;
    private readonly X509Certificate2 identity;
    private readonly ScreenshotHotkey screenshotHotkey;
    private readonly ScreenshotHotkey recordingHotkey;
    private readonly ScreenshotController screenshots;
    private readonly ScreenRecordingController recording = new();
    private readonly Button recordButton = MakeButton("区域录屏", width: 115);
    private readonly System.Windows.Forms.Timer recordingTimer = new() { Interval = 1000 };
    private bool closingRecording;
    private readonly CheckBox startup = new() { Text = "开机自启动（后台）", AutoSize = true };
    private readonly Button captureButton = MakeButton("截图 Ctrl+Alt+A", width: 180);
    private bool suppressFirstShow, runtimeInitialized, updatingStartup, shortcutSettingsOpen;
    private readonly TextBox folder = new() { ReadOnly = true, Dock = DockStyle.Fill };
    private readonly TextBox host = new() { PlaceholderText = "对方的局域网 IP", Width = 165 };
    private readonly Button start = MakeButton("开启共享", true);
    private readonly Button manual = MakeButton("连接 IP");
    private readonly Button browse = MakeButton("更改目录");
    private readonly CheckBox sync = new() { Text = "同步文字和图片剪贴板", AutoSize = true };
    private readonly CheckBox accept = new() { Text = "自动接收已配对设备文件", AutoSize = true };
    private readonly ComboBox targets = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 210 };
    private readonly Label state = new() { AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label detail = new() { AutoSize = false, Dock = DockStyle.Fill, ForeColor = Color.FromArgb(91, 110, 127) };
    private readonly ListView history = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true };
    private readonly ListView received = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = true };
    private readonly NotifyIcon tray = new() { Icon = SystemIcons.Application, Text = "局域共享 · 未开启", Visible = true };
    private readonly Dictionary<string, ListViewItem> progressRows = new();
    private readonly SemaphoreSlim sends = new(1);
    private PeerNetwork? network;
    private ClipboardContent? latestClipboard;
    private bool broadcasting, shuttingDown, exitRequested, closeHintShown;
    private readonly Icon appIcon = LoadAppIcon();
    private readonly bool smoke;
    private readonly string baseTitle = "局域共享 · Simple LAN Share";

    // 正常启动自动发现设备；首次确认前不交换剪贴板或文件。
    public MainForm(bool smoke = false, bool startInTray = false)
    {
        this.smoke = smoke; suppressFirstShow = startInTray;
        settings = smoke ? new AppSettings() : AppSettings.Load();
        identity = settings.ReadIdentity();
        folder.Text = settings.ReceiveFolder;
        sync.Checked = settings.ClipboardEnabled; accept.Checked = settings.AutoAccept;
        AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
        Text = baseTitle; ClientSize = new Size(1040, 720); MinimumSize = new Size(940, 660);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 9F);
        BackColor = Color.FromArgb(242, 246, 249);
        ForeColor = Color.FromArgb(28, 47, 65);
        Icon = appIcon; tray.Icon = appIcon;
        screenshots = new ScreenshotController(appIcon);
        screenshotHotkey = new ScreenshotHotkey((Keys)settings.ScreenshotShortcut, !smoke);
        recordingHotkey = new ScreenshotHotkey((Keys)settings.RecordingShortcut, !smoke, "录屏", Keys.Control | Keys.Alt | Keys.R);
        recording.ShortcutLabel = recordingHotkey.Label;
        startup.Checked = settings.LaunchAtLogin;
        InitializeInput();
        BuildLayout();
        clipboard = new ClipboardBridge();
        clipboard.Changed += content => { latestClipboard = content; _ = BroadcastClipboardAsync(); };
        clipboard.Notice += text => { detail.Text = text; };
        startup.CheckedChanged += (_, _) => ChangeStartupSetting();
        captureButton.Click += async (_, _) => await CaptureScreenshotAsync();
        screenshotHotkey.Pressed += async () => { if (inputRouter.Mode != ControlMode.Sending) await CaptureScreenshotAsync(); };
        recordButton.Click += async (_, _) => await ToggleRecordingAsync();
        recordingHotkey.Pressed += async () => { if (inputRouter.Mode != ControlMode.Sending) await ToggleRecordingAsync(); };
        recording.Changed += RefreshRecordingState;
        recording.Notice += text => { Log("录屏", text); recordingMessage.Text = text; if (!shuttingDown && !recording.IsRecording) tray.ShowBalloonTip(4000, "区域录屏", text, text.Contains("失败") ? ToolTipIcon.Warning : ToolTipIcon.Info); };
        recordingTimer.Tick += (_, _) => RefreshRecordingState();
        Microsoft.Win32.SystemEvents.SessionSwitch += OnSessionSwitch;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanging += OnDisplaySettingsChanging;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        screenshots.Notice += text => { Log("截图", text); screenshotMessage.Text = text; if (!Visible && (text.Contains("失败") || text.Contains("占用"))) tray.ShowBalloonTip(3000, "截图", text, ToolTipIcon.Warning); };
        start.Click += (_, _) => ToggleSharing();
        browse.Click += (_, _) => ChooseFolder();
        manual.Click += async (_, _) => await ConnectManuallyAsync();
        sync.CheckedChanged += (_, _) => { clipboard.Enabled = sync.Checked && network is not null; if (!sync.Checked) latestClipboard = null; };
        received.ItemDrag += (_, _) => DragReceivedFiles();
        received.DoubleClick += (_, _) => ShowReceivedLocation();
        tray.DoubleClick += (_, _) => RestoreWindow();
        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("打开局域共享", null, (_, _) => RestoreWindow());
        trayMenu.Items.Add("键鼠返回本机", null, (_, _) => inputRouter.ReturnLocal("已从托盘返回本机"));
        var captureItem = trayMenu.Items.Add("截图", null, async (_, _) => await CaptureScreenshotAsync());
        var lastItem = trayMenu.Items.Add("查看 / 保存上次截图", null, (_, _) => screenshots.ShowLast());
        trayMenu.Items.Add("截图快捷键…", null, (_, _) => ConfigureScreenshot());
        var recordItem = trayMenu.Items.Add("区域录屏…", null, async (_, _) => await ToggleRecordingAsync());
        trayMenu.Items.Add("录屏快捷键…", null, (_, _) => ConfigureRecording());
        var videoItem = trayMenu.Items.Add("打开上次录屏位置", null, (_, _) => recording.ShowLast());
        trayMenu.Items.Add(new ToolStripSeparator());
        var startupItem = new ToolStripMenuItem("开机自启动") { CheckOnClick = true };
        startupItem.CheckedChanged += (_, _) => { if (!updatingStartup) startup.Checked = startupItem.Checked; };
        trayMenu.Items.Add(startupItem);
        trayMenu.Opening += (_, _) =>
        {
            captureItem.Text = "截图 (" + screenshotHotkey.Label + ")"; captureItem.Enabled = !screenshots.IsCapturing && !recording.IsBusy;
            recordItem.Text = recording.IsStopping ? "正在保存录屏…" : recording.IsBusy ? "停止录屏并保存" : "区域录屏 (" + recordingHotkey.Label + ")";
            recordItem.Enabled = !shuttingDown && !screenshots.IsCapturing && !shortcutSettingsOpen && !recording.IsStopping;
            videoItem.Enabled = recording.LastVideo is not null;
            lastItem.Enabled = screenshots.HasImage; updatingStartup = true; startupItem.Checked = startup.Checked; updatingStartup = false;
        };
        trayMenu.Items.Add("退出", null, (_, _) => ExitApplication());
        tray.ContextMenuStrip = trayMenu;
        Resize += (_, _) => { if (WindowState == FormWindowState.Minimized && !smoke) Hide(); };
        RefreshPeers();
        UpdateState("自动发现 · 首次连接需要双方确认", "各台电脑打开软件即可；确认过的设备下次自动连接，无需共享码。");
        RefreshShortcutLabels();
        Shown += (_, _) => InitializeRuntime();
    }

    // 第一次显示被真正抑制，后台启动不先闪现窗口；消息循环仍由主窗体保持。
    protected override void SetVisibleCore(bool value)
    {
        if (value && suppressFirstShow)
        {
            suppressFirstShow = false; ShowInTaskbar = false;
            _ = Handle;
            BeginInvoke(new Action(InitializeRuntime));
            base.SetVisibleCore(false); return;
        }
        base.SetVisibleCore(value);
    }

    // 无论隐藏启动还是打开窗口都只初始化一次，恢复窗口不会反向停止已建立连接。
    private void InitializeRuntime()
    {
        if (runtimeInitialized || shuttingDown) return;
        runtimeInitialized = true;
        if (smoke) return;
        try { StartupRegistration.SetEnabled(settings.LaunchAtLogin, StartupRegistration.CurrentCommand()); settings.Save(); }
        catch (Exception ex) { Log("自启动设置失败", ex.Message); tray.ShowBalloonTip(3000, "自启动设置失败", "可打开窗口重新设置：" + ex.Message, ToolTipIcon.Warning); }
        ToggleSharing();
        if (recordingHotkey.Error is not null) { Log("录屏快捷键", recordingHotkey.Error); recordingMessage.Text = recordingHotkey.Error; tray.ShowBalloonTip(3000, "录屏快捷键未注册", recordingHotkey.Error, ToolTipIcon.Warning); }
        if (screenshotHotkey.Error is not null) { Log("截图快捷键", screenshotHotkey.Error); tray.ShowBalloonTip(3000, "截图快捷键未注册", screenshotHotkey.Error, ToolTipIcon.Warning); }
        else if (!Visible) tray.ShowBalloonTip(2000, "局域共享已在后台运行", screenshotHotkey.Label + " 截图；双击托盘图标打开窗口。", ToolTipIcon.Info);
    }

    // 用户选项立即写入当前用户启动项，失败时恢复界面和先前配置。
    private void ChangeStartupSetting()
    {
        if (smoke || updatingStartup) return;
        bool previous = settings.LaunchAtLogin;
        try
        {
            StartupRegistration.SetEnabled(startup.Checked, StartupRegistration.CurrentCommand());
            settings.LaunchAtLogin = startup.Checked; settings.Save();
            Log("自启动", startup.Checked ? "已开启：登录 Windows 后在托盘运行并自动连接" : "已关闭开机自启动");
        }
        catch (Exception ex)
        {
            settings.LaunchAtLogin = previous; updatingStartup = true; startup.Checked = previous; updatingStartup = false;
            try { StartupRegistration.SetEnabled(previous, StartupRegistration.CurrentCommand()); } catch { }
            Log("自启动设置失败", ex.Message);
        }
    }

    // 全局热键、主界面和托盘共用同一截图入口，防止同时创建多个框选遮罩。
    private async Task CaptureScreenshotAsync()
    {
        if (shuttingDown || screenshots.IsCapturing || shortcutSettingsOpen || recording.IsBusy) return;
        bool restore = Visible;
        if (restore) Hide();
        try { await screenshots.CaptureAsync(); }
        finally { if (restore && !shuttingDown && !IsDisposed) RestoreWindow(); }
    }

    // 快捷键保存失败时回滚配置，热键窗口会保留原先的有效组合。
    private void ConfigureScreenshot()
    {
        if (screenshots.IsCapturing || shortcutSettingsOpen || recording.IsBusy) return;
        shortcutSettingsOpen = true;
        try
        {
            using var dialog = new ScreenshotSettingsDialog(screenshotHotkey, value =>
            {
                int previous = settings.ScreenshotShortcut; settings.ScreenshotShortcut = (int)value;
                try { settings.Save(); } catch { settings.ScreenshotShortcut = previous; throw; }
            }, appIcon, "截图", value => value == recordingHotkey.Shortcut ? "截图和录屏必须使用不同的快捷键。" : null);
            dialog.ShowDialog(); RefreshShortcutLabels();
        }
        finally { shortcutSettingsOpen = false; }
    }

    // 录屏快捷键单独持久保存，同时防止与本软件截图快捷键重复。
    private void ConfigureRecording()
    {
        if (screenshots.IsCapturing || shortcutSettingsOpen || recording.IsBusy) return;
        shortcutSettingsOpen = true;
        try
        {
            using var dialog = new ScreenshotSettingsDialog(recordingHotkey, value =>
            {
                int previous = settings.RecordingShortcut; settings.RecordingShortcut = (int)value;
                try { settings.Save(); } catch { settings.RecordingShortcut = previous; throw; }
            }, appIcon, "录屏", value => value == screenshotHotkey.Shortcut ? "录屏和截图必须使用不同的快捷键。" : null);
            dialog.ShowDialog(); recording.ShortcutLabel = recordingHotkey.Label; RefreshShortcutLabels();
        }
        finally { shortcutSettingsOpen = false; }
    }

    // 同一按钮负责开始与停止；录制期间不自动恢复主窗口以免遮挡选区。
    private async Task ToggleRecordingAsync()
    {
        if (shuttingDown || screenshots.IsCapturing || shortcutSettingsOpen || recording.IsStopping) return;
        if (recording.IsBusy) { await recording.StopAsync(); return; }
        bool restore = Visible;
        if (restore) Hide();
        try { await recording.StartAsync(); }
        finally { if (restore && !shuttingDown && !IsDisposed) RestoreWindow(); }
    }

    // 托盘图标和计时提示持续标明录制状态，停止时恢复原有共享状态。
    private void RefreshRecordingState()
    {
        if (IsDisposed) return;
        recordButton.Text = recording.IsStopping ? "正在保存…" : recording.IsBusy ? "停止并保存" : "开始录屏";
        recordButton.Enabled = !shuttingDown && !recording.IsStopping;
        captureButton.Enabled = !recording.IsBusy;
        recording.UpdateOverlay();
        recordingClock.Text = recording.IsStopping ? "正在保存" : recording.IsBusy && !recording.IsRecording ? "准备录制" : recording.Elapsed.ToString(@"hh\:mm\:ss");
        tray.Icon = recording.IsBusy ? SystemIcons.Warning : appIcon;
        if (recording.IsBusy)
        {
            tray.Text = recording.IsStopping ? "局域共享 · 正在保存录屏" : recording.IsRecording ? $"局域共享 · 录屏 {recording.Elapsed:hh\\:mm\\:ss} · 右键停止" : "局域共享 · 正在选择录屏区域";
            recordingTimer.Start();
        }
        else { recordingTimer.Stop(); tray.Text = network is null ? "局域共享 · 未开启" : "局域共享 · 共享中"; }
    }

    // 锁屏后停止并保存，解锁不会自动继续录制。
    private void OnSessionSwitch(object sender, Microsoft.Win32.SessionSwitchEventArgs e)
    {
        Ui(() =>
        {
            if (e.Reason is Microsoft.Win32.SessionSwitchReason.SessionLock or Microsoft.Win32.SessionSwitchReason.ConsoleDisconnect or Microsoft.Win32.SessionSwitchReason.RemoteDisconnect)
            {
                inputLocked = true;
                SetInputRuntime();
                if (recording.IsRecording) _ = recording.StopAsync();
            }
            else if (e.Reason is Microsoft.Win32.SessionSwitchReason.SessionUnlock or Microsoft.Win32.SessionSwitchReason.ConsoleConnect or Microsoft.Win32.SessionSwitchReason.RemoteConnect)
            {
                inputLocked = false; SetInputRuntime();
            }
        });
    }

    // 显示器布局改变后原坐标可能失效，及时停止，避免录到选区之外的屏幕内容。
    private void OnDisplaySettingsChanging(object? sender, EventArgs e) => Ui(() => { inputRouter.ReturnLocal("显示器布局改变"); if (recording.IsRecording) _ = recording.StopAsync(); });

    // 系统完成显示器调整后重新读取虚拟桌面边界。
    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => Ui(SetInputRuntime);

    // 明确退出先完成视频索引，再进入原有资源释放路径。
    private async Task FinishRecordingAndExitAsync()
    {
        await recording.StopAsync(exiting: true);
        ExitApplication();
    }
    // 按钮保留系统焦点与键盘操作语义，主按钮使用统一强调色。
    private static Button MakeButton(string text, bool primary = false, int width = 100) => new()
    {
        Text = text, Width = width, Height = 34, FlatStyle = FlatStyle.Flat,
        BackColor = primary ? Color.FromArgb(17, 112, 103) : Color.White,
        ForeColor = primary ? Color.White : Color.FromArgb(28, 47, 65), Margin = new Padding(3, 1, 3, 1), Cursor = Cursors.Hand
    };

    // 表单标签使用统一内边距。
    private static Label Caption(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(3, 6, 8, 0) };

    // 展示可用于手动连接的 IPv4 地址，不进行外网查询。
    private static string LocalAddresses()
    {
        try { return string.Join(" / ", Dns.GetHostAddresses(Dns.GetHostName()).Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))); }
        catch (SocketException) { return "未检测到 IPv4 地址"; }
    }

    // 开启共享后保存设备身份，并禁止运行中修改接收目录。
    private void ToggleSharing()
    {
        if (network is not null) { StopSharing(); return; }
        try
        {
            Directory.CreateDirectory(folder.Text);
            settings.ReceiveFolder = folder.Text; settings.ClipboardEnabled = sync.Checked; settings.AutoAccept = accept.Checked;
            settings.Save();
            var current = new PeerNetwork(identity, folder.Text, settings.TrustedDeviceIds);
            network = current;
            current.Status += text => Ui(() => { if (network == current) Log("连接", text); });
            current.PeerConnected += peer => AttachPeer(current, peer);
            current.PeerDisconnected += (peer, reason) => Ui(() => { if (network == current) { DetachInput(peer); RefreshPeers(); Log(peer.PeerName, "已断开：" + reason); } });
            current.ConfirmPeerAsync = (request, ct) => ConfirmPeerAsync(current, request, ct);
            current.Start();
            SetInputRuntime();
            clipboard.Enabled = sync.Checked;
            browse.Enabled = false; start.Text = "停止共享";
            tray.Text = "局域共享 · 已开启";
            RefreshPeers();
            UpdateState("共享已开启 · 正在发现电脑", "首次连接会自动弹出确认；核对两台电脑的短校验码后同意，下次自动重连。");
            Log("自动发现", "正在寻找局域网电脑，已配对设备自动连接");
        }
        catch (Exception ex) { StopSharing(); Log("开启失败", ex.Message); if (Visible) MessageBox.Show(this, ex.Message, "无法开启共享", MessageBoxButtons.OK, MessageBoxIcon.Warning); else tray.ShowBalloonTip(3000, "无法开启共享", ex.Message, ToolTipIcon.Warning); }
    }

    // 所有旧会话回调都用所属网络实例过滤，避免重启共享后旧消息污染新状态。
    private void AttachPeer(PeerNetwork current, PeerSession peer)
    {
        BindInput(current, peer);
        peer.ClipboardReceived += content => Ui(() => { if (network == current && peer.IsConnected) clipboard.Receive(content); });
        peer.FileReceived += path => Ui(() =>
        {
            if (network != current) return;
            var item = new ListViewItem([Path.GetFileName(path), peer.PeerName, path]) { Tag = path };
            received.Items.Insert(0, item); Log(peer.PeerName, "已接收：" + Path.GetFileName(path));
            if (!Visible) tray.ShowBalloonTip(2500, "收到文件", peer.PeerName + " → " + Path.GetFileName(path), ToolTipIcon.Info);
        });
        peer.Progress += progress => Ui(() => { if (network == current) ShowProgress(peer, progress); });
        peer.Notice += text => Ui(() => { if (network == current) Log(peer.PeerName, text); });
        peer.ConfirmFileAsync = (offer, ct) => ConfirmFileAsync(current, peer, offer, ct);
        Ui(() =>
        {
            if (network != current) return;
            settings.TrustedDeviceIds = current.TrustedIds.ToList();
            RememberInputPeer(peer);
            try { settings.Save(); } catch (Exception ex) { Log("保存信任失败", ex.Message); }
            RefreshPeers(); Log(peer.PeerName, "已连接并记住此设备，下次自动连接");
        });
    }

    // 两台电脑显示相同校验码，用户只需核对并点击，无需输入或复制共享码。
    private Task<bool> ConfirmPeerAsync(PeerNetwork current, PairingRequest request, CancellationToken ct)
    {
        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = ct.Register(() => result.TrySetCanceled(ct));
        Ui(() =>
        {
            if (result.Task.IsCompleted || network != current) { result.TrySetResult(false); return; }
            var dialog = new Form
            {
                Text = "发现新电脑 · 确认连接", ClientSize = new Size(500, 270), Font = Font,
                StartPosition = FormStartPosition.CenterScreen, FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false, MinimizeBox = false, Icon = Icon
            };
            var info = new Label
            {
                Text = $"{request.Name}\n{request.Address}\n\n请核对两台电脑显示的校验码一致，无需输入：\n\n{request.VerificationCode}\n\n确认后记住此设备，允许同步剪贴板和收发文件。",
                Dock = DockStyle.Fill, Padding = new Padding(20), Font = new Font(Font.FontFamily, 11)
            };
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 45, FlowDirection = FlowDirection.RightToLeft };
            var yes = MakeButton("确认连接", true); var no = MakeButton("拒绝");
            yes.Click += (_, _) => { result.TrySetResult(true); dialog.Close(); };
            no.Click += (_, _) => { result.TrySetResult(false); dialog.Close(); };
            dialog.FormClosed += (_, _) => { result.TrySetResult(false); dialog.Dispose(); };
            buttons.Controls.AddRange([yes, no]); dialog.Controls.Add(info); dialog.Controls.Add(buttons);
            dialog.AcceptButton = yes; dialog.CancelButton = no; dialog.Show(this);
            if (!Visible) tray.ShowBalloonTip(3000, "发现新电脑", request.Name + " 等待连接确认", ToolTipIcon.Info);
            _ = result.Task.ContinueWith(_ => Ui(() => { if (!dialog.IsDisposed) dialog.Close(); }), TaskScheduler.Default);
        });
        _ = result.Task.ContinueWith(_ => registration.Dispose(), TaskScheduler.Default);
        return result.Task;
    }

    // 清除本机信任后暂停共享，下一次连接必须重新确认，不影响磁盘上的已收文件。
    private void ForgetDevices()
    {
        if (MessageBox.Show(this, "清除已确认的设备并停止共享？下次连接需要重新确认。", "清除信任", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        StopSharing(); settings.TrustedDeviceIds.Clear();
        settings.InputAllowed.Clear(); settings.InputOrder.Clear(); settings.InputNames.Clear(); RefreshInputCards();
        try { settings.Save(); Log("配对", "已清除信任，点击开启共享可重新配对"); }
        catch (Exception ex) { Log("设置保存失败", ex.Message); }
    }
    // 默认允许已配对的自有电脑发送；关闭自动接收后使用可超时关闭的独立确认窗。
    private Task<bool> ConfirmFileAsync(PeerNetwork current, PeerSession peer, FileOffer offer, CancellationToken ct)
    {
        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = ct.Register(() => result.TrySetCanceled(ct));
        Ui(() =>
        {
            if (result.Task.IsCompleted || network != current) { result.TrySetResult(false); return; }
            if (accept.Checked) { result.TrySetResult(true); return; }
            var dialog = new Form { Text = "接收文件", ClientSize = new Size(460, 180), StartPosition = FormStartPosition.CenterScreen, Font = Font, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false };
            var label = new Label { Text = $"{peer.PeerName} 想发送文件：\n\n{offer.Name}\n{FormatSize(offer.Size)}", Dock = DockStyle.Fill, Padding = new Padding(18) };
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 42, FlowDirection = FlowDirection.RightToLeft };
            var yes = MakeButton("接收", true); var no = MakeButton("拒绝");
            yes.Click += (_, _) => { result.TrySetResult(true); dialog.Close(); };
            no.Click += (_, _) => { result.TrySetResult(false); dialog.Close(); };
            dialog.FormClosed += (_, _) => { result.TrySetResult(false); dialog.Dispose(); };
            buttons.Controls.AddRange([yes, no]); dialog.Controls.Add(label); dialog.Controls.Add(buttons); dialog.Show(this);
            _ = result.Task.ContinueWith(_ => Ui(() => { if (!dialog.IsDisposed) dialog.Close(); }), TaskScheduler.Default);
        });
        _ = result.Task.ContinueWith(_ => registration.Dispose(), TaskScheduler.Default);
        return result.Task;
    }

    // 剪贴板广播只有一个在途快照；传输期间发生的新复制覆盖尚未发送的旧快照。
    private async Task BroadcastClipboardAsync()
    {
        if (broadcasting) return;
        broadcasting = true;
        try
        {
            while (latestClipboard is not null && network is not null && sync.Checked)
            {
                var content = latestClipboard; latestClipboard = null;
                var peers = network.Sessions.Where(p => p.IsConnected).ToArray();
                await Task.WhenAll(peers.Select(async peer =>
                {
                    try { await Task.Run(() => peer.SendClipboardAsync(content)); }
                    catch (Exception ex) { Ui(() => Log(peer.PeerName, "剪贴板发送失败：" + ex.Message)); }
                }));
                detail.Text = peers.Length == 0 ? "当前没有在线电脑，连接后请重新复制。" : $"{(content.Format == "text" ? "文字" : "图片")}剪贴板已同步到 {peers.Length} 台电脑";
            }
        }
        finally { broadcasting = false; }
    }

    // 拖入时固定目标设备快照；同一批文件分别流式发送到各目标，文件之间保持顺序。
    private async Task QueueFilesAsync(string[] paths)
    {
        var current = network;
        if (current is null) { Log("提示", "请先开启共享并连接其他电脑"); return; }
        var selected = targets.SelectedItem as Target;
        var peers = current.Sessions.Where(p => p.IsConnected && (selected?.Id is null || p.PeerId == selected.Id)).ToArray();
        if (peers.Length == 0) { Log("提示", "没有在线目标电脑"); return; }
        if (paths.Length > 128) { Log("提示", "单批最多发送 128 个文件"); return; }
        var files = paths.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (files.Length != paths.Length) Log("提示", "文件夹暂不支持，请先压缩成 ZIP；已跳过目录或不可用项目");
        if (files.Length == 0) return;
        Log("发送队列", $"{files.Length} 个文件 → {string.Join("、", peers.Select(p => p.PeerName))}");
        await sends.WaitAsync();
        try
        {
            if (network != current) return;
            await Task.WhenAll(peers.Select(async peer =>
            {
                foreach (var path in files)
                {
                    if (!peer.IsConnected) break;
                    try { await Task.Run(() => peer.SendFileAsync(path)); Ui(() => Log(peer.PeerName, "发送完成：" + Path.GetFileName(path))); }
                    catch (Exception ex) { Ui(() => Log(peer.PeerName, "发送失败：" + Path.GetFileName(path) + " · " + ex.Message)); }
                }
            }));
        }
        finally { sends.Release(); }
    }

    // 手动连接作为广播隔离网络的备用入口，错误信息保持可见。
    private async Task ConnectManuallyAsync()
    {
        var current = network;
        if (current is null) { Log("提示", "请先开启共享"); return; }
        if (!IPAddress.TryParse(host.Text.Trim(), out var address) || address.AddressFamily != AddressFamily.InterNetwork)
        { Log("提示", "请输入对方的 IPv4 地址，例如 192.168.1.20"); return; }
        manual.Enabled = false;
        try { await current.ConnectAsync(address.ToString()); }
        catch (Exception ex) { if (network == current) Log("连接失败", ex is OperationCanceledException ? "连接超时，请检查 IP、防火墙或两端确认窗口" : ex.Message); }
        finally { if (!IsDisposed) manual.Enabled = true; }
    }

    // 刷新设备时尽量保留用户的目标选择，设备下线后回到明确的全部目标。
    private void RefreshPeers()
    {
        string? id = (targets.SelectedItem as Target)?.Id;
        targets.Items.Clear(); targets.Items.Add(new Target(null, "全部在线电脑"));
        if (network is not null)
            foreach (var peer in network.Sessions.OrderBy(p => p.PeerName)) targets.Items.Add(new Target(peer.PeerId, peer.PeerName + " · " + peer.PeerId[..4]));
        targets.SelectedIndex = 0;
        for (int index = 1; index < targets.Items.Count; index++)
            if (targets.Items[index] is Target candidate && candidate.Id == id) targets.SelectedIndex = index;
        if (network is not null) state.Text = $"共享已开启 · {targets.Items.Count - 1} 台电脑在线";
    }

    // 用独立条目更新各目标、各方向的进度，不记录剪贴板正文。
    private void ShowProgress(PeerSession peer, TransferProgress progress)
    {
        string key = peer.PeerId + ":" + progress.Sending + ":" + progress.Name;
        if (!progressRows.TryGetValue(key, out var row) || row.ListView is null)
        {
            row = new ListViewItem([DateTime.Now.ToString("HH:mm:ss"), (progress.Sending ? "发送 → " : "接收 ← ") + peer.PeerName, ""]);
            history.Items.Insert(0, row); progressRows[key] = row;
        }
        double percent = progress.Total == 0 ? 100 : progress.Completed * 100.0 / progress.Total;
        row.SubItems[2].Text = $"{progress.Name}  ·  {percent:F0}%  ·  {FormatSize(progress.Completed)} / {FormatSize(progress.Total)}";
    }

    // 统一以二进制单位展示文件大小。
    private static string FormatSize(long value) => value >= 1024L * 1024 * 1024 ? $"{value / (1024.0 * 1024 * 1024):F2} GiB" : value >= 1024 * 1024 ? $"{value / (1024.0 * 1024):F1} MiB" : value >= 1024 ? $"{value / 1024.0:F1} KiB" : value + " B";

    // 状态区保留当前操作说明。
    private void UpdateState(string title, string explanation) { state.Text = title; detail.Text = explanation; }

    // 活动日志只记录元数据，限制条目数防止长时间运行无限增长。
    private void Log(string source, string text)
    {
        if (IsDisposed) return;
        history.Items.Insert(0, new ListViewItem([DateTime.Now.ToString("HH:mm:ss"), source, text]));
        while (history.Items.Count > 300) history.Items.RemoveAt(history.Items.Count - 1);
        foreach (var key in progressRows.Where(pair => pair.Value.ListView is null).Select(pair => pair.Key).ToArray()) progressRows.Remove(key);
        detail.Text = text;
    }

    // 接收目录由本机用户选择，网络不接受远端提供的目录。
    private void ChooseFolder()
    {
        using var dialog = new FolderBrowserDialog { SelectedPath = folder.Text, Description = "选择接收文件的目录", UseDescriptionForTitle = true };
        if (dialog.ShowDialog(this) == DialogResult.OK) folder.Text = dialog.SelectedPath;
    }

    // 只打开接收目录，不自动执行收到的文件。
    private void OpenFolder()
    {
        try { Directory.CreateDirectory(folder.Text); Process.Start(new ProcessStartInfo(folder.Text) { UseShellExecute = true }); }
        catch (Exception ex) { Log("打开目录失败", ex.Message); }
    }

    // 双击在资源管理器中定位文件，避免将可执行文件当作普通文档自动打开。
    private void ShowReceivedLocation()
    {
        if (received.SelectedItems.Count == 0 || received.SelectedItems[0].Tag is not string path || !File.Exists(path)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true });
    }

    // 对外提供标准文件拖拽格式，用户可将已接收文件复制到任意资源管理器窗口。
    private void DragReceivedFiles()
    {
        string[] paths = received.SelectedItems.Cast<ListViewItem>().Select(item => (string)item.Tag!).Where(File.Exists).ToArray();
        if (paths.Length != 0) received.DoDragDrop(new DataObject(DataFormats.FileDrop, paths), DragDropEffects.Copy);
    }

    // 后台事件统一投递到界面线程，窗体退出后不再入队。
    private void Ui(Action action)
    {
        if (shuttingDown || IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(new Action(() => { if (!shuttingDown && !IsDisposed) action(); })); }
        catch (InvalidOperationException) { }
    }

    // 停止共享立即关闭所有传输，部分文件由接收会话清理。
    private void StopSharing()
    {
        var previous = network; network = null;
        SetInputRuntime(); inputPeers.Clear(); RefreshInputCards();
        if (previous is not null) settings.TrustedDeviceIds = previous.TrustedIds.ToList();
        clipboard.Enabled = false; latestClipboard = null; previous?.Dispose();
        browse.Enabled = true; start.Text = "开启共享"; tray.Text = "局域共享 · 未开启";
        RefreshPeers(); UpdateState("共享已停止", "剪贴板不再同步；正在传输的任务已取消。重新开启可再次自动连接。");
    }

    // 从托盘恢复窗口，并带到前台。
    internal void RestoreWindow() { suppressFirstShow = false; ShowInTaskbar = true; Show(); WindowState = FormWindowState.Normal; Activate(); }

    // 已在后台运行时，第二次启动通过本机窗口消息恢复当前窗口。
    protected override void WndProc(ref System.Windows.Forms.Message message)
    {
        if (message.Msg == 0x8201) { RestoreWindow(); return; }
        base.WndProc(ref message);
    }

    // 点击关闭只隐藏到托盘；托盘退出、系统关机和自检模式才真正释放资源。
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!exitRequested && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true; Hide();
            if (!smoke && !closeHintShown) { tray.ShowBalloonTip(2500, "局域共享仍在运行", "关闭窗口后继续共享。双击托盘图标打开；右键选择退出可结束程序。", ToolTipIcon.Info); closeHintShown = true; }
            base.OnFormClosing(e); return;
        }
        if (recording.IsBusy)
        {
            e.Cancel = true;
            if (!closingRecording) { closingRecording = true; shuttingDown = true; _ = FinishRecordingAndExitAsync(); }
            base.OnFormClosing(e); return;
        }
        shuttingDown = true;
        if (!smoke)
        {
            try { if (network is not null) settings.TrustedDeviceIds = network.TrustedIds.ToList(); settings.ClipboardEnabled = sync.Checked; settings.AutoAccept = accept.Checked; settings.ReceiveFolder = folder.Text; settings.Save(); }
            catch (Exception) { }
        }
        Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitch;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanging -= OnDisplaySettingsChanging;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        recordingTimer.Dispose(); recording.Dispose();
        screenshotHotkey.Dispose(); recordingHotkey.Dispose(); screenshots.Dispose(); StopSharing(); identity.Dispose(); clipboard.Dispose(); tray.Visible = false; tray.Dispose(); appIcon.Dispose();
        inputTimer.Dispose(); inputDesktop.Dispose();
        base.OnFormClosing(e);
    }

    // 托盘明确退出才结束后台共享；自检也使用相同的资源回收入口。
    internal void ExitApplication() { exitRequested = true; Close(); }

    // 从程序集读取多尺寸图标，单文件发布后窗口和托盘仍可直接使用。
    private static Icon LoadAppIcon()
    {
        using var stream = typeof(MainForm).Assembly.GetManifestResourceStream("LanShare.App.Icon") ?? throw new InvalidOperationException("缺少应用图标。");
        using var icon = new Icon(stream);
        return (Icon)icon.Clone();
    }
    // 无需启动网络或修改剪贴板即可生成界面快照，用于本地视觉验证。
    internal void SavePreview(string path, int pageIndex = 0)
    {
        SelectPage(pageIndex);
        Opacity = 0; ShowInTaskbar = false; Show(); PerformLayout(); Application.DoEvents();
        using var bitmap = new Bitmap(Width, Height);
        DrawToBitmap(bitmap, new Rectangle(Point.Empty, Size)); bitmap.Save(path);
    }

    private sealed record Target(string? Id, string Name)
    {
        // 目标下拉框展示名称，实际路由使用唯一节点 ID。
        public override string ToString() => Name;
    }
}
