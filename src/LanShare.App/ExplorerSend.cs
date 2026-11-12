using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using LanShare.Core;

namespace LanShare.App;

// 一次文件右键操作固定一个已配对目标，不能隐式转成广播。
internal sealed record ExplorerSend(string PeerId, string FilePath)
{
    internal bool Valid => Security.IsFingerprint(PeerId) && !string.IsNullOrWhiteSpace(FilePath)
        && FilePath.Length <= 32767 && Path.IsPathFullyQualified(FilePath) && !FilePath.Contains('\0');
}

// 资源管理器启动的短命进程通过当前用户的命名管道投递到本登录会话的托盘实例。
internal static class ExplorerSendPipe
{
    private const int MaxBytes = 256 * 1024;
    internal static string Name => "SimpleLanShare.Send." + Process.GetCurrentProcess().SessionId;

    // 等待主实例启动；收到入队确认即退出，文件传输由托盘继续。
    internal static async Task ForwardAsync(string name, ExplorerSend request)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(request);
        if (bytes.Length > MaxBytes) throw new IOException("文件路径过长。");
        await pipe.WriteAsync(bytes, timeout.Token).ConfigureAwait(false);
        byte[] ack = new byte[1];
        await pipe.ReadExactlyAsync(ack, timeout.Token).ConfigureAwait(false);
        if (ack[0] != 1) throw new IOException("后台程序未能接收发送请求。");
    }

    // 每条消息有长度和读取时限，损坏的请求不会占住后续右键操作。
    internal static async Task ListenAsync(string name, Func<ExplorerSend, bool> enqueue, CancellationToken stop)
    {
        // 即使另一台机器使用同一个 Windows 账号，也不能通过网络调用本机的资源管理器命令。
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new IOException("无法确定当前用户。");
        var access = new PipeSecurity();
        // 与 .NET 管道客户端的所有者校验一致；提升权限时令牌的默认所有者可能是管理员组。
        access.SetOwner(identity.Owner ?? user);
        access.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl, AccessControlType.Deny));
        access.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        while (!stop.IsCancellationRequested)
        {
            using var pipe = NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Message, PipeOptions.Asynchronous, 4096, 4096, access);
            await pipe.WaitForConnectionAsync(stop).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                using var message = new MemoryStream();
                var chunk = new byte[4096];
                do
                {
                    int count = await pipe.ReadAsync(chunk, timeout.Token).ConfigureAwait(false);
                    if (count == 0 || message.Length + count > MaxBytes) throw new InvalidDataException();
                    message.Write(chunk, 0, count);
                } while (!pipe.IsMessageComplete);
                var request = JsonSerializer.Deserialize<ExplorerSend>(message.GetBuffer().AsSpan(0, (int)message.Length));
                bool accepted = request?.Valid == true && enqueue(request);
                await pipe.WriteAsync(new byte[] { accepted ? (byte)1 : (byte)0 }, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or OperationCanceledException) { }
        }
    }
}
