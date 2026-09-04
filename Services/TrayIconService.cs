using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace ClassIsle.Services;

/// <summary>
/// 系统托盘驻留（原生 Shell_NotifyIcon 实现）。
/// 右键菜单仅两项：设置 / 退出。左键单击唤醒灵动岛。
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private const uint WM_APP_TRAY = 0x8000 + 1; // WM_APP + 1
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_RBUTTONUP = 0x0205;
    private const int MENU_SETTINGS = 100;
    private const int MENU_EXIT = 101;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassW(ref WNDCLASSEXW lpWndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, uint uIDNewItem, string lpNewItem);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenu(
        IntPtr hMenu, uint uFlags, int x, int y, int nReserved,
        IntPtr hWnd, IntPtr prcRect);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativeMethods.POINT pt);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern IntPtr CallWindowProcW(IntPtr lpPrevWndFunc, IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_DELETE = 0x00000002;
    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;
    private const uint HWND_MESSAGE = 0xFFFFFFFF;
    private const uint TPM_RETURNCMD = 0x00000100;
    private const uint TPM_RIGHTBUTTON = 0x00000002;
    private const uint WM_COMMAND = 0x0111;

    private IntPtr _hwnd;
    private readonly WndProc _wndProc;
    private readonly DispatcherQueue _dispatcher;
    private bool _added;

    public Action? OnSettingsRequested;
    public Action? OnExitRequested;
    public Action? OnLeftClick;

    public TrayIconService()
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _wndProc = WndProcImpl;
        var hInstance = GetModuleHandleW(null);
        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = hInstance,
            lpszClassName = "ClassIsle.TrayWnd",
        };
        RegisterClassW(ref wc);
        _hwnd = CreateWindowExW(0, 0, "ClassIsle.TrayWnd", "ClassIsle Tray", 0, 0, 0, 0,
            (IntPtr)HWND_MESSAGE, IntPtr.Zero, hInstance, IntPtr.Zero);
    }

    public void Show()
    {
        if (_added) return;
        var data = new NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_APP_TRAY,
            hIcon = LoadAppIcon(),
            szTip = "ClassIsle 灵动岛课表",
        };
        _added = Shell_NotifyIconW(NIM_ADD, ref data);
    }

    /// <summary>运行时生成一个简易应用图标（蓝色圆点胶囊）</summary>
    private static IntPtr LoadAppIcon()
    {
        const int size = 32;
        // AND 掩码：0 = 不透明
        var andMask = new byte[size * size / 8];
        // 异或色平面：每像素 4 字节 BGRA，画一个居中蓝色圆
        var color = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                double dx = x - 15.5, dy = y - 15.5;
                bool inside = dx * dx + dy * dy <= 14 * 14;
                int off = (y * size + x) * 4;
                if (inside)
                {
                    color[off + 0] = 0xFF;     // B
                    color[off + 1] = 0xAA;     // G
                    color[off + 2] = 0x4B;     // R
                    color[off + 3] = 0xFF;     // A
                }
            }
        }
        return CreateIcon(GetModuleHandleW(null), size, size, (byte)1, (byte)32, andMask, color);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr CreateIcon(
        IntPtr hInstance, int nWidth, int nHeight, byte cPlanes, byte cBitsPixel,
        byte[] lpbANDbits, byte[] lpbXORbits);

    private IntPtr WndProcImpl(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_APP_TRAY)
        {
            var mouse = (uint)lParam;
            if (mouse == WM_RBUTTONUP) ShowContextMenu();
            else if (mouse == WM_LBUTTONUP) _dispatcher.TryEnqueue(() => OnLeftClick?.Invoke());
        }
        else if (msg == WM_COMMAND)
        {
            var id = (int)(wParam & 0xFFFF);
            if (id == MENU_SETTINGS) _dispatcher.TryEnqueue(() => OnSettingsRequested?.Invoke());
            else if (id == MENU_EXIT) _dispatcher.TryEnqueue(() => OnExitRequested?.Invoke());
        }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        // 托盘菜单必须在前台窗口上下文中显示，否则点击外部无法关闭
        SetForegroundWindow(_hwnd);
        GetCursorPos(out var pt);
        var menu = CreatePopupMenu();
        AppendMenuW(menu, 0, (uint)MENU_SETTINGS, "设置");
        AppendMenuW(menu, 0, (uint)MENU_EXIT, "退出");
        var cmd = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, pt.X, pt.Y, 0, _hwnd, IntPtr.Zero);
        DestroyMenu(menu);
        if (cmd == MENU_SETTINGS) OnSettingsRequested?.Invoke();
        else if (cmd == MENU_EXIT) OnExitRequested?.Invoke();
    }

    public void Dispose()
    {
        if (_added)
        {
            var data = new NOTIFYICONDATAW
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
                hWnd = _hwnd,
                uID = 1,
            };
            Shell_NotifyIconW(NIM_DELETE, ref data);
            _added = false;
        }
        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
    }
}
