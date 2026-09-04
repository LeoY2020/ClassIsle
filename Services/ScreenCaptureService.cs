using System.Runtime.InteropServices;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.Imaging;
using WinRT;

namespace ClassIsle.Services;

/// <summary>
/// 实时屏幕捕获：Windows.Graphics.Capture 监视器捕获，
/// 下采样 + SoftwareBitmap 中转，供 Win2D 做背景模糊。
/// </summary>
public sealed class ScreenCaptureService : IDisposable
{
    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [ComVisible(true)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow([In] IntPtr window, in Guid iid);
        IntPtr CreateForMonitor([In] IntPtr monitor, in Guid iid);
    }

    private static readonly Guid GraphicsCaptureItemGuid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    // D3D11 设备创建（用于帧池）——Microsoft 官方 ScreenCapture 配方：
    // D3D11CreateDevice -> QI IDXGIDevice -> CreateDirect3D11DeviceFromDXGIDevice -> Marshal.GetObjectForIUnknown
    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice", SetLastError = true, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    private static extern uint CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [DllImport("d3d11.dll", EntryPoint = "D3D11CreateDevice", SetLastError = true)]
    private static extern int D3D11CreateDevice(
        IntPtr adapter, uint driverType, IntPtr software, uint flags,
        uint[] featureLevels, uint featureLevelsCount,
        uint sdkVersion, out IntPtr device, out uint featureLevel, out IntPtr context);

    private const uint D3D11_SDK_VERSION = 7;

    private static readonly Guid IDXGIDeviceGuid = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");

    private static Windows.Graphics.DirectX.Direct3D11.IDirect3DDevice CreateD3DDevice()
    {
        var levels = new uint[] { 11, 10, 9, 8 };
        var hr = D3D11CreateDevice(IntPtr.Zero, 0, IntPtr.Zero, 0x40 /* BGRA */,
            levels, (uint)levels.Length, D3D11_SDK_VERSION,
            out var d3dDevice, out _, out _);
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);

        IntPtr dxgiDevice;
        hr = Marshal.QueryInterface(d3dDevice, ref IDXGIDeviceGuid, out dxgiDevice);
        Marshal.Release(d3dDevice);
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);

        uint hr2 = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out var inspectable);
        Marshal.Release(dxgiDevice);
        if (hr2 != 0) Marshal.ThrowExceptionForHR(unchecked((int)hr2));

        try
        {
            return Marshal.GetObjectForIUnknown(inspectable) as Windows.Graphics.DirectX.Direct3D11.IDirect3DDevice
                ?? throw new InvalidOperationException("无法创建 Direct3D 设备");
        }
        finally
        {
            Marshal.Release(inspectable);
        }
    }

    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private SoftwareBitmap? _latest;
    private readonly object _lock = new();
    private const int DownScale = 2; // 下采样倍数，降低 CPU/GPU 开销

    public SizeInt32 MonitorSize { get; private set; }
    public bool IsRunning { get; private set; }

    public void Start(IntPtr hwnd)
    {
        if (IsRunning || !GraphicsCaptureSession.IsSupported())
            return;

        var hmon = MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
        var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        var itemPointer = interop.CreateForMonitor(hmon, GraphicsCaptureItemGuid);
        var item = GraphicsCaptureItem.FromAbi(itemPointer);
        Marshal.Release(itemPointer);

        MonitorSize = item.Size;
        var poolSize = new SizeInt32(
            Math.Max(1, item.Size.Width / DownScale),
            Math.Max(1, item.Size.Height / DownScale));

        var device = CreateD3DDevice();
        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, poolSize);
        _session = _framePool.CreateCaptureSession(item);
        _framePool.FrameArrived += OnFrameArrived;
        _session.StartCapture();
        IsRunning = true;
    }

    private async void OnFrameArrived(Direct3D11CaptureFramePool sender, object? args)
    {
        try
        {
            using var frame = sender.TryGetNextFrame();
            if (frame == null) return;
            var sb = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface);
            lock (_lock)
            {
                _latest?.Dispose();
                _latest = sb;
            }
        }
        catch
        {
            // 捕获失败时静默（例如设备丢失）
        }
    }

    /// <summary>获取最新一帧（线程安全，可能为 null）</summary>
    public SoftwareBitmap? GetLatest()
    {
        lock (_lock)
        {
            return _latest;
        }
    }

    public void Dispose()
    {
        IsRunning = false;
        if (_framePool != null)
        {
            try { _framePool.FrameArrived -= OnFrameArrived; } catch { }
            try { _framePool.Dispose(); } catch { }
        }
        try { _session?.Dispose(); } catch { }
        lock (_lock) { _latest?.Dispose(); _latest = null; }
    }
}
