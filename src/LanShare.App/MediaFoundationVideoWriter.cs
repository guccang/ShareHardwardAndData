using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace LanShare.App;

// 使用 Windows 自带 Media Foundation 写入 H.264/MP4；所有原生对象只在创建线程上使用和释放。
internal sealed unsafe class MediaFoundationVideoWriter : IDisposable
{
    private nint writer;
    private uint stream;
    private bool comStarted, mediaStarted, completed;
    private readonly Size size;
    internal const int FramesPerSecond = 20;
    internal const long FrameDuration = TimeSpan.TicksPerSecond / FramesPerSecond;
    private static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid FrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid FrameRate = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    private static readonly Guid AspectRatio = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    private static readonly Guid Interlace = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    private static readonly Guid Bitrate = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    private static readonly Guid Stride = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    private static readonly Guid Video = new("73646976-0000-0010-8000-00aa00389b71");
    private static readonly Guid H264 = new("34363248-0000-0010-8000-00aa00389b71");
    private static readonly Guid Rgb32 = new("00000016-0000-0010-8000-00aa00389b71");

    // 显式建立视频流，初始化失败也成对释放 COM 和 Media Foundation。
    public MediaFoundationVideoWriter(string path, Size dimensions)
    {
        if (dimensions.Width < 2 || dimensions.Height < 2 || dimensions.Width > 1920 || dimensions.Height > 1080 ||
            dimensions.Width % 2 != 0 || dimensions.Height % 2 != 0) throw new ArgumentException("视频尺寸须为不超过 1920×1080 的偶数。");
        size = dimensions;
        nint output = 0, input = 0;
        try
        {
            int hr = CoInitializeEx(0, 0);
            // 自检入口可能已经处于 STA；已有线程单元可继续使用，但不能代替其释放 COM。
            if (hr != unchecked((int)0x80010106)) { Check(hr); comStarted = true; }
            Check(MFStartup(0x20070, 0)); mediaStarted = true;
            Check(MFCreateSinkWriterFromURL(path, 0, 0, out writer));
            output = CreateType(H264);
            SetUInt32(output, Bitrate, 6_000_000);
            uint index;
            Check(((delegate* unmanaged[Stdcall]<nint, nint, uint*, int>)Method(writer, 3))(writer, output, &index)); // AddStream
            stream = index;
            input = CreateType(Rgb32);
            SetUInt32(input, Stride, (uint)(size.Width * 4));
            Check(((delegate* unmanaged[Stdcall]<nint, uint, nint, nint, int>)Method(writer, 4))(writer, stream, input, 0)); // SetInputMediaType
            Check(((delegate* unmanaged[Stdcall]<nint, int>)Method(writer, 5))(writer)); // BeginWriting
        }
        catch { Release(ref input); Release(ref output); Dispose(); throw; }
        finally { Release(ref input); Release(ref output); }
    }

    // 为输入和输出设置一致的尺寸、帧率、像素比例及逐行扫描属性。
    private nint CreateType(Guid format)
    {
        Check(MFCreateMediaType(out nint type));
        try
        {
            SetGuid(type, MajorType, Video); SetGuid(type, Subtype, format);
            SetUInt64(type, FrameSize, ((ulong)size.Width << 32) | (uint)size.Height);
            SetUInt64(type, FrameRate, ((ulong)FramesPerSecond << 32) | 1);
            SetUInt64(type, AspectRatio, (1UL << 32) | 1);
            SetUInt32(type, Interlace, 2);
            return type;
        }
        catch { Release(ref type); throw; }
    }

    // 拷贝逐行 BGRA 像素并写入时间戳；缓冲区由样本保留，不把整段录像留在内存里。
    public void WriteFrame(Bitmap bitmap, long timestamp, long duration = FrameDuration)
    {
        ObjectDisposedException.ThrowIf(writer == 0, this);
        if (completed || bitmap.Size != size || timestamp < 0 || duration <= 0) throw new ArgumentException("视频帧尺寸或时间无效。");
        nint buffer = 0, sample = 0;
        try
        {
            uint length = checked((uint)(size.Width * size.Height * 4));
            Check(MFCreateMemoryBuffer(length, out buffer));
            nint pixels;
            Check(((delegate* unmanaged[Stdcall]<nint, nint*, nint, nint, int>)Method(buffer, 3))(buffer, &pixels, 0, 0)); // Lock
            try
            {
                var data = bitmap.LockBits(new Rectangle(Point.Empty, size), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
                try
                {
                    int row = size.Width * 4;
                    for (int y = 0; y < size.Height; y++)
                        Buffer.MemoryCopy((void*)(data.Scan0 + y * data.Stride), (void*)(pixels + y * row), row, row);
                }
                finally { bitmap.UnlockBits(data); }
            }
            finally { Check(((delegate* unmanaged[Stdcall]<nint, int>)Method(buffer, 4))(buffer)); } // Unlock
            Check(((delegate* unmanaged[Stdcall]<nint, uint, int>)Method(buffer, 6))(buffer, length)); // SetCurrentLength
            Check(MFCreateSample(out sample));
            Check(((delegate* unmanaged[Stdcall]<nint, nint, int>)Method(sample, 42))(sample, buffer)); // AddBuffer
            Check(((delegate* unmanaged[Stdcall]<nint, long, int>)Method(sample, 36))(sample, timestamp)); // SetSampleTime
            Check(((delegate* unmanaged[Stdcall]<nint, long, int>)Method(sample, 38))(sample, duration)); // SetSampleDuration
            Check(((delegate* unmanaged[Stdcall]<nint, uint, nint, int>)Method(writer, 6))(writer, stream, sample)); // WriteSample
        }
        finally { Release(ref sample); Release(ref buffer); }
    }

    // 写完容器索引后才能把临时文件公布为可播放的视频。
    public void Complete()
    {
        ObjectDisposedException.ThrowIf(writer == 0, this);
        if (completed) return;
        Check(((delegate* unmanaged[Stdcall]<nint, int>)Method(writer, 11))(writer)); // Finalize
        completed = true;
    }

    // 虚表槽位来自 Windows SDK 的 mfobjects.h / mfreadwrite.h，包含 IUnknown 的前三个方法。
    private static nint Method(nint instance, int slot) => ((nint*)(*(nint*)instance))[slot];

    // IMFAttributes.SetGUID 是包含 IUnknown 在内的第 24 槽。
    private static void SetGuid(nint instance, Guid key, Guid value) =>
        Check(((delegate* unmanaged[Stdcall]<nint, Guid*, Guid*, int>)Method(instance, 24))(instance, &key, &value));

    // 设置 32 位媒体属性，例如码率和有符号行距的位表示。
    private static void SetUInt32(nint instance, Guid key, uint value) =>
        Check(((delegate* unmanaged[Stdcall]<nint, Guid*, uint, int>)Method(instance, 21))(instance, &key, value));

    // 用高低各 32 位保存尺寸或分数，等价于 MFSetAttributeSize / Ratio。
    private static void SetUInt64(nint instance, Guid key, ulong value) =>
        Check(((delegate* unmanaged[Stdcall]<nint, Guid*, ulong, int>)Method(instance, 22))(instance, &key, value));

    // 将原生错误转为异常，交给录制控制器显示，而不是留下静默失败的视频。
    private static void Check(int result) { if (result < 0) Marshal.ThrowExceptionForHR(result); }

    // 只释放本类拥有的原生引用，不使用跨线程 RCW 或强制垃圾回收。
    private static void Release(ref nint instance) { if (instance != 0) { Marshal.Release(instance); instance = 0; } }

    // 必须在同一录制线程释放编码器、媒体平台和 COM 单元。
    public void Dispose()
    {
        Release(ref writer);
        if (mediaStarted) { MFShutdown(); mediaStarted = false; }
        if (comStarted) { CoUninitialize(); comStarted = false; }
    }

    // 初始化当前线程的 COM 单元。
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(nint reserved, uint mode);
    // 释放本线程持有的 COM 初始化计数。
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    // 启动 Windows 媒体平台。
    [DllImport("mfplat.dll")] private static extern int MFStartup(uint version, uint flags);
    // 释放媒体平台的初始化计数。
    [DllImport("mfplat.dll")] private static extern int MFShutdown();
    // 根据 .mp4 扩展名建立文件容器。
    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)] private static extern int MFCreateSinkWriterFromURL(string url, nint stream, nint attributes, out nint sink);
    // 创建视频媒体类型属性集合。
    [DllImport("mfplat.dll")] private static extern int MFCreateMediaType(out nint mediaType);
    // 创建编码器输入帧缓冲。
    [DllImport("mfplat.dll")] private static extern int MFCreateMemoryBuffer(uint length, out nint buffer);
    // 创建携带时间戳和帧缓冲的媒体样本。
    [DllImport("mfplat.dll")] private static extern int MFCreateSample(out nint sample);
}
