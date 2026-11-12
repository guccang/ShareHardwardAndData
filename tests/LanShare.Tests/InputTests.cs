using LanShare.Core;

// 用三个内存桌面模拟物理输入，覆盖授权、跨屏、网络超时和按键释放。
internal static class InputTests
{
    // 分离网络投递与物理操作，确保请求和确认之间的输入仍由本机处理。
    internal static void Run(Action<bool, string> check)
    {
        string[] ids = ["A", "B", "C"];
        var desktops = ids.ToDictionary(id => id, _ => new Desktop());
        var routers = new Dictionary<string, SharedInput>();
        var allowed = new HashSet<(string Target, string Source)>();
        var packets = new Queue<(string From, string To, InputPacket Event)>();
        long clock = 1000;
        foreach (var id in ids)
        {
            routers[id] = new(id, desktops[id], Neighbor, source => allowed.Contains((id, source)),
                (to, packet) => { packets.Enqueue((id, to, packet)); return true; }, () => desktops[id].Selecting, () => clock);
            routers[id].Enable(true);
        }
        var a = routers["A"]; var b = routers["B"]; var c = routers["C"];
        var target = desktops["B"];
        var ctrl = new InputPacket(InputOp.Key, default, 162, 29);
        var leftDown = new InputPacket(InputOp.Button, default, 1, 1);
        PushEdge();
        check(a.Mode == ControlMode.Waiting && target.Events.Count == 0, "edge waits before intercepting");
        Dispatch();
        check(a.Mode == ControlMode.Local && b.Mode == ControlMode.Local, "data pairing does not grant input");
        allowed.Add(("B", "A")); allowed.Add(("C", "A"));
        PushEdge();
        check(!a.PhysicalEvent(ctrl), "physical key while waiting passes through and cancels request");
        Dispatch();
        check(b.Mode == ControlMode.Local, "cancel before grant ends both sides");
        desktops["A"].PhysicalInputHeld = true;
        PushEdge();
        check(a.Mode == ControlMode.Local, "local drag cannot cross edge");
        desktops["A"].PhysicalInputHeld = false;
        desktops["A"].Selecting = true; PushEdge();
        check(a.Mode == ControlMode.Local, "area selection keeps pointer local");
        desktops["A"].Selecting = false;
        PushEdge(); Dispatch();
        check(a.Mode == ControlMode.Sending && b.Mode == ControlMode.Receiving && target.Position.X == 1, "grant activates both ends");
        b.Receive("A", new(InputOp.Button, Guid.NewGuid(), 2, 1));
        check(target.Events.Count == 0, "wrong lease cannot operate remote desktop");
        a.PhysicalEvent(ctrl); a.PhysicalEvent(leftDown); a.PhysicalEvent(new(InputOp.Wheel, default, -120, 1)); Dispatch();
        check(target.Events.Select(p => p.Op).SequenceEqual(new[] { InputOp.Key, InputOp.Button, InputOp.Wheel }), "keys, mouse buttons and wheel stay ordered");
        clock += 400; a.Pointer(900, 50); Dispatch();
        check(a.Peer == "B" && target.Position.X == 99, "drag at far edge stays on same target");
        a.ReturnLocal("紧急返回"); Dispatch();
        check(target.Events.Count(p => p.Op == InputOp.Key && p.C == 1) == 1 && target.Events.Count(p => p.Op == InputOp.Button && p.B == 0) == 1, "emergency return releases held keys and mouse buttons");
        PushEdge(); Dispatch();
        target.Selecting = true; clock += 350; b.Tick(); Dispatch(); a.Pointer(900, 50); Dispatch();
        check(a.Peer == "B", "remote screenshot selection prevents edge crossing");
        target.Selecting = false; clock += 350; b.Tick(); Dispatch(); a.Pointer(900, 50); Dispatch();
        check(a.Peer == "C" && c.Mode == ControlMode.Receiving && b.Mode == ControlMode.Local, "cross B to C using A peripherals");
        clock += 350; a.Pointer(0, 50); Dispatch();
        check(a.Peer == "B" && b.Mode == ControlMode.Receiving, "reverse direction through three computers");
        clock += 350; a.Pointer(-200, 50); Dispatch();
        check(a.Mode == ControlMode.Local && desktops["A"].Position.X == 98, "cross adjacent edge back to source");
        PushEdge(); Dispatch();
        a.PhysicalEvent(ctrl); Dispatch();
        check(!b.Pointer(20, 20), "target physical mouse takes local control");
        Dispatch();
        check(a.Mode == ControlMode.Local && b.Mode == ControlMode.Local && target.Events.Last().C == 1, "target takeover releases modifier");
        PushEdge(); Dispatch();
        a.PhysicalEvent(leftDown); Dispatch();
        clock += 1900; b.Tick(); packets.Clear(); a.Tick(); packets.Clear();
        check(a.Mode == ControlMode.Local && b.Mode == ControlMode.Local && target.Events.Last().B == 0, "heartbeat loss releases both ends without a network");
        a.Pointer(99, 50); a.Pointer(99, 50);
        check(a.Mode == ControlMode.Local, "return requires leaving the edge before another switch");
        PushEdge(); Dispatch();
        a.PhysicalEvent(ctrl); Dispatch(); b.Enable(false); Dispatch();
        check(a.Mode == ControlMode.Local && !b.Enabled && target.Events.Last().C == 1, "locking or disabling releases held keys");
        PushEdge(); Dispatch();
        check(a.Mode == ControlMode.Local, "disabled receiving side denies incoming request");
        b.Enable(true);
        check(a.Mode == ControlMode.Local && b.Mode == ControlMode.Local, "unlock does not resume previous control");

        // 使用桌面左右位置计算邻居，模拟页面排列。
        string? Neighbor(string id, int side)
        {
            int index = Array.IndexOf(ids, id) + side;
            return index >= 0 && index < ids.Length ? ids[index] : null;
        }
        // 模拟离开边缘后再次主动推过外侧的动作。
        void PushEdge()
        {
            clock += 500; a.Pointer(50, 50); a.Pointer(99, 50); a.Pointer(99, 50);
        }
        // 按写入顺序递送到各个独立状态机，包括接收端的确认和结束响应。
        void Dispatch()
        {
            while (packets.TryDequeue(out var p)) routers[p.To].Receive(p.From, p.Event);
        }
    }

    // 内存桌面记录注入顺序与光标位置，不向运行测试的操作系统发送输入。
    private sealed class Desktop : IInputDesktop
    {
        public int Width => 100;
        public int Height => 100;
        public (int X, int Y) Anchor => (50, 50);
        public bool PhysicalInputHeld { get; set; }
        public bool Selecting { get; set; }
        public (int X, int Y) Position { get; private set; }
        public List<InputPacket> Events { get; } = [];
        // 模拟目标桌面的绝对定位。
        public bool Move(int x, int y) { Position = (x, y); return true; }
        // 保存系统接收的事件，供释放及排序断言使用。
        public bool Inject(InputPacket packet) { Events.Add(packet); return true; }
    }
}
