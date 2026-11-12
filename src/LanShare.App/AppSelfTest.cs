using System.Drawing.Imaging;
using LanShare.Core;

namespace LanShare.App;

// 不修改系统剪贴板的 Windows 专项自检：图片编解码、DPAPI 和窗体创建。
internal static class AppSelfTest
{
    // 检查真实 GDI+ 和当前用户密钥服务，并将结果写到指定测试目录。
    public static void Run(string report)
    {
        var lines = new List<string>();
        if (InputIntegrity.Local is not > 0 || InputIntegrity.ReadProcess(Environment.ProcessId) != InputIntegrity.Local ||
            InputIntegrity.ReadProcess(-1) is not null)
            throw new Exception("Windows process integrity query failed");
        lines.Add($"PASS: current process integrity RID {InputIntegrity.Local:X}, missing process handled without blocking input");
        string code = Guid.NewGuid().ToString("N");
        string protectedCode = AppSettings.Protect(code, false);
        if (protectedCode == code || AppSettings.Protect(protectedCode, true) != code) throw new Exception("DPAPI roundtrip failed");
        lines.Add("PASS: DPAPI current-user encrypted-data roundtrip");
        var settings = new AppSettings();
        using var identity = settings.ReadIdentity();
        var restored = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(System.Text.Json.JsonSerializer.Serialize(settings))!;
        using var restoredIdentity = restored.ReadIdentity();
        if (Security.Fingerprint(identity) != Security.Fingerprint(restoredIdentity)) throw new Exception("Identity persistence failed");
        lines.Add("PASS: DPAPI-protected identity survives settings roundtrip");
        CheckInstanceLock();
        lines.Add("PASS: retained unlocked or abandoned mutex permits restart, live owner prevents duplicates");
        CheckInputShutdown();
        lines.Add("PASS: blocked input thread does not block shutdown request; queued cleanup runs once");
        using (var loop = new InputMessageLoop())
        {
            var tested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            loop.Post(() =>
            {
                try
                {
                    using var input = new WindowsInput();
                    input.Start(); input.Dispose(); input.Start();
                    if (input.Width < 1 || input.Height < 1 || SynchronizationContext.Current is not WindowsFormsSynchronizationContext)
                        throw new Exception("Invalid input desktop extent or missing input message context");
                    tested.SetResult();
                }
                catch (Exception ex) { tested.SetException(ex); }
            });
            tested.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            CheckBusyClock(loop);
            lines.Add("PASS: periodic heartbeat runs on the input thread while desktop work remains queued");
        }
        lines.Add("PASS: dedicated input thread installs, releases and reinstalls both Windows hooks while UI waits");
        if (!WindowsInput.TryMakeKey(65, 30, 0, out var letter) || letter.A != 65 || letter.B != 30 || letter.C != 0 ||
            !WindowsInput.TryMakeKey(0x25, 0xE04B, 0x80, out var left) || left.B != 0x4B || left.C != 3 ||
            !WindowsInput.TryMakeKey(0x13, 0xE145, 0, out var pause) || pause.B != 0 ||
            WindowsInput.TryMakeKey(255, 0, 0, out _))
            throw new Exception("Keyboard scan code normalization failed");
        lines.Add("PASS: ordinary typing, extended release and virtual-key fallback fit the existing wire format");
        using (var activity = new LocalDeviceActivity(() => false, _ => { }))
        {
            activity.SetReceiving(true);
            if (!activity.Registered || !LocalDeviceActivity.IsDeviceKeyDown(1, 0, 65, 0) ||
                LocalDeviceActivity.IsDeviceKeyDown(0, 0, 65, 0) ||
                LocalDeviceActivity.IsDeviceKeyDown(1, 0, 65, WindowsInput.ReplayTag) ||
                LocalDeviceActivity.IsDeviceKeyDown(1, 1, 65, 0))
                throw new Exception("Device takeover filtering failed");
            activity.SetReceiving(false);
            if (activity.Registered) throw new Exception("Device subscription retained after return");
        }
        lines.Add("PASS: Windows device registration and filtering of replay, release and device-less keyboard events");
        if (!WindowsInput.CanRouteWheel(ControlMode.Sending, 0, 0) ||
            !WindowsInput.CanRouteWheel(ControlMode.Sending, 1, 0) ||
            WindowsInput.CanRouteWheel(ControlMode.Receiving, 1, 0) ||
            WindowsInput.CanRouteWheel(ControlMode.Sending, 3, 0) ||
            WindowsInput.CanRouteWheel(ControlMode.Sending, 1, WindowsInput.ReplayTag))
            throw new Exception("Smooth wheel hook filtering failed");
        lines.Add("PASS: smooth wheel driver events route only while sending, excluding replay and lower-integrity input");
        var motion = new PointerMotion();
        for (int n = 0; n < 8; n++)
        {
            if (motion.Observe(new Point(-1920, 60), true) != Point.Empty ||
                motion.Observe(new Point(-1913, 64)) != new Point(7, 4) ||
                motion.Observe(new Point(960, 540), true) != Point.Empty ||
                motion.Observe(new Point(957, 546)) != new Point(-3, 6) ||
                motion.Observe(new Point(957, 546)) != Point.Empty)
                throw new Exception("Pointer displacement drifted after recenter or re-entry");
        }
        lines.Add("PASS: eight round trips exclude warp offsets and stationary motion");
        Rectangle[] monitors = [new(-1920, 200, 1920, 1080), new(0, 0, 2560, 1440)];
        if (WindowsInput.VisiblePoint(new Point(-1500, 0), monitors) != new Point(-1500, 200) ||
            WindowsInput.VisiblePoint(new Point(-120, 400), monitors) != new Point(-120, 400))
            throw new Exception("Pointer missed visible monitor pixels");
        lines.Add("PASS: pointer targets visible pixels on staggered monitors with negative origins");
        using (var marker = new RemotePointerMarker())
        {
            marker.SetReceiving(true);
            var styles = GetWindowLong(marker.Handle, -20);
            if (!marker.Visible || (styles & 0x08080020) != 0x08080020)
                throw new Exception("Remote pointer marker must be visible, layered, click-through and non-activating");
            marker.SetReceiving(false);
            if (marker.Visible) throw new Exception("Remote pointer marker remained visible after return");
        }
        lines.Add("PASS: receiving cursor marker appears click-through and hides on return");
        using (var sender = new SenderCursorWindow())
        {
            var area = Screen.PrimaryScreen!.WorkingArea;
            var region = new Rectangle(area.Left + area.Width / 2, area.Top + area.Height / 2, 16, 16);
            for (int trip = 0; trip < 2; trip++)
            {
                if (!sender.TryShow(region)) throw new Exception("Could not show the sender cursor window");
                nint hwnd = sender.Handle;
                DwmFlush();
                var hit = WindowFromPoint(new Point(region.Left + 8, region.Top + 8));
                var handled = SendMessage(hwnd, 0x0020, hwnd, 1);
                if (GetForegroundWindow() == hwnd || hit != hwnd || handled != 1 || GetCursor() != 0)
                    throw new Exception($"Sender cursor window failed: hover={hit == hwnd}, active={GetForegroundWindow() == hwnd}, hidden={GetCursor() == 0}");
                sender.Dispose();
                if (sender.Handle != 0 || GetCursor() == 0) throw new Exception("Source cursor did not recover");
            }
        }
        lines.Add("PASS: sender window owns hover without activation, hides the cursor and restores it twice");
        using var bitmap = new Bitmap(8, 8);
        bitmap.SetPixel(3, 4, Color.FromArgb(128, 20, 100, 200));
        using var memory = new MemoryStream(); bitmap.Save(memory, ImageFormat.Png);
        using var decoded = ClipboardBridge.DecodeImage(memory.ToArray());
        if (decoded.Width != 8 || decoded.Height != 8 || decoded.GetPixel(3, 4).A != 128) throw new Exception("PNG roundtrip failed");
        lines.Add("PASS: PNG dimensions and transparency preserved in codec");
        byte[] huge = memory.ToArray();
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(huge.AsSpan(16, 4), 100000);
        bool rejected = false;
        try { using var invalid = ClipboardBridge.DecodeImage(huge); }
        catch (InvalidDataException) { rejected = true; }
        if (!rejected) throw new Exception("Oversized image accepted");
        lines.Add("PASS: oversized PNG rejected before GDI+ decode");
        using var form = new MainForm(true);
        form.SavePreview(Path.ChangeExtension(report, ".png"));
        for (int page = 1; page < 6; page++) form.SavePreview(Path.Combine(Path.GetDirectoryName(report)!, $"page-{page}.png"), page);
        if (BuildIdentity.Commit.Length == 40 && !BuildIdentity.Commit.All(Uri.IsHexDigit))
            throw new Exception("Invalid embedded Git commit");
        if (!UpdatePackage.IsCandidate("SimpleLanShare-0.6.7-win-x64 (1).zip") || UpdatePackage.IsCandidate("我的照片.zip"))
            throw new Exception("Incorrect update package detection");
        lines.Add($"PASS: running build {BuildIdentity.Info}, received package name and duplicate suffix recognized");
        MinimizeTests.Run(form, lines);
        ExplorerMenuTests.CheckRegistry();
        lines.Add("PASS: Explorer file submenu and both background verbs register, update and uninstall under an isolated key");
        var explorerTest = form.TestExplorerSendAsync(Path.GetDirectoryName(Path.GetFullPath(report))!);
        while (!explorerTest.IsCompleted) { Application.DoEvents(); Thread.Sleep(10); }
        explorerTest.GetAwaiter().GetResult();
        lines.Add("PASS: Explorer pipe delivers two files with Unicode and spaces only to the chosen TLS peer while the main window is hidden");
        lines.Add("PASS: sender checks a broadcast update once after delivery and skips a refused update");
        form.RestoreWindow();
        form.Close(); Application.DoEvents();
        if (form.IsDisposed || form.Visible) throw new Exception("Close must hide to tray without disposal");
        lines.Add("PASS: window close keeps application alive in tray");
        form.ExitApplication();
        var closeDeadline = Environment.TickCount64 + 5000;
        while (!form.IsDisposed && Environment.TickCount64 < closeDeadline) { Application.DoEvents(); Thread.Sleep(5); }
        if (!form.IsDisposed) throw new Exception("Explicit tray exit must dispose window");
        lines.Add("PASS: explicit tray exit disposes application");
        lines.Add("PASS: WinForms layout and clipboard listener initialization");
        lines.AddRange(DesktopFeatureTests.Run(Path.GetDirectoryName(Path.GetFullPath(report))!));
        lines.AddRange(ScreenRecordingTests.Run(Path.GetDirectoryName(Path.GetFullPath(report))!));
        lines.AddRange(RecordingInteractionTests.Run(Path.GetDirectoryName(Path.GetFullPath(report))!));
        File.WriteAllLines(report, lines);
    }

    // 从实际窗口句柄读取扩展样式，验证光标标记的点击穿透和不激活行为。
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(nint window, int index);
    // 验证发送端隐藏鼠标时不抢走前台窗口。
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    // 查询测试点实际命中的窗口。
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern nint WindowFromPoint(Point point);
    // 直接请求测试窗口设置客户区指针，不移动用户的实体鼠标。
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern nint SendMessage(nint window, uint msg, nint wParam, nint lParam);
    // 读取本线程在进入和退出控制时设置的指针句柄。
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern nint GetCursor();
    // 等待桌面合成器完成显示后再验证命中位置。
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")] private static extern int DwmFlush();
    // 持续排入桌面工作使消息队列无法空闲，验证心跳仍能在同一线程得到调度。
    private static void CheckBusyClock(InputMessageLoop loop)
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        loop.Post(() =>
        {
            int owner = Environment.CurrentManagedThreadId;
            int beats = 0;
            long deadline = Environment.TickCount64 + 3000;
            loop.SetClock(() =>
            {
                if (Environment.CurrentManagedThreadId != owner) finished.TrySetException(new Exception("Unexpected heartbeat thread"));
                beats++;
            });
            // 模拟接收队列每处理完一小批，就立刻投递下一批。
            void Drain()
            {
                if (beats >= 3 || Environment.TickCount64 >= deadline || finished.Task.IsCompleted)
                {
                    loop.SetClock(null);
                    if (beats >= 3) finished.TrySetResult();
                    else finished.TrySetException(new Exception("Queued desktop input starved the heartbeat"));
                    return;
                }
                Thread.Sleep(2);
                loop.Post(Drain);
            }
            Drain();
        });
        finished.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
    }

    // 用另一个线程模拟活动实例和异常退出，保留旧句柄时也按所有权判定。
    private static void CheckInstanceLock()
    {
        string name = @"Local\SimpleLanShare.Test." + Guid.NewGuid().ToString("N");
        using var retained = new Mutex(false, name);
        using var next = Mutex.OpenExisting(name);
        if (!Program.TryOwnInstance(next)) throw new Exception("Stale mutex handle prevented launch");
        next.ReleaseMutex();
        using var owned = new ManualResetEventSlim();
        using var leave = new ManualResetEventSlim();
        var owner = new Thread(() => { retained.WaitOne(); owned.Set(); leave.Wait(); });
        owner.Start(); owned.Wait();
        try { if (Program.TryOwnInstance(next)) throw new Exception("Duplicate instance accepted"); }
        finally { leave.Set(); owner.Join(); }
        if (!Program.TryOwnInstance(next)) throw new Exception("Abandoned mutex prevented launch");
        next.ReleaseMutex();
    }

    // 阻塞模拟输入线程，确认 UI 调用停机可立即返回，解除阻塞后仍完整清理。
    private static void CheckInputShutdown()
    {
        using var loop = new InputMessageLoop();
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int cleanups = 0;
        loop.Post(() => { started.Set(); release.Wait(TimeSpan.FromSeconds(5)); });
        started.Wait();
        try
        {
            var stopped = loop.StopAsync(() => cleanups++);
            if (stopped.IsCompleted || stopped != loop.StopAsync()) throw new Exception("Input shutdown did not yield");
            release.Set();
            stopped.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
            if (cleanups != 1) throw new Exception("Input cleanup was skipped or repeated");
        }
        finally { release.Set(); }
    }

}
