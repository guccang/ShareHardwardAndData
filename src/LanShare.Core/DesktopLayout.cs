namespace LanShare.Core;

// 布局快照只包含排列和编辑版本。授权、设备名称及连接信息始终由各电脑自行管理。
public sealed record DesktopLayout(long Revision, string Author, string[] Order)
{
    // 限制快照大小并拒绝重复设备，保证可在控制通道的单个元数据帧中发送。
    public bool IsValid() => Revision > 0 && Revision < long.MaxValue && Security.IsFingerprint(Author) &&
        Order is { Length: > 0 and <= 192 } && Order.All(Security.IsFingerprint) &&
        Order.Contains(Author) && Order.Distinct().Count() == Order.Length;

    // 逻辑版本优先，并发编辑时所有电脑按相同的证书顺序决定胜者。
    public bool IsNewerThan(DesktopLayout? other) => other == null || Revision > other.Revision ||
        (Revision == other.Revision && string.CompareOrdinal(Author, other.Author) > 0);

    // 只显示本地认识的设备；还未进入共享快照的设备保留在列表中。
    public List<string> ForKnownDevices(IEnumerable<string> localOrder)
    {
        var known = localOrder.ToHashSet();
        return Order.Where(known.Contains).Concat(localOrder).Distinct().ToList();
    }

    // 编辑当前可见设备时保留离线或尚未与本机配对的其他设备在全局布局中的位置。
    public static DesktopLayout Edit(DesktopLayout? previous, string author, IReadOnlyList<string> localOrder)
    {
        var remaining = new Queue<string>(localOrder);
        var known = localOrder.ToHashSet();
        var order = new List<string>();
        foreach (string id in previous?.Order ?? [])
            order.Add(known.Contains(id) ? remaining.Dequeue() : id);
        order.AddRange(remaining);
        var next = new DesktopLayout(checked((previous?.Revision ?? 0) + 1), author, order.ToArray());
        return next.IsValid() ? next : throw new InvalidOperationException("无法同步此设备排列，设备数量或版本超出支持范围。");
    }
}
