using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage.Streams;
using OpenClaw.Shared;
using OpenClaw.Shared.Capabilities;
using WinRT;

namespace OpenClawTray.Services;

/// <summary>
/// Records the screen using Windows.Graphics.Capture and encodes to MP4 via MediaTranscoder.
/// </summary>
internal sealed class ScreenRecordingService : IDisposable
{
    private readonly IOpenClawLogger _logger;
    private readonly ConcurrentDictionary<string, ActiveSession> _sessions = new();

    private const int MaxFps        = 60;
    private const int MinFps        = 1;
    private const int MinDurationMs = 250;
    private const int MaxDurationMs = 60_000;
    private const int PoolBuffers   = 2;

    public ScreenRecordingService(IOpenClawLogger logger)
    {
        _logger = logger;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public async Task<ScreenRecordResult> RecordAsync(ScreenRecordArgs args)
    {
        var durationMs  = Math.Clamp(args.DurationMs, MinDurationMs, MaxDurationMs);
        var fps         = Math.Clamp(args.Fps, MinFps, MaxFps);
        var screenIndex = args.ScreenIndex;

        _logger.Info($"[ScreenRecording] duration={durationMs}ms fps={fps} screen={screenIndex}");

        var item    = CreateCaptureItem(screenIndex);
        var width   = item.Size.Width;
        var height  = item.Size.Height;
        var d3d     = CreateDirect3DDevice();

        Direct3D11CaptureFramePool? pool    = null;
        GraphicsCaptureSession?     session = null;
        var latestFrame = (Direct3D11CaptureFrame?)null;
        using var ready = new SemaphoreSlim(0, 1);
        var frames = new List<byte[]>();

        try
        {
            pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                d3d,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                PoolBuffers,
                new global::Windows.Graphics.SizeInt32 { Width = width, Height = height });

            session = pool.CreateCaptureSession(item);
            session.IsCursorCaptureEnabled = false;

            pool.FrameArrived += (p, _) =>
            {
                var f = p.TryGetNextFrame();
                if (f == null) return;
                Interlocked.Exchange(ref latestFrame, f)?.Dispose();
                try { ready.Release(); } catch { /* already signaled */ }
            };

            session.StartCapture();

            var intervalMs  = 1000 / fps;
            var deadline    = DateTime.UtcNow.AddMilliseconds(durationMs);
            var nextCapture = DateTime.UtcNow;

            while (DateTime.UtcNow < deadline)
            {
                var waitMs = (int)(nextCapture - DateTime.UtcNow).TotalMilliseconds;
                if (waitMs > 0)
                    await Task.Delay(waitMs);

                if (!await ready.WaitAsync(intervalMs * 2))
                    continue;

                var frame = Interlocked.Exchange(ref latestFrame, null);
                if (frame == null) continue;

                using (frame)
                {
                    try
                    {
                        var bmp = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface);
                        frames.Add(ExtractBitmapBytes(bmp));
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn($"[ScreenRecording] Frame skipped: {ex.Message}");
                    }
                }

                nextCapture = nextCapture.AddMilliseconds(intervalMs);
            }
        }
        finally
        {
            session?.Dispose();
            pool?.Dispose();
            Interlocked.Exchange(ref latestFrame, null)?.Dispose();
        }

        _logger.Info($"[ScreenRecording] Captured {frames.Count} frames, encoding...");

        var base64 = await EncodeToMp4Async(frames, width, height, fps);

        return new ScreenRecordResult
        {
            Format      = "mp4",
            Base64      = base64,
            DurationMs  = durationMs,
            Fps         = fps,
            ScreenIndex = screenIndex,
            Width       = width,
            Height      = height,
            HasAudio    = false,
        };
    }

    public Task<string> StartAsync(ScreenRecordStartArgs args)
    {
        var fps         = Math.Clamp(args.Fps, MinFps, MaxFps);
        var screenIndex = args.ScreenIndex;

        _logger.Info($"[ScreenRecording] start fps={fps} screen={screenIndex}");

        var item   = CreateCaptureItem(screenIndex);
        var width  = item.Size.Width;
        var height = item.Size.Height;
        var d3d    = CreateDirect3DDevice();

        var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            d3d,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            PoolBuffers,
            new global::Windows.Graphics.SizeInt32 { Width = width, Height = height });

        var captureSession = pool.CreateCaptureSession(item);
        captureSession.IsCursorCaptureEnabled = false;

        var session = new ActiveSession(screenIndex, fps, width, height, pool, captureSession, _logger);
        _sessions[session.Id] = session;

        _logger.Info($"[ScreenRecording] started session {session.Id}");
        return Task.FromResult(session.Id);
    }

    public async Task<ScreenRecordResult> StopAsync(string recordingId)
    {
        if (!_sessions.TryRemove(recordingId, out var session))
            throw new KeyNotFoundException($"Recording session '{recordingId}' not found");

        _logger.Info($"[ScreenRecording] stopping session {recordingId}...");

        List<byte[]> frames;
        int width, height, fps, screenIndex, durationMs;
        using (session)
        {
            (frames, durationMs) = await session.StopAsync();
            width       = session.Width;
            height      = session.Height;
            fps         = session.Fps;
            screenIndex = session.ScreenIndex;
        }

        _logger.Info($"[ScreenRecording] session {recordingId}: {frames.Count} frames, encoding...");
        var base64 = await EncodeToMp4Async(frames, width, height, fps);

        return new ScreenRecordResult
        {
            Format      = "mp4",
            Base64      = base64,
            DurationMs  = durationMs,
            Fps         = fps,
            ScreenIndex = screenIndex,
            Width       = width,
            Height      = height,
            HasAudio    = false,
        };
    }

    public void Dispose()
    {
        foreach (var kv in _sessions)
        {
            if (_sessions.TryRemove(kv.Key, out var s))
                try { s.Dispose(); } catch { }
        }
    }

    // ── Encoding ──────────────────────────────────────────────────────────────

    private static async Task<string> EncodeToMp4Async(
        List<byte[]> frames, int width, int height, int fps)
    {
        var output = new InMemoryRandomAccessStream();

        var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD720p);
        profile.Video.Width  = (uint)width;
        profile.Video.Height = (uint)height;
        profile.Video.FrameRate.Numerator   = (uint)fps;
        profile.Video.FrameRate.Denominator = 1;
        profile.Audio = null;

        var input = BuildRawVideoStream(frames, width, height);

        PrepareTranscodeResult? xcode = null;
        try
        {
            xcode = await new MediaTranscoder { HardwareAccelerationEnabled = true }
                .PrepareStreamTranscodeAsync(input, output, profile);
        }
        catch
        {
            xcode = await new MediaTranscoder { HardwareAccelerationEnabled = false }
                .PrepareStreamTranscodeAsync(input, output, profile);
        }

        if (!xcode.CanTranscode)
            throw new InvalidOperationException($"Transcode failed: {xcode.FailureReason}");

        await xcode.TranscodeAsync();

        output.Seek(0);
        var reader = new DataReader(output);
        await reader.LoadAsync((uint)output.Size);
        var bytes = new byte[output.Size];
        reader.ReadBytes(bytes);
        return Convert.ToBase64String(bytes);
    }

    private static InMemoryRandomAccessStream BuildRawVideoStream(
        List<byte[]> frames, int width, int height)
    {
        var stream = new InMemoryRandomAccessStream();
        var writer = new DataWriter(stream);
        foreach (var frame in frames)
            writer.WriteBytes(BgraToNv12(frame, width, height));
        writer.StoreAsync().AsTask().Wait();
        stream.Seek(0);
        return stream;
    }

    /// <summary>BT.601 limited-range BGRA→NV12 conversion.</summary>
    private static byte[] BgraToNv12(byte[] bgra, int width, int height)
    {
        var nv12   = new byte[width * height * 3 / 2];
        int yBase  = 0;
        int uvBase = width * height;

        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int i = (y * width + x) * 4;
            byte b = bgra[i], g = bgra[i + 1], r = bgra[i + 2];

            nv12[yBase++] = (byte)(16 + (66 * r + 129 * g + 25 * b) / 256);

            if ((y & 1) == 0 && (x & 1) == 0)
            {
                int uv = uvBase + (y / 2 * width) + (x & ~1);
                nv12[uv]     = (byte)(128 + (-38 * r -  74 * g + 112 * b) / 256);
                nv12[uv + 1] = (byte)(128 + (112 * r -  94 * g -  18 * b) / 256);
            }
        }

        return nv12;
    }

    // ── D3D11 / WinRT interop ─────────────────────────────────────────────────

    // IID_IDXGIDevice
    private static readonly Guid IID_DXGIDevice =
        new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c");

    private static IDirect3DDevice CreateDirect3DDevice()
    {
        // D3D_DRIVER_TYPE_HARDWARE=1, D3D11_CREATE_DEVICE_BGRA_SUPPORT=0x20, D3D11_SDK_VERSION=7
        D3D11CreateDevice(IntPtr.Zero, 1, IntPtr.Zero, 0x20, IntPtr.Zero, 0, 7,
            out var d3dPtr, IntPtr.Zero, IntPtr.Zero);

        var iid = IID_DXGIDevice;
        Marshal.QueryInterface(d3dPtr, ref iid, out var dxgiPtr);
        Marshal.Release(d3dPtr);

        NativeCreateDirect3D11DeviceFromDXGIDevice(dxgiPtr, out var winrtPtr);
        Marshal.Release(dxgiPtr);

        var device = MarshalInterface<IDirect3DDevice>.FromAbi(winrtPtr);
        Marshal.Release(winrtPtr);
        return device;
    }

    private static GraphicsCaptureItem CreateCaptureItem(int screenIndex)
    {
        var monitors = GetMonitorHandles();
        if (screenIndex < 0 || screenIndex >= monitors.Count)
            screenIndex = 0;

        const string classId = "Windows.Graphics.Capture.GraphicsCaptureItem";
        var iid = typeof(IGraphicsCaptureItemInterop).GUID;

        WindowsCreateString(classId, classId.Length, out var hstring);
        try
        {
            RoGetActivationFactory(hstring, ref iid, out var factoryPtr);
            var factory = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factoryPtr);
            Marshal.Release(factoryPtr);

            var itemIid = typeof(GraphicsCaptureItem).GUID;
            factory.CreateForMonitor(monitors[screenIndex], in itemIid, out var itemPtr);

            var item = MarshalInterface<GraphicsCaptureItem>.FromAbi(itemPtr);
            Marshal.Release(itemPtr);
            return item;
        }
        finally
        {
            WindowsDeleteString(hstring);
        }
    }

    private static List<IntPtr> GetMonitorHandles()
    {
        var handles = new List<IntPtr>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            (hMon, _, ref _, _) => { handles.Add(hMon); return true; },
            IntPtr.Zero);
        return handles;
    }

    private static byte[] ExtractBitmapBytes(SoftwareBitmap bitmap)
    {
        var capacity = (uint)(bitmap.PixelWidth * bitmap.PixelHeight * 4);
        var buf      = new global::Windows.Storage.Streams.Buffer(capacity);
        bitmap.CopyToBuffer(buf);
        using var dr = DataReader.FromBuffer(buf);
        var bytes = new byte[buf.Length];
        dr.ReadBytes(bytes);
        return bytes;
    }

    // ── P/Invoke declarations ─────────────────────────────────────────────────

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(
        IntPtr pAdapter, uint DriverType, IntPtr Software, uint Flags,
        IntPtr pFeatureLevels, uint FeatureLevels, uint SDKVersion,
        out IntPtr ppDevice, IntPtr pFeatureLevel, IntPtr ppImmediateContext);

    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice")]
    private static extern int NativeCreateDirect3D11DeviceFromDXGIDevice(
        IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [DllImport("combase.dll")]
    private static extern int WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string sourceString, int length, out IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(
        IntPtr runtimeClassId, ref Guid iid, out IntPtr factory);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(
        IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    private delegate bool MonitorEnumProc(
        IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        void CreateForWindow(IntPtr hwnd, in Guid riid, out IntPtr ppv);
        void CreateForMonitor(IntPtr hMonitor, in Guid riid, out IntPtr ppv);
    }

    // ── Active session (start/stop) ───────────────────────────────────────────

    private sealed class ActiveSession : IDisposable
    {
        public readonly string Id          = Guid.NewGuid().ToString("N")[..12];
        public readonly int    ScreenIndex;
        public readonly int    Fps;
        public readonly int    Width;
        public readonly int    Height;

        private readonly IOpenClawLogger              _logger;
        private readonly List<byte[]>                 _frames     = new();
        private readonly object                       _framesLock = new();
        private readonly CancellationTokenSource      _cts        = new();
        private readonly Direct3D11CaptureFramePool   _pool;
        private readonly GraphicsCaptureSession       _session;
        private readonly DateTime                     _startedAt  = DateTime.UtcNow;
        private volatile Direct3D11CaptureFrame?      _latestFrame;
        private readonly SemaphoreSlim                _ready      = new(0, 1);
        private readonly Task                         _captureTask;

        public ActiveSession(int screenIndex, int fps, int width, int height,
            Direct3D11CaptureFramePool pool, GraphicsCaptureSession session,
            IOpenClawLogger logger)
        {
            ScreenIndex = screenIndex; Fps = fps; Width = width; Height = height;
            _pool = pool; _session = session; _logger = logger;

            pool.FrameArrived += OnFrameArrived;
            session.StartCapture();
            _captureTask = RunAsync(_cts.Token);
        }

        private void OnFrameArrived(Direct3D11CaptureFramePool pool, object _)
        {
            var f = pool.TryGetNextFrame();
            if (f == null) return;
            Interlocked.Exchange(ref _latestFrame, f)?.Dispose();
            try { _ready.Release(); } catch { /* already signaled */ }
        }

        private async Task RunAsync(CancellationToken ct)
        {
            var intervalMs  = 1000 / Fps;
            var nextCapture = DateTime.UtcNow;

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var waitMs = (int)(nextCapture - DateTime.UtcNow).TotalMilliseconds;
                    if (waitMs > 0) await Task.Delay(waitMs, ct);

                    if (!await _ready.WaitAsync(intervalMs * 2, ct)) continue;
                }
                catch (OperationCanceledException) { break; }

                var frame = Interlocked.Exchange(ref _latestFrame, null);
                if (frame == null) continue;

                using (frame)
                {
                    try
                    {
                        var bmp   = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface);
                        var bytes = ExtractBitmapBytes(bmp);
                        lock (_framesLock) _frames.Add(bytes);
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn($"[ScreenRecording] Session {Id} frame skipped: {ex.Message}");
                    }
                }

                nextCapture = nextCapture.AddMilliseconds(intervalMs);
            }
        }

        public async Task<(List<byte[]> frames, int durationMs)> StopAsync()
        {
            _cts.Cancel();
            try { await _captureTask; } catch (OperationCanceledException) { } catch { }

            var durationMs = (int)(DateTime.UtcNow - _startedAt).TotalMilliseconds;
            List<byte[]> snapshot;
            lock (_framesLock) snapshot = new List<byte[]>(_frames);
            return (snapshot, durationMs);
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _session.Dispose(); } catch { }
            try { _pool.Dispose(); } catch { }
            Interlocked.Exchange(ref _latestFrame, null)?.Dispose();
            _cts.Dispose();
            _ready.Dispose();
        }
    }
}
