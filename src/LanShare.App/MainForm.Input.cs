using LanShare.Core;

namespace LanShare.App;

// 独立的键鼠设置页。设备位置和单向授权均以配对证书指纹保存。
internal sealed partial class MainForm
{
    private const string CardFormat = "SimpleLanShare.InputLayout";
    private readonly WindowsInput inputDesktop = new();
    private readonly RemotePointerMarker pointerMarker = new();
    private readonly System.Windows.Forms.Timer inputTimer = new() { Interval = 150 };
    private readonly Dictionary<string, PeerSession> inputPeers = [];
    private readonly FlowLayoutPanel inputCards = Flow();
    private readonly Label inputStatus = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Label inputSelection = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
    private readonly Button inputPermission = MakeButton("允许控制本机", width: 150);
    private readonly Button moveLeft = MakeButton("向左移", width: 80), moveRight = MakeButton("向右移", width: 80);
    private string inputLocalId = "", selectedInputId = "";
    private bool inputLocked;
    private SharedInput inputRouter = null!;

    // 在窗口构建前初始化状态机，系统钩子只在用户开启该功能后安装。
    private void InitializeInput()
    {
        inputLocalId = Security.Fingerprint(identity);
        selectedInputId = inputLocalId;
        settings.InputOrder = settings.InputOrder.Where(id => id == inputLocalId || settings.TrustedDeviceIds.Contains(id)).Distinct().ToList();
        if (!settings.InputOrder.Contains(inputLocalId)) settings.InputOrder.Insert(0, inputLocalId);
        foreach (string id in settings.TrustedDeviceIds)
            if (!settings.InputOrder.Contains(id)) settings.InputOrder.Add(id);
        if (settings.SharedLayout is {} saved) settings.InputOrder = saved.ForKnownDevices(settings.InputOrder);
        inputRouter = new SharedInput(inputLocalId, inputDesktop, InputNeighbor,
            id => settings.InputAllowed.Contains(id) && settings.TrustedDeviceIds.Contains(id) && !inputDesktop.HasPendingReleases,
            (id, packet) => inputPeers.TryGetValue(id, out var peer) && peer.Input?.TrySend(packet) == true,
            () => screenshots.IsCapturing || (recording.IsBusy && !recording.IsRecording));
        inputDesktop.Router = inputRouter;
        inputDesktop.ReturnKey = settings.InputReturnKey;
        inputTimer.Tick += (_, _) =>
        {
            bool active = WindowsInput.OnUserDesktop();
            if (active && inputDesktop.HasPendingReleases) inputDesktop.RetryReleases();
            bool enabled = active && settings.InputEnabled && network is not null && !inputLocked && !shuttingDown;
            if (inputRouter.Enabled != enabled) { inputRouter.Enable(enabled); UpdateInputStatus(); }
            if (!enabled && !inputDesktop.HasPendingReleases && (network is null || !settings.InputEnabled || inputLocked)) inputTimer.Stop();
            inputRouter.Tick();
        };
        inputRouter.Changed += reason => Ui(() => { UpdateInputStatus(reason); Log("键鼠共享", reason); });
        inputRouter.Notice += reason => Ui(() => { UpdateInputStatus(); inputStatus.Text += " · " + reason; Log("键盘输入", reason); });
    }

    // 只跨到布局中紧邻的在线电脑，离线位置保留，重连后恢复使用。
    private string? InputNeighbor(string from, int side)
    {
        int index = settings.InputOrder.IndexOf(from);
        int next = index + side;
        if (index < 0 || next < 0 || next >= settings.InputOrder.Count) return null;
        string id = settings.InputOrder[next];
        return id == inputLocalId || (inputPeers.TryGetValue(id, out var peer) && peer.Input?.IsOpen == true) ? id : null;
    }

    // 显示易读设备名，未获得名称的旧配对用短指纹区分。
    private string InputName(string id) => id == inputLocalId ? Environment.MachineName : settings.InputNames.GetValueOrDefault(id, id[..Math.Min(8, id.Length)]);

    // 将系统桌面状态、共享总开关与用户设置合并；停用时先释放按键再移除钩子。
    private void SetInputRuntime()
    {
        if (settings.InputEnabled && network is not null && !inputLocked && !smoke && !shuttingDown)
        {
            try { inputDesktop.Start(); inputRouter.Enable(WindowsInput.OnUserDesktop()); inputTimer.Start(); }
            catch (Exception ex) { inputRouter.Enable(false); inputTimer.Stop(); UpdateInputStatus(ex.Message); return; }
        }
        else
        {
            inputRouter.Enable(false); inputDesktop.Dispose();
            if (inputDesktop.HasPendingReleases && !shuttingDown) inputTimer.Start(); else inputTimer.Stop();
        }
        UpdateInputStatus();
    }

    // 页面与托盘标出当前控制方向；活动记录不保存按键内容。
    private void UpdateInputStatus(string? reason = null)
    {
        pointerMarker.SetReceiving(!shuttingDown && inputRouter.Mode == ControlMode.Receiving);
        string name = inputRouter.Peer is {} id ? InputName(id) : "";
        string status = inputRouter.Mode switch
        {
            ControlMode.Sending => "正在控制 " + name,
            ControlMode.Receiving => "正由 " + name + " 控制",
            ControlMode.Waiting => "正在等待 " + name + " 接管",
            _ => inputRouter.Enabled ? "键鼠在本机" : settings.InputEnabled ? "键鼠共享已暂停" : "键鼠共享已关闭"
        };
        inputStatus.Text = reason is null || inputRouter.Mode != ControlMode.Local ? status : status + " · " + reason;
        if (!recording.IsBusy && !shuttingDown && network is not null)
        {
            string text = "局域共享 · " + status;
            tray.Text = text[..Math.Min(63, text.Length)];
        }
    }

    // 所有事件按到达顺序在窗口线程处理，旧连接的回调不能接管已重连的设备。
    private void BindInput(PeerNetwork current, PeerSession peer)
    {
        peer.LayoutReceived += layout => Ui(() =>
        {
            if (network == current && inputPeers.GetValueOrDefault(peer.PeerId) == peer && layout.IsNewerThan(settings.SharedLayout))
                ApplySharedLayout(layout, peer);
        });
        if (peer.Input is not {} link) return;
        var inbox = new DesktopInputQueue(action => BeginInvoke(action), packet =>
        {
            if (!shuttingDown && network == current && link.IsOpen && inputPeers.GetValueOrDefault(peer.PeerId) == peer)
                inputRouter.Receive(peer.PeerId, packet);
        }, link.Dispose);
        link.Received = inbox.Add;
        link.Closed += inbox.Dispose;
        link.Closed += () => Ui(() =>
        {
            if (network != current || inputPeers.GetValueOrDefault(peer.PeerId) != peer) return;
            if (inputRouter.Peer == peer.PeerId) inputRouter.ReturnLocal("键鼠连接已断开");
            RefreshInputCards();
        });
    }

    // 记录上线的显示名和首次出现的位置，既有位置由用户决定。
    private void RememberInputPeer(PeerSession peer)
    {
        inputPeers[peer.PeerId] = peer;
        settings.InputNames[peer.PeerId] = peer.PeerName;
        if (!settings.InputOrder.Contains(peer.PeerId)) settings.InputOrder.Add(peer.PeerId);
        if (settings.SharedLayout is {} layout)
        {
            settings.InputOrder = layout.ForKnownDevices(settings.InputOrder);
            _ = SendLayoutAsync(peer, layout);
        }
        RefreshInputCards();
    }

    // 旧会话的断开通知不会覆盖随后建立的新会话。
    private void DetachInput(PeerSession peer)
    {
        if (inputPeers.GetValueOrDefault(peer.PeerId) != peer) return;
        if (inputRouter.Peer == peer.PeerId) inputRouter.ReturnLocal("对方已离线");
        inputPeers.Remove(peer.PeerId);
        RefreshInputCards();
    }

    // 将布局、单向授权及返回快捷键集中到专属页面。
    private Control BuildInputPage()
    {
        var page = Rows(42, 36, 32, 86, 38, 36, 44, 44, -1);
        page.AutoScroll = true;
        var enabled = new CheckBox { Text = "启用键鼠共享", AutoSize = true, Checked = settings.InputEnabled, Margin = new Padding(3, 8, 10, 0) };
        enabled.CheckedChanged += (_, _) => { settings.InputEnabled = enabled.Checked; SetInputRuntime(); SaveInputSettings(); };
        var first = Flow(); first.Controls.Add(enabled); page.Controls.Add(first, 0, 0);
        page.Controls.Add(TextLine("电脑排列", 14, true), 0, 1);
        page.Controls.Add(TextLine("拖动卡片调整左右顺序，排列会自动同步到已配对的电脑。", 10), 0, 2);
        inputCards.AutoScroll = true; page.Controls.Add(inputCards, 0, 3);
        var positions = Flow(); positions.Controls.AddRange([moveLeft, moveRight]);
        var publish = MakeButton("同步当前排列", width: 125);
        publish.Click += (_, _) => PublishOrder(settings.InputOrder);
        positions.Controls.Add(publish);
        moveLeft.Click += (_, _) => MoveInputCard(selectedInputId, settings.InputOrder.IndexOf(selectedInputId) - 1);
        moveRight.Click += (_, _) => MoveInputCard(selectedInputId, settings.InputOrder.IndexOf(selectedInputId) + 1);
        page.Controls.Add(positions, 0, 4);
        page.Controls.Add(inputSelection, 0, 5);
        inputPermission.Click += (_, _) =>
        {
            if (!settings.TrustedDeviceIds.Contains(selectedInputId)) return;
            if (!settings.InputAllowed.Remove(selectedInputId)) settings.InputAllowed.Add(selectedInputId);
            else if (inputRouter.Peer == selectedInputId && inputRouter.Mode == ControlMode.Receiving)
                inputRouter.ReturnLocal("已撤销控制权限");
            SaveInputSettings(); UpdateCardSelection();
        };
        var permissions = Flow(); permissions.Controls.Add(inputPermission); page.Controls.Add(permissions, 0, 6);
        var shortcuts = Flow(); shortcuts.Controls.Add(Caption("立即返回本机"));
        var keys = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
        Keys[] choices = [Keys.Home, Keys.End, Keys.Pause];
        foreach (var key in choices) keys.Items.Add("Ctrl + Alt + " + key);
        keys.SelectedIndex = Math.Max(0, Array.IndexOf(choices, settings.InputReturnKey));
        inputDesktop.ReturnKey = choices[keys.SelectedIndex];
        keys.SelectedIndexChanged += (_, _) => { settings.InputReturnKey = inputDesktop.ReturnKey = choices[keys.SelectedIndex]; SaveInputSettings(); };
        shortcuts.Controls.Add(keys); page.Controls.Add(shortcuts, 0, 7);
        page.Controls.Add(inputStatus, 0, 8);
        RefreshInputCards(); UpdateInputStatus(); return page;
    }

    // 将配置写入已有设置文件，自检模式不改动真实用户配置。
    private void SaveInputSettings()
    {
        if (smoke) return;
        try { settings.Save(); } catch (Exception ex) { Log("键鼠设置保存失败", ex.Message); MessageBox.Show(this, ex.Message, "键鼠设置保存失败"); }
    }

    // 重新显示桌面排列，选择操作只切换外观，保留鼠标拖动的事件源。
    private void RefreshInputCards()
    {
        if (!settings.InputOrder.Contains(inputLocalId)) settings.InputOrder.Insert(0, inputLocalId);
        if (!settings.InputOrder.Contains(selectedInputId)) selectedInputId = inputLocalId;
        inputCards.SuspendLayout();
        while (inputCards.Controls.Count > 0) inputCards.Controls[0].Dispose();
        foreach (string id in settings.InputOrder)
        {
            string state = id == inputLocalId ? "本机" : inputPeers.TryGetValue(id, out var peer) ?
                peer.Input?.IsOpen == true ? "在线" : peer.Input is null ? "需升级对方版本" : "键鼠通道断开" : "离线";
            var card = MakeButton(InputName(id) + "\n" + state, width: 148);
            card.Height = 58; card.Tag = id; card.AllowDrop = true; card.AutoEllipsis = true;
            Point origin = default;
            card.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) { selectedInputId = id; origin = e.Location; UpdateCardSelection(); } };
            card.Click += (_, _) => { selectedInputId = id; UpdateCardSelection(); };
            card.MouseMove += (_, e) =>
            {
                if (e.Button != MouseButtons.Left || new Rectangle(origin.X - 4, origin.Y - 4, 9, 9).Contains(e.Location)) return;
                card.DoDragDrop(new DataObject(CardFormat, id), DragDropEffects.Move);
            };
            card.DragOver += (_, e) => e.Effect = e.Data?.GetDataPresent(CardFormat, false) == true ? DragDropEffects.Move : DragDropEffects.None;
            card.DragDrop += (_, e) =>
            {
                if (e.Data?.GetData(CardFormat, false) is string source && settings.InputOrder.Contains(source))
                    Ui(() => MoveInputCard(source, settings.InputOrder.IndexOf(id)));
            };
            inputCards.Controls.Add(card);
        }
        inputCards.ResumeLayout(); UpdateCardSelection();
    }

    // 移动指定设备时结束正在进行的控制，下一次跨屏使用新布局。
    private void MoveInputCard(string id, int index)
    {
        int old = settings.InputOrder.IndexOf(id);
        if (old < 0 || index < 0 || index >= settings.InputOrder.Count || old == index) return;
        var order = settings.InputOrder.ToList();
        order.RemoveAt(old); order.Insert(index, id);
        selectedInputId = id; PublishOrder(order);
    }

    // 拖动、左右移动和手动发布均产生高于已知版本的新快照。
    private void PublishOrder(List<string> order)
    {
        try { ApplySharedLayout(DesktopLayout.Edit(settings.SharedLayout, inputLocalId, order), null); }
        catch (InvalidOperationException ex) { inputStatus.Text = ex.Message; }
    }

    // 先持久化再转发。相同版本不再传播，旧版的本地布局可由用户一次性发布。
    private void ApplySharedLayout(DesktopLayout layout, PeerSession? source)
    {
        var oldLayout = settings.SharedLayout;
        var oldOrder = settings.InputOrder;
        settings.SharedLayout = layout;
        settings.InputOrder = layout.ForKnownDevices(oldOrder);
        try { if (!smoke) settings.Save(); }
        catch (Exception ex)
        {
            settings.SharedLayout = oldLayout; settings.InputOrder = oldOrder;
            Log("排列保存失败", ex.Message); inputStatus.Text = "排列保存失败：" + ex.Message; return;
        }
        if (!oldOrder.SequenceEqual(settings.InputOrder)) inputRouter.ReturnLocal("桌面排列已改变");
        RefreshInputCards();
        foreach (var peer in inputPeers.Values)
            if (peer != source) _ = SendLayoutAsync(peer, layout);
    }

    // 失败时保留已保存快照，下一次连接会重新交换最新版本。
    private async Task SendLayoutAsync(PeerSession peer, DesktopLayout layout)
    {
        try { await peer.SendLayoutAsync(layout); }
        catch (Exception ex) { if (!shuttingDown) Log(peer.PeerName, "排列同步中断：" + ex.Message); }
    }

    // 设备权限单独呈现，短指纹可区分同名电脑。
    private void UpdateCardSelection()
    {
        foreach (Button card in inputCards.Controls)
        {
            bool selected = Equals(card.Tag, selectedInputId);
            card.BackColor = selected ? Accent : Color.White; card.ForeColor = selected ? Color.White : ForeColor;
        }
        int index = settings.InputOrder.IndexOf(selectedInputId);
        moveLeft.Enabled = index > 0; moveRight.Enabled = index >= 0 && index < settings.InputOrder.Count - 1;
        inputPermission.Enabled = selectedInputId != inputLocalId && settings.TrustedDeviceIds.Contains(selectedInputId);
        bool allowed = settings.InputAllowed.Contains(selectedInputId);
        inputPermission.Text = allowed ? "撤销控制权限" : "允许控制本机";
        inputSelection.Text = selectedInputId == inputLocalId ? "选择对方电脑，可设置它控制本机的权限。" :
            $"{InputName(selectedInputId)}  ·  指纹 {selectedInputId[..Math.Min(8, selectedInputId.Length)]}  ·  {(allowed ? "允许控制本机" : "未授权控制本机")}";
    }
}
