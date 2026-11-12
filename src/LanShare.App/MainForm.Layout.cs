namespace LanShare.App;

// 桌面工具的分区布局：一个页面只承载一种任务，连接设置和活动记录不挤占操作空间。
internal sealed partial class MainForm
{
    private readonly List<Control> pages = [];
    private readonly List<Button> navigation = [];
    private readonly Label pageTitle = new();
    private readonly Label pageDescription = new();
    private readonly Label screenshotKeys = new();
    private readonly Label recordingKeys = new();
    private readonly Label recordingClock = new();
    private readonly Label screenshotMessage = new() { Text = "截图后可在这里查看操作结果。" };
    private readonly Label recordingMessage = new() { Text = "尚未录制。框选后自动开始，停止后自动保存。" };
    private readonly string[] pageNames = ["文件共享", "截图", "录屏", "设置", "活动记录", "键鼠共享"];
    private readonly string[] pageDescriptions =
    [
        "把文件送到自己的另一台电脑。", "选取画面，复制到剪贴板。", "框选后直接录制，结束后自动保存。",
        "管理连接、接收目录和后台运行。", "查看连接状态、文件进度和最近操作。", "按桌面上的左右顺序排列电脑，鼠标跨屏后键盘一起切换。"
    ];
    private static readonly Color Accent = Color.FromArgb(17, 112, 103);
    private static readonly Color Muted = Color.FromArgb(91, 110, 117);

    // 左侧固定导航，右侧独立工具页，底部始终保留共享状态。
    private void BuildLayout()
    {
        var shell = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 184));
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(shell);
        var sidebar = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(227, 239, 235), Padding = new Padding(18, 26, 18, 20), ColumnCount = 1, RowCount = pageNames.Length + 3, Margin = Padding.Empty };
        sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 98));
        foreach (var name in pageNames) sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        sidebar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));
        var brand = Rows(42, -1);
        brand.Controls.Add(new Label { Text = "局域共享", Dock = DockStyle.Fill, Font = new Font(Font.FontFamily, 19, FontStyle.Bold), ForeColor = Color.FromArgb(17, 83, 83) }, 0, 0);
        brand.Controls.Add(new Label { Text = "自己的电脑，随手互通", Dock = DockStyle.Fill, ForeColor = Muted }, 0, 1);
        sidebar.Controls.Add(brand, 0, 0);
        for (int i = 0; i < pageNames.Length; i++)
        {
            int index = i;
            var button = new Button { Text = pageNames[i], Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(16, 0, 0, 0), FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 4, 0, 4), Cursor = Cursors.Hand };
            button.FlatAppearance.BorderSize = 0;
            button.Click += (_, _) => SelectPage(index);
            navigation.Add(button); sidebar.Controls.Add(button, 0, i + 1);
        }
        sidebar.Controls.Add(new Label { Text = "本机\n" + Environment.MachineName, Dock = DockStyle.Fill, ForeColor = Muted, TextAlign = ContentAlignment.BottomLeft }, 0, pageNames.Length + 2);
        shell.Controls.Add(sidebar, 0, 0);

        var workspace = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(28, 20, 28, 12), ColumnCount = 1, RowCount = 3, Margin = Padding.Empty };
        workspace.RowStyles.Add(new RowStyle(SizeType.Absolute, 90));
        workspace.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        workspace.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));
        var heading = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Margin = Padding.Empty };
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); heading.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 126));
        heading.RowStyles.Add(new RowStyle(SizeType.Absolute, 46)); heading.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        pageTitle.Dock = DockStyle.Fill; pageTitle.Font = new Font(Font.FontFamily, 23, FontStyle.Bold); pageTitle.ForeColor = Color.FromArgb(17, 83, 83);
        pageDescription.Dock = DockStyle.Fill; pageDescription.ForeColor = Muted; pageDescription.Padding = new Padding(0, 4, 0, 0);
        heading.Controls.Add(pageTitle, 0, 0); heading.Controls.Add(pageDescription, 0, 1);
        start.Width = 116; start.Anchor = AnchorStyles.Top | AnchorStyles.Right; start.Margin = new Padding(0, 8, 0, 0);
        heading.Controls.Add(start, 1, 0); heading.SetRowSpan(start, 2);
        workspace.Controls.Add(heading, 0, 0);

        var hostPanel = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        pages.AddRange([BuildSharePage(), BuildScreenshotPage(), BuildRecordingPage(), BuildSettingsPage(), BuildActivityPage(), BuildInputPage()]);
        foreach (var page in pages) { page.Dock = DockStyle.Fill; hostPanel.Controls.Add(page); }
        workspace.Controls.Add(hostPanel, 0, 1);
        var status = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Padding = new Padding(0, 10, 0, 0), Margin = Padding.Empty };
        status.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
        state.Font = new Font(Font, FontStyle.Bold); state.AutoEllipsis = detail.AutoEllipsis = true;
        status.Controls.Add(state, 0, 0); status.Controls.Add(detail, 0, 1);
        workspace.Controls.Add(status, 0, 2); shell.Controls.Add(workspace, 1, 0);
        SelectPage(0);
    }

    // 导航切换不重建业务控件，传输列表、已选设备和后台录制都保持原状态。
    internal void SelectPage(int index)
    {
        for (int i = 0; i < pages.Count; i++)
        {
            pages[i].Visible = i == index;
            navigation[i].BackColor = i == index ? Accent : Color.FromArgb(227, 239, 235);
            navigation[i].ForeColor = i == index ? Color.White : Color.FromArgb(42, 69, 65);
            navigation[i].AccessibleDescription = i == index ? "当前页面" : "切换页面";
        }
        pageTitle.Text = pageNames[index]; pageDescription.Text = pageDescriptions[index];
    }

    // 文件页突出目标与投递区，已接收文件紧随其后，网络参数移到设置页。
    private Control BuildSharePage()
    {
        var page = Rows(48, 190, 42, -1);
        var targetRow = Flow();
        targetRow.Controls.Add(Caption("发送到")); targets.Width = 250; targetRow.Controls.Add(targets);
        var open = MakeButton("接收目录", width: 104); open.Click += (_, _) => OpenFolder(); targetRow.Controls.Add(open);
        page.Controls.Add(targetRow, 0, 0);
        var drop = new Label
        {
            Text = "＋\n拖入文件，即可发送\n\n也可以点击选择文件，支持多选", Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(227, 242, 239), ForeColor = Color.FromArgb(17, 83, 83),
            TextAlign = ContentAlignment.MiddleCenter, Font = new Font(Font.FontFamily, 13), Cursor = Cursors.Hand, AllowDrop = true, Margin = new Padding(0, 0, 0, 6)
        };
        drop.DragEnter += (_, e) => e.Effect = network is not null && e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
        drop.DragDrop += async (_, e) => { if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths) await QueueFilesAsync(paths); };
        drop.Click += async (_, _) =>
        {
            using var dialog = new OpenFileDialog { Multiselect = true, Title = "选择要发送的文件" };
            if (dialog.ShowDialog(this) == DialogResult.OK) await QueueFilesAsync(dialog.FileNames);
        };
        page.Controls.Add(drop, 0, 1);
        page.Controls.Add(TextLine("已接收文件     双击定位，或拖到本地文件夹", 10, true), 0, 2);
        received.BorderStyle = BorderStyle.None;
        received.Columns.Add("文件名", 220); received.Columns.Add("来自", 140); received.Columns.Add("保存位置", 360);
        page.Controls.Add(received, 0, 3);
        return page;
    }

    // 截图页让快捷键、主操作与最近结果各占明确的一层。
    private Control BuildScreenshotPage()
    {
        var page = Rows(244, 58, -1);
        var tool = Rows(44, 58, 52, -1); tool.BackColor = Color.White; tool.Padding = new Padding(24);
        tool.Controls.Add(TextLine("框选即复制", 18, true), 0, 0);
        PrepareKeys(screenshotKeys); tool.Controls.Add(screenshotKeys, 0, 1);
        StylePrimaryAction(captureButton, "开始截图"); tool.Controls.Add(captureButton, 0, 2);
        tool.Controls.Add(TextLine("拖动选择区域，松开即可复制。按 Esc 或右键取消。", 10), 0, 3);
        page.Controls.Add(tool, 0, 0);
        var actions = Flow(); actions.Padding = new Padding(0, 10, 0, 0);
        var preview = MakeButton("查看 / 保存上次截图", width: 180); preview.Click += (_, _) => screenshots.ShowLast();
        var shortcut = MakeButton("修改截图快捷键", width: 150); shortcut.Click += (_, _) => ConfigureScreenshot();
        actions.Controls.AddRange([preview, shortcut]); page.Controls.Add(actions, 0, 1);
        var notes = Rows(52, 36, -1); notes.Padding = new Padding(4, 12, 4, 0);
        notes.Controls.Add(TextLine("开启剪贴板同步后，截图会同步到已连接的电脑，直接粘贴即可。", 10), 0, 0);
        notes.Controls.Add(TextLine("最近操作", 10, true), 0, 1);
        PrepareMessage(screenshotMessage); notes.Controls.Add(screenshotMessage, 0, 2); page.Controls.Add(notes, 0, 2);
        return page;
    }

    // 录屏页与截图完全独立，明确显示时长、自动保存目录和录制结果。
    private Control BuildRecordingPage()
    {
        var page = Rows(244, 56, 86, -1);
        var tool = Rows(44, 58, 52, -1); tool.BackColor = Color.White; tool.Padding = new Padding(24);
        var top = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
        top.Controls.Add(TextLine("录下选中的画面", 18, true), 0, 0);
        recordingClock.Text = "00:00:00"; recordingClock.Dock = DockStyle.Fill; recordingClock.TextAlign = ContentAlignment.MiddleRight;
        recordingClock.Font = new Font(Font.FontFamily, 17, FontStyle.Bold); recordingClock.ForeColor = Color.FromArgb(190, 48, 48);
        top.Controls.Add(recordingClock, 1, 0); tool.Controls.Add(top, 0, 0);
        PrepareKeys(recordingKeys); tool.Controls.Add(recordingKeys, 0, 1);
        StylePrimaryAction(recordButton, "开始录屏"); tool.Controls.Add(recordButton, 0, 2);
        tool.Controls.Add(TextLine("框选后直接开始。点击选区旁的停止按钮，或再次按快捷键结束。", 10), 0, 3);
        page.Controls.Add(tool, 0, 0);
        var actions = Flow(); actions.Padding = new Padding(0, 10, 0, 0);
        var shortcut = MakeButton("修改录屏快捷键", width: 150); shortcut.Click += (_, _) => ConfigureRecording();
        var last = MakeButton("定位上次录屏", width: 140); last.Click += (_, _) => recording.ShowLast();
        var directory = MakeButton("打开录屏目录", width: 140); directory.Click += (_, _) => recording.OpenSaveDirectory();
        actions.Controls.AddRange([shortcut, last, directory]); page.Controls.Add(actions, 0, 1);
        var save = Rows(28, 30, -1); save.Padding = new Padding(4, 2, 4, 0);
        save.Controls.Add(TextLine("自动保存目录   ·   文件按时间命名", 10, true), 0, 0);
        save.Controls.Add(new TextBox { Text = ScreenRecordingController.SaveDirectory, ReadOnly = true, Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, BackColor = BackColor, ForeColor = Muted }, 0, 1);
        save.Controls.Add(TextLine("MP4 视频  /  无声  /  最高 1080p，目标 20 fps", 9), 0, 2);
        page.Controls.Add(save, 0, 2);
        PrepareMessage(recordingMessage); recordingMessage.Padding = new Padding(4, 16, 4, 0); page.Controls.Add(recordingMessage, 0, 3);
        return page;
    }

    // 设置按共享行为、接收目录和连接分组，低频操作不再占用工具页面。
    private Control BuildSettingsPage()
    {
        var page = Rows(36, 44, 44, 36, 46, 36, 60, 46, 48, -1); page.AutoScroll = true;
        page.Controls.Add(TextLine("共享与后台运行", 13, true), 0, 0);
        var behavior = Flow(); sync.Margin = new Padding(3, 8, 22, 0); accept.Margin = new Padding(3, 8, 3, 0);
        behavior.Controls.AddRange([sync, accept]); page.Controls.Add(behavior, 0, 1);
        var background = Flow(); startup.Margin = new Padding(3, 8, 22, 0); background.Controls.Add(startup);
        var shellMenu = new CheckBox { Text = "资源管理器右键菜单", Checked = settings.ExplorerMenuEnabled, AutoSize = true, Margin = startup.Margin };
        shellMenu.Click += (_, _) => ChangeExplorerMenu(shellMenu);
        background.Controls.Add(shellMenu);
        var shareNow = MakeButton("同步当前剪贴板", width: 150); shareNow.Click += (_, _) => clipboard.ShareNow(); background.Controls.Add(shareNow);
        page.Controls.Add(background, 0, 2);
        page.Controls.Add(TextLine("接收文件", 13, true), 0, 3);
        var receiveFolder = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
        receiveFolder.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); receiveFolder.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112)); receiveFolder.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 78));
        folder.Margin = new Padding(3, 7, 10, 0); receiveFolder.Controls.Add(folder, 0, 0); receiveFolder.Controls.Add(browse, 1, 0);
        var open = MakeButton("打开", width: 70); open.Click += (_, _) => OpenFolder(); receiveFolder.Controls.Add(open, 2, 0); page.Controls.Add(receiveFolder, 0, 4);
        page.Controls.Add(TextLine("设备连接", 13, true), 0, 5);
        page.Controls.Add(TextLine(Environment.MachineName + "\n本机 IP：" + LocalAddresses(), 10), 0, 6);
        var manualRow = Flow(); manualRow.Controls.Add(Caption("手动连接")); host.Width = 230; host.Margin = new Padding(3, 6, 10, 0); manualRow.Controls.AddRange([host, manual]); page.Controls.Add(manualRow, 0, 7);
        var pairing = Flow();
        var retry = MakeButton("重新配对", width: 110); retry.Click += (_, _) => { if (network is null) ToggleSharing(); else network.RetryPairing(); Log("配对", "已允许重新发起连接确认"); };
        var forget = MakeButton("清除信任", width: 110); forget.Click += (_, _) => ForgetDevices(); pairing.Controls.AddRange([retry, forget]); page.Controls.Add(pairing, 0, 8);
        page.Controls.Add(TextLine("已配对的电脑自动连接。仅在自动发现不可用时填写 IP。\n关闭窗口仍在后台运行，可从托盘退出。", 9), 0, 9);
        return page;
    }

    // 活动页完整保留传输日志，不让长列表干扰截图和录屏操作。
    private Control BuildActivityPage()
    {
        var page = Rows(38, -1);
        page.Controls.Add(TextLine("最近 300 条活动；不记录剪贴板正文。", 10), 0, 0);
        history.BorderStyle = BorderStyle.None;
        history.Columns.Add("时间", 82); history.Columns.Add("方向 / 设备", 170); history.Columns.Add("内容 / 进度", 490);
        page.Controls.Add(history, 0, 1); return page;
    }

    // 统一行容器保留最末弹性空间，缩小时可结合页面滚动保持内容可达。
    private static TableLayoutPanel Rows(params int[] heights)
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = heights.Length, Margin = Padding.Empty };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (int height in heights) panel.RowStyles.Add(height < 0 ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.Absolute, height));
        return panel;
    }

    // 横向动作组在所在功能内排列，避免跨功能按钮混杂。
    private static FlowLayoutPanel Flow() => new() { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };

    // 文案有固定层级，常规说明使用较弱颜色，标题使用正常前景。
    private Label TextLine(string text, float size, bool bold = false) => new()
    {
        Text = text, Dock = DockStyle.Fill, Font = new Font(Font.FontFamily, size, bold ? FontStyle.Bold : FontStyle.Regular),
        ForeColor = bold ? ForeColor : Muted, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Margin = Padding.Empty
    };

    // 快捷键在专属页面独立显示，不再塞进工具按钮名称。
    private void PrepareKeys(Label label) { label.Dock = DockStyle.Fill; label.Font = new Font(Font.FontFamily, 19); label.ForeColor = Muted; label.TextAlign = ContentAlignment.MiddleLeft; }

    // 工具主操作比辅助按钮更醒目，保留系统焦点边框和键盘操作。
    private static void StylePrimaryAction(Button button, string text)
    {
        button.Text = text; button.Size = new Size(166, 42); button.BackColor = Accent; button.ForeColor = Color.White;
        button.FlatAppearance.BorderSize = 0; button.Margin = new Padding(0, 3, 0, 3);
    }

    // 最近操作显示允许换行，长文件路径仍能在窗口中读取。
    private static void PrepareMessage(Label label) { label.Dock = DockStyle.Fill; label.ForeColor = Muted; label.AutoEllipsis = true; }

    // 快捷键更改立即同步到各自页面；冲突状态提示用户修改。
    private void RefreshShortcutLabels()
    {
        screenshotKeys.Text = screenshotHotkey.Label; recordingKeys.Text = recordingHotkey.Label;
        if (screenshotHotkey.Error is not null) screenshotMessage.Text = screenshotHotkey.Error;
        if (recordingHotkey.Error is not null) recordingMessage.Text = recordingHotkey.Error;
    }

}
