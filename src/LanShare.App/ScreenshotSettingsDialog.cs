namespace LanShare.App;

// 截图设置窗口在原有配对、共享之外独立配置快捷键。
internal sealed class ScreenshotSettingsDialog : Form
{
    // 控件明确选择修饰键和主键；失败时保留窗口和原先的可用组合。
    public ScreenshotSettingsDialog(ScreenshotHotkey hotkey, Action<Keys> persist, Icon icon)
    {
        Icon = icon; Text = "截图快捷键"; ClientSize = new Size(470, 185);
        AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96);
        StartPosition = FormStartPosition.CenterParent; FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false; Font = new Font("Microsoft YaHei UI", 9);
        var hint = new Label { Text = "设置全局截图快捷键（后台也可用）", AutoSize = true, Location = new Point(16, 16) };
        var row = new FlowLayoutPanel { Location = new Point(16, 48), Size = new Size(440, 38) };
        var ctrl = new CheckBox { Text = "Ctrl", Checked = hotkey.Shortcut.HasFlag(Keys.Control), AutoSize = true };
        var alt = new CheckBox { Text = "Alt", Checked = hotkey.Shortcut.HasFlag(Keys.Alt), AutoSize = true };
        var shift = new CheckBox { Text = "Shift", Checked = hotkey.Shortcut.HasFlag(Keys.Shift), AutoSize = true };
        var key = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 95 };
        foreach (Keys candidate in Enum.GetValues<Keys>().Distinct().Where(k =>
            k is >= Keys.A and <= Keys.Z or >= Keys.D0 and <= Keys.D9 or >= Keys.F1 and <= Keys.F24)) key.Items.Add(candidate);
        key.SelectedItem = hotkey.Shortcut & Keys.KeyCode;
        row.Controls.AddRange([ctrl, alt, shift, key]);
        var error = new Label { Text = hotkey.Error ?? "", ForeColor = Color.DarkRed, Location = new Point(16, 91), Size = new Size(440, 40) };
        var save = new Button { Text = "保存", Location = new Point(285, 143), Width = 76 };
        var cancel = new Button { Text = "取消", Location = new Point(373, 143), Width = 76, DialogResult = DialogResult.Cancel };
        save.Click += (_, _) =>
        {
            if (key.SelectedItem is not Keys mainKey) return;
            Keys value = mainKey | (ctrl.Checked ? Keys.Control : 0) | (alt.Checked ? Keys.Alt : 0) | (shift.Checked ? Keys.Shift : 0);
            string? failure = hotkey.Apply(value, persist);
            if (failure is null) { DialogResult = DialogResult.OK; Close(); } else error.Text = failure;
        };
        Controls.AddRange([hint, row, error, save, cancel]); AcceptButton = save; CancelButton = cancel;
    }
}
