using System.Runtime.InteropServices;
using ClassIsle.Models;
using ClassIsle.Services;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using WinUIEx;

namespace ClassIsle;

public enum IslandState { Collapsed, Expanded, Notifying, BlackScreen }

public sealed partial class MainWindow : Window
{
    private readonly IntPtr _hwnd;
    private NativeMethods.SUBCLASSPROC? _subclassProc;

    private readonly AppSettings _settings;
    private readonly ScheduleService _schedule;
    private readonly ScreenCaptureService _capture = new();

    private IslandState _state = IslandState.Collapsed;
    private DateTime _lastInteraction = DateTime.Now;
    private DateTime _notifyEnd = DateTime.Now;
    private bool _notifyIsNap;
    private string _notifyCourseName = "";

    private readonly DispatcherTimer _fastTimer = new() { Interval = TimeSpan.FromMilliseconds(25) };
    private readonly DispatcherTimer _secondTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTime _lastWeatherRefresh = DateTime.MinValue;
    private WeatherInfo? _weather;
    private NativeMethods.RECT _monitorRect;

    // 玻璃渲染缓存
    private CanvasBitmap? _dispMap;
    private Windows.Foundation.Size _dispMapSize;
    private CanvasBitmap? _cachedBg;
    private SoftwareBitmap? _cachedBgSource;

    // 胶囊屏幕区域（窗口坐标系，物理像素）
    private Windows.Foundation.Rect _pillRectDip;
    private bool _suppressIdle;

    public MainWindow(AppSettings settings)
    {
        _settings = settings;
        _hwnd = Win32Interop.GetWindowFromWindowId(AppWindow.Id);

        // WORKAROUND 1: 先隐藏再 InitializeComponent，避免黑/白闪烁
        NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_HIDE);
        // WORKAROUND 2: InitializeComponent 前设置 WS_EX_NOREDIRECTIONBITMAP
        ApplyWindowStyles();
        InitializeComponent();

        // WORKAROUND 3/4: 边框与标题栏处理
        var presenter = OverlappedPresenter.Create();
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsResizable = false;
        presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
        presenter.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(presenter);
        ExtendsContentIntoTitleBar = true;
        AppWindow.IsShownInSwitchers = false;

        // 窗口覆盖整个监视器（透明区域通过 WM_NCHITTEST 穿透）
        var hmon = NativeMethods.MonitorFromWindow(_hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var mi = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        NativeMethods.GetMonitorInfo(hmon, ref mi);
        _monitorRect = mi.rcMonitor;
        AppWindow.MoveAndResize(new PointInt32(_monitorRect.Left, _monitorRect.Top,
            _monitorRect.Right - _monitorRect.Left, _monitorRect.Bottom - _monitorRect.Top));

        // WORKAROUND 5: AppWindow.* 之后重新应用样式
        ApplyWindowStyles();

        Activated += OnFirstActivated;
        Closed += (_, _) => Cleanup();

        // 课表调度
        _schedule = new ScheduleService(settings, OnScheduleEvent);
        _schedule.Start();

        // 定时器
        _fastTimer.Tick += (_, _) => OnFastTick();
        _secondTimer.Tick += (_, _) => OnSecondTick();

        IslandRoot.PointerMoved += (_, _) => _lastInteraction = DateTime.Now;
        IslandRoot.PointerPressed += (_, _) => _lastInteraction = DateTime.Now;
        ComponentsPanel.SizeChanged += (_, _) => UpdatePillRect();
    }

    private void OnFirstActivated(object sender, WindowActivatedEventArgs e)
    {
        Activated -= OnFirstActivated;
        // WORKAROUND 6: SystemBackdrop 延迟到首次激活
        SystemBackdrop = new TransparentTintBackdrop();
        // WORKAROUND 7
        ApplyWindowStyles();
        // WORKAROUND 8: 修补 XAML 岛子 HWND 的白色背景
        NativeMethods.EnumChildWindows(_hwnd, (childHwnd, _) =>
        {
            NativeMethods.SetClassLongPtr(childHwnd, NativeMethods.GCLP_HBRBACKGROUND,
                NativeMethods.GetStockObject(NativeMethods.NULL_BRUSH));
            return true;
        }, IntPtr.Zero);
        // WORKAROUND 9: 子类化拦截 WM_ERASEBKGND / WM_NCHITTEST / WM_DISPLAYCHANGE
        _subclassProc = SubclassProc;
        NativeMethods.SetWindowSubclass(_hwnd, _subclassProc, 1, 0);

        // 启动实时屏幕捕获
        try { _capture.Start(_hwnd); } catch { }

        // 不抢焦点
        var ex = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE, ex | 0x08000000 /* WS_EX_NOACTIVATE */);

        BuildComponents();
        _secondTimer.Start();
        _fastTimer.Start();
        _ = RefreshWeather();
    }

    private void Cleanup()
    {
        _fastTimer.Stop();
        _secondTimer.Stop();
        _capture.Dispose();
        if (_subclassProc != null)
        {
            NativeMethods.RemoveWindowSubclass(_hwnd, _subclassProc, 1);
            _subclassProc = null;
        }
    }

    private IntPtr SubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam,
        nuint uIdSubclass, nuint dwRefData)
    {
        const uint WM_NCHITTEST = 0x0084;
        const int HTTRANSPARENT = -1;
        const int HTCLIENT = 1;

        if (uMsg == WM_NCHITTEST)
        {
            var screenX = (short)(lParam.ToInt64() & 0xFFFF);
            var screenY = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
            var localX = screenX - _monitorRect.Left;
            var localY = screenY - _monitorRect.Top;

            if (_state == IslandState.BlackScreen)
                return HTCLIENT;

            // 胶囊区域内接收输入，其余穿透
            var scale = Root.XamlRoot?.RasterizationScale == 0 ? 1.0 : Root.XamlRoot!.RasterizationScale;
            var px = localX / scale;
            var py = localY / scale;
            if (px >= _pillRectDip.X - 6 && px <= _pillRectDip.X + _pillRectDip.Width + 6
                && py >= _pillRectDip.Y - 6 && py <= _pillRectDip.Y + _pillRectDip.Height + 6)
                return HTCLIENT;
            return HTTRANSPARENT;
        }
        if (uMsg == NativeMethods.WM_ERASEBKGND)
            return new IntPtr(1);
        if (uMsg == NativeMethods.WM_DISPLAYCHANGE)
            DispatcherQueue.TryEnqueue(ApplyWindowStyles);
        return NativeMethods.DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    private void ApplyWindowStyles()
    {
        NativeMethods.SetClassLongPtr(_hwnd, NativeMethods.GCLP_HBRBACKGROUND,
            NativeMethods.GetStockObject(NativeMethods.NULL_BRUSH));
        NativeMethods.SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_FRAMECHANGED);
        var style = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_STYLE);
        NativeMethods.SetWindowLong(_hwnd, NativeMethods.GWL_STYLE,
            style & ~(NativeMethods.WS_BORDER | NativeMethods.WS_DLGFRAME | NativeMethods.WS_THICKFRAME));
        var exStyle = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE,
            (exStyle | NativeMethods.WS_EX_NOREDIRECTIONBITMAP | NativeMethods.WS_EX_TOOLWINDOW)
            & ~NativeMethods.WS_EX_WINDOWEDGE);
        if (IsWindows11OrGreater())
        {
            var noBorder = NativeMethods.DWMWA_COLOR_NONE;
            NativeMethods.DwmSetWindowAttribute(_hwnd, NativeMethods.DWMWA_BORDER_COLOR, ref noBorder, sizeof(uint));
            var noRound = NativeMethods.DWMWCP_DONOTROUND;
            NativeMethods.DwmSetWindowAttribute(_hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref noRound, sizeof(uint));
        }
        var margins = new NativeMethods.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        NativeMethods.DwmExtendFrameIntoClientArea(_hwnd, ref margins);
    }

    private static bool IsWindows11OrGreater()
        => Environment.OSVersion.Version is { Major: > 10 } or { Major: 10, Build: >= 22000 };

    // ==================== 状态机：唤醒 / 收起 / 通知 / 黑屏 ====================

    private void OnFastTick()
    {
        if (_state == IslandState.Collapsed)
        {
            // 鼠标靠近屏幕顶边 ≤ 5 物理像素（触屏下滑同样会移动光标位置）即唤醒
            if (NativeMethods.GetCursorPos(out var pt))
            {
                var nearTop = pt.Y <= _monitorRect.Top + 5
                    && pt.X >= _monitorRect.Left && pt.X <= _monitorRect.Right;
                if (nearTop) Wake();
            }
        }
        else if (_state is IslandState.Expanded or IslandState.Notifying && !_suppressIdle)
        {
            // 午休通知持续到午休结束，不参与待机收起
            if (_state == IslandState.Notifying && _notifyIsNap) return;
            if (_state == IslandState.Notifying) return; // 通知由定时结束收起
            if ((DateTime.Now - _lastInteraction).TotalSeconds >= _settings.IdleSeconds)
                Collapse();
        }

        if (_state is IslandState.Expanded or IslandState.Notifying)
            UpdateCountdownComponent();
    }

    private void OnSecondTick()
    {
        UpdatePillRect();
        if (_state is IslandState.Expanded or IslandState.Notifying)
        {
            UpdateClockComponent();
            UpdateOtherComponents();

            // 通知到期收起
            if (_state == IslandState.Notifying && DateTime.Now >= _notifyEnd && !_notifyIsNap)
                Collapse();
        }
        if ((DateTime.Now - _lastWeatherRefresh).TotalMinutes >= 30)
            _ = RefreshWeather();
    }

    /// <summary>唤出：水滴 → 胶囊（弹性过冲）</summary>
    public void Wake()
    {
        if (_state is not IslandState.Collapsed) return;
        _state = IslandState.Expanded;
        _lastInteraction = DateTime.Now;

        BuildComponents();
        UpdateClockComponent();
        UpdateCountdownComponent();
        UpdateOtherComponents();
        UpdatePillRect();

        IslandRoot.Opacity = 0;
        NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_SHOWNOACTIVATE);
        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);

        // 弹性展开动画：纵向从压扁的"水滴"回弹到胶囊
        var visual = ElementCompositionPreview.GetElementVisual(IslandRoot);
        visual.CenterPoint = new System.Numerics.Vector3(
            (float)(IslandRoot.ActualWidth / 2), (float)(IslandRoot.ActualHeight / 2), 0);
        var compositor = visual.Compositor;

        var spring = compositor.CreateSpringVector3Animation();
        spring.InitialValue = new System.Numerics.Vector3(1.0f, 0.12f, 1.0f);
        spring.FinalValue = new System.Numerics.Vector3(1.0f, 1.0f, 1.0f);
        spring.DampingRatio = 0.55f;
        spring.Period = TimeSpan.FromMilliseconds(50);
        visual.StartAnimation("Scale", spring);

        var offset = compositor.CreateSpringVector3Animation();
        offset.InitialValue = new System.Numerics.Vector3(0, -46, 0);
        offset.FinalValue = new System.Numerics.Vector3(0, 0, 0);
        offset.DampingRatio = 0.6f;
        offset.Period = TimeSpan.FromMilliseconds(55);
        visual.StartAnimation("Offset", offset);

        // 快速淡入
        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0f, 0f);
        fade.InsertKeyFrame(1f, 1f);
        fade.Duration = TimeSpan.FromMilliseconds(120);
        visual.StartAnimation("Opacity", fade);
    }

    /// <summary>收起：向上滑出 + 淡出，0.25 秒贝塞尔缓动</summary>
    public void Collapse()
    {
        if (_state is IslandState.Collapsed or IslandState.BlackScreen) return;

        _state = IslandState.Collapsed;
        var visual = ElementCompositionPreview.GetElementVisual(IslandRoot);
        visual.StopAnimation("Scale");
        visual.StopAnimation("Offset");
        visual.StopAnimation("Opacity");
        visual.Scale = new System.Numerics.Vector3(1, 1, 1);
        visual.Offset = new System.Numerics.Vector3(0, 0, 0);

        var storyboard = new Storyboard();
        var translate = new TranslateTransform();
        IslandRoot.RenderTransform = translate;

        var yAnim = new DoubleAnimation
        {
            From = 0,
            To = -70,
            Duration = new Duration(TimeSpan.FromMilliseconds(250)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };
        Storyboard.SetTarget(yAnim, translate);
        Storyboard.SetTargetProperty(yAnim, "Y");
        storyboard.Children.Add(yAnim);

        var opacityAnim = new DoubleAnimation
        {
            From = 1,
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(250)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };
        Storyboard.SetTarget(opacityAnim, IslandRoot);
        Storyboard.SetTargetProperty(opacityAnim, "Opacity");
        storyboard.Children.Add(opacityAnim);

        storyboard.Completed += (_, _) =>
        {
            NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_HIDE);
            IslandRoot.Opacity = 1;
            IslandRoot.RenderTransform = null;
            // 恢复组件显示（下次唤醒直接可用）
            ComponentsPanel.Visibility = Visibility.Visible;
            NotifyPanel.Visibility = Visibility.Collapsed;
        };
        storyboard.Begin();
    }

    private void OnScheduleEvent(ScheduleEvent evt)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            // 黑屏期间屏蔽其他通知
            if (_state == IslandState.BlackScreen && evt.Type != ScheduleEventType.NapEnd)
                return;

            switch (evt.Type)
            {
                case ScheduleEventType.PrepareBell:
                    ShowNotification("准备上课", TimeSpan.FromSeconds(_settings.NotificationSeconds), false);
                    break;
                case ScheduleEventType.ClassStart:
                    ShowNotification("上课", TimeSpan.FromSeconds(_settings.NotificationSeconds), false);
                    break;
                case ScheduleEventType.ClassEnd:
                    ShowNotification("下课", TimeSpan.FromSeconds(_settings.NotificationSeconds), false);
                    break;
                case ScheduleEventType.Lunch:
                    ShowNotification("午饭时间", TimeSpan.FromSeconds(_settings.NotificationSeconds), false);
                    break;
                case ScheduleEventType.NapStart:
                    ShowNapNotification();
                    break;
                case ScheduleEventType.NapEnd:
                    ExitBlackScreen(true);
                    break;
            }
        });
    }

    private void ShowNotification(string text, TimeSpan duration, bool isNap)
    {
        _state = IslandState.Notifying;
        _notifyIsNap = isNap;
        _notifyEnd = DateTime.Now + duration;
        _lastInteraction = DateTime.Now;

        NotifyText.Text = text;
        BlackScreenButton.Visibility = isNap ? Visibility.Visible : Visibility.Collapsed;
        NotifyPanel.Visibility = Visibility.Visible;
        ComponentsPanel.Visibility = Visibility.Collapsed;
        UpdatePillRect();

        // 若窗口当前隐藏则显示
        if (!IsWindowVisibleSafe()) WakeNotify();
        _lastInteraction = DateTime.Now;
    }

    private bool IsWindowVisibleSafe()
        => NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_STYLE) >= 0 && IsWindowVisible(_hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    private void WakeNotify()
    {
        IslandRoot.Opacity = 1;
        NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_SHOWNOACTIVATE);
    }

    private void ShowNapNotification()
    {
        var end = DateTime.Today + ScheduleService.ParseTime(_settings.NapEnd);
        ShowNotification("午休时间", end - DateTime.Now, true);
    }

    // ==================== 黑屏模式 ====================

    private void OnBlackScreenClick(object sender, RoutedEventArgs e)
        => EnterBlackScreen();

    private void OnCloseBlackClick(object sender, RoutedEventArgs e)
        => ExitBlackScreen(false);

    private void EnterBlackScreen()
    {
        if (_state == IslandState.BlackScreen) return;
        _state = IslandState.BlackScreen;

        var w = Root.ActualWidth;
        var h = Root.ActualHeight;
        var pillCx = _pillRectDip.X + _pillRectDip.Width / 2;
        var pillCy = _pillRectDip.Y + _pillRectDip.Height / 2;

        BlackOverlay.Visibility = Visibility.Visible;
        BlackOverlay.Opacity = 1;
        IslandRoot.Visibility = Visibility.Collapsed;

        var scale = new ScaleTransform
        {
            CenterX = pillCx,
            CenterY = pillCy,
            ScaleX = _pillRectDip.Width / Math.Max(1, w),
            ScaleY = _pillRectDip.Height / Math.Max(1, h),
        };
        BlackOverlay.RenderTransform = scale;

        var storyboard = new Storyboard();
        var sx = new DoubleAnimation
        {
            To = 1.0,
            Duration = new Duration(TimeSpan.FromMilliseconds(450)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };
        Storyboard.SetTarget(sx, scale);
        Storyboard.SetTargetProperty(sx, "ScaleX");
        var sy = new DoubleAnimation
        {
            To = 1.0,
            Duration = new Duration(TimeSpan.FromMilliseconds(450)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };
        Storyboard.SetTarget(sy, scale);
        Storyboard.SetTargetProperty(sy, "ScaleY");
        storyboard.Children.Add(sx);
        storyboard.Children.Add(sy);
        storyboard.Begin();
    }

    private void ExitBlackScreen(bool becauseNapEnd)
    {
        if (_state != IslandState.BlackScreen) return;

        void Finish()
        {
            BlackOverlay.Visibility = Visibility.Collapsed;
            BlackOverlay.RenderTransform = null;
            IslandRoot.Visibility = Visibility.Visible;
            _state = IslandState.Collapsed;
            ComponentsPanel.Visibility = Visibility.Visible;
            NotifyPanel.Visibility = Visibility.Collapsed;
            NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_HIDE);
        }

        var w = Root.ActualWidth;
        var h = Root.ActualHeight;
        var pillCx = _pillRectDip.X + _pillRectDip.Width / 2;
        var pillCy = _pillRectDip.Y + _pillRectDip.Height / 2;
        var targetX = _pillRectDip.Width / Math.Max(1, w);
        var targetY = _pillRectDip.Height / Math.Max(1, h);

        var scale = new ScaleTransform { CenterX = pillCx, CenterY = pillCy };
        BlackOverlay.RenderTransform = scale;

        var storyboard = new Storyboard();
        var sx = new DoubleAnimation
        {
            To = targetX,
            Duration = new Duration(TimeSpan.FromMilliseconds(450)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(sx, scale);
        Storyboard.SetTargetProperty(sx, "ScaleX");
        var sy = new DoubleAnimation
        {
            To = targetY,
            Duration = new Duration(TimeSpan.FromMilliseconds(450)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(sy, scale);
        Storyboard.SetTargetProperty(sy, "ScaleY");
        storyboard.Children.Add(sx);
        storyboard.Children.Add(sy);
        storyboard.Completed += (_, _) => Finish();
        storyboard.Begin();
    }

    // ==================== 组件 ====================

    private TextBlock? _clockText;
    private TextBlock? _countdownText;
    private ProgressBar? _countdownBar;
    private TextBlock? _countdownCaption;
    private TextBlock? _currentText;
    private Border? _currentColorBlock;
    private TextBlock? _moreText;
    private TextBlock? _weatherText;
    private FontIcon? _weatherIcon;
    private TextBlock? _dateText;
    private TextBlock? _countdownDayText;
    private TextBlock? _countdownDayCaption;

    private void BuildComponents()
    {
        IslandRoot.Margin = new Thickness(0, _settings.TopMargin, 0, 0);
        ComponentsPanel.Children.Clear();
        _clockText = _countdownText = _countdownBar = _countdownCaption = _currentText =
            _currentColorBlock = _moreText = _weatherText = _weatherIcon = _dateText =
            _countdownDayText = _countdownDayCaption = null;

        if (_settings.ShowWeather)
        {
            var g = MakeComponent(200);
            _weatherIcon = new FontIcon { FontSize = 16, Foreground = White() };
            _weatherText = new TextBlock { FontSize = 13, Foreground = White(), VerticalAlignment = VerticalAlignment.Center };
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(_weatherIcon);
            sp.Children.Add(_weatherText);
            g.Children.Add(sp);
            ComponentsPanel.Children.Add(g);
        }
        if (_settings.ShowCountdown)
        {
            var g = MakeComponent(200);
            var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
            _countdownCaption = new TextBlock { FontSize = 10, Foreground = DimWhite(), Text = "距离下课还有" };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            _countdownText = new TextBlock { FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = White() };
            _countdownBar = new ProgressBar
            {
                Width = 60, Height = 3, Minimum = 0, Maximum = 100,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                Background = new SolidColorBrush(Microsoft.UI.Colors.DimGray),
                VerticalAlignment = VerticalAlignment.Center,
            };
            row.Children.Add(_countdownText);
            row.Children.Add(_countdownBar);
            sp.Children.Add(_countdownCaption);
            sp.Children.Add(row);
            g.Children.Add(sp);
            ComponentsPanel.Children.Add(g);
        }
        if (_settings.ShowCurrentActivity)
        {
            var g = MakeComponent(360);
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
            _currentColorBlock = new Border
            {
                Width = 22, Height = 22, CornerRadius = new CornerRadius(11),
                VerticalAlignment = VerticalAlignment.Center,
            };
            _currentText = new TextBlock
            {
                FontSize = 17, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = White(), VerticalAlignment = VerticalAlignment.Center,
            };
            sp.Children.Add(_currentColorBlock);
            sp.Children.Add(_currentText);
            g.Children.Add(sp);
            ComponentsPanel.Children.Add(g);
        }
        if (_settings.ShowMoreActivities)
        {
            var g = MakeComponent(290);
            _moreText = new TextBlock
            {
                FontSize = 13, Foreground = White(), VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            g.Children.Add(_moreText);
            ComponentsPanel.Children.Add(g);
        }
        if (_settings.ShowDate)
        {
            var g = MakeComponent(160);
            _dateText = new TextBlock { FontSize = 14, Foreground = White(), VerticalAlignment = VerticalAlignment.Center };
            g.Children.Add(_dateText);
            ComponentsPanel.Children.Add(g);
        }
        if (_settings.ShowClock)
        {
            var g = MakeComponent(160);
            _clockText = new TextBlock
            {
                FontSize = 22, FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                Foreground = White(), VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            g.Children.Add(_clockText);
            ComponentsPanel.Children.Add(g);
        }
        if (_settings.ShowCountdownDay)
        {
            var g = MakeComponent(200);
            var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
            _countdownDayCaption = new TextBlock { FontSize = 10, Foreground = DimWhite(), Text = $"距离 {_settings.CountdownDayTitle} 还有" };
            _countdownDayText = new TextBlock { FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = White() };
            sp.Children.Add(_countdownDayCaption);
            sp.Children.Add(_countdownDayText);
            g.Children.Add(sp);
            ComponentsPanel.Children.Add(g);
        }

        UpdatePillRect();
    }

    private static Grid MakeComponent(double width)
    {
        var g = new Grid
        {
            Width = width,
            Height = 40,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        g.Children.Add(new Border()); // 占位，内容居中由内部元素负责
        return g;
    }

    private static SolidColorBrush White() => new(Microsoft.UI.Colors.White);
    private static SolidColorBrush DimWhite() => new(Microsoft.UI.Color.FromArgb(255, 200, 200, 205));

    private void UpdateClockComponent()
    {
        if (_clockText != null)
            _clockText.Text = DateTime.Now.ToString("HH:mm");
        if (_dateText != null)
            _dateText.Text = $"{DateTime.Now.Month}月{DateTime.Now.Day}日 周{"一二三四五六日"[(int)DateTime.Now.DayOfWeek]}";
    }

    private void UpdateCountdownComponent()
    {
        if (_countdownText == null) return;
        var now = DateTime.Now.TimeOfDay;
        var current = _schedule.GetCurrentCourse(now);
        if (current != null)
        {
            var end = ScheduleService.ParseTime(current.EndTime);
            var remain = end - now;
            _countdownCaption!.Text = "距离下课还有";
            _countdownText.Text = $"{(int)remain.TotalMinutes:00}:{remain.Seconds:00}";
            var total = (ScheduleService.ParseTime(current.EndTime) - ScheduleService.ParseTime(current.StartTime)).TotalSeconds;
            var progress = total > 0 ? 100 - remain.TotalSeconds / total * 100 : 0;
            _countdownBar!.Value = Math.Clamp(progress, 0, 100);
        }
        else
        {
            var next = _schedule.GetNextCourse(now);
            if (next != null)
            {
                var remain = ScheduleService.ParseTime(next.StartTime) - now;
                if (remain < TimeSpan.Zero) remain = TimeSpan.Zero;
                _countdownCaption!.Text = "距离上课还有";
                _countdownText.Text = $"{(int)remain.TotalMinutes:00}:{remain.Seconds:00}";
                _countdownBar!.Value = 0;
            }
            else
            {
                _countdownCaption!.Text = "今日课程已结束";
                _countdownText.Text = "--:--";
                _countdownBar!.Value = 100;
            }
        }
    }

    private void UpdateOtherComponents()
    {
        var now = DateTime.Now.TimeOfDay;

        if (_currentText != null)
        {
            var act = _schedule.GetCurrentActivity(now);
            _currentText.Text = act?.Name ?? "空闲";
            var color = act != null ? CourseThemes.Get(act.Value.Name) : CourseThemes.Default;
            _currentColorBlock!.Background = new SolidColorBrush(
                Microsoft.UI.Colors.FromArgb(255, color.R, color.G, color.B));
        }
        if (_moreText != null)
        {
            var upcoming = _schedule.GetUpcomingCourses(now, 3);
            var names = string.Join(" · ", upcoming.Select(c => c.Name));
            if (names.Length > 16) names = names[..16] + "...";
            _moreText.Text = names.Length > 0 ? names : "今日无更多课程";
        }
        if (_weatherText != null && _weather != null)
        {
            _weatherText.Text = $"{_weather.TemperatureC}° {_settings.CityName}";
            _weatherIcon!.Glyph = _weather.IconGlyph;
        }
        else if (_weatherText != null)
        {
            _weatherText.Text = _settings.CityName;
            _weatherIcon!.Glyph = "\uE703";
        }
        if (_countdownDayText != null)
        {
            if (DateTime.TryParse(_settings.CountdownDayDate, out var target))
            {
                var days = (int)Math.Ceiling((target.Date - DateTime.Today).TotalDays);
                _countdownDayText.Text = days >= 0 ? $"{days} 天" : "已结束";
            }
            else _countdownDayText.Text = "--";
        }
    }

    private async Task RefreshWeather()
    {
        _lastWeatherRefresh = DateTime.Now;
        _weather = await WeatherService.GetAsync(_settings.Latitude, _settings.Longitude);
        if (_weather != null)
            DispatcherQueue.TryEnqueue(() => UpdateOtherComponents());
    }

    private void UpdatePillRect()
    {
        try
        {
            IslandRoot.UpdateLayout();
            if (IslandRoot.ActualWidth <= 0) return;
            var transform = IslandRoot.TransformToVisual(null);
            var topLeft = transform.TransformPoint(new Windows.Foundation.Point(0, 0));
            _pillRectDip = new Windows.Foundation.Rect(topLeft.X, topLeft.Y,
                IslandRoot.ActualWidth, Math.Max(40, IslandRoot.ActualHeight));
        }
        catch { }
    }

    public void ApplySettingsChanged()
    {
        BuildComponents();
        UpdateClockComponent();
        UpdateCountdownComponent();
        UpdateOtherComponents();
    }

    // ==================== 液态玻璃渲染（Win2D） ====================

    private void OnGlassUpdate(ICanvasAnimatedControl sender, CanvasAnimatedUpdateEventArgs args)
    {
        // 高光漂移等动画由时间驱动（Draw 中计算），此处无操作
    }

    private void OnGlassDraw(ICanvasAnimatedControl sender, CanvasAnimatedDrawEventArgs args)
    {
        var ds = args.DrawingSession;
        if (_state == IslandState.Collapsed || IslandRoot.Visibility == Visibility.Collapsed)
            return;

        if (_pillRectDip.Width <= 0) return;
        var rect = _pillRectDip;
        float radius = (float)(rect.Height / 2);

        // ---------- ① 实时背景模糊 ----------
        var pxPerDip = sender.ConvertDipsToPixels(1f, CanvasDpiRounding.Round);
        var capture = _capture.GetLatest();
        if (capture != null && _capture.IsRunning)
        {
            if (!ReferenceEquals(capture, _cachedBgSource))
            {
                _cachedBg?.Dispose();
                _cachedBg = CanvasBitmap.CreateFromSoftwareBitmap(sender, capture);
                _cachedBgSource = capture;
            }

            if (_cachedBg != null)
            {
                // DIP → 物理像素 → 捕获位图（下采样）坐标
                float downScale = _capture.MonitorSize.Width > 0
                    ? (float)_cachedBg.Size.Width / _capture.MonitorSize.Width : 1f;

                var srcRect = new Windows.Foundation.Rect(
                    rect.X * pxPerDip * downScale,
                    rect.Y * pxPerDip * downScale,
                    rect.Width * pxPerDip * downScale,
                    rect.Height * pxPerDip * downScale);

                ICanvasImage img = new CropEffect { Source = _cachedBg, SourceRectangle = srcRect };
                img = new GaussianBlurEffect { Source = img, BlurAmount = 18f, Optimization = EffectOptimization.Balanced };
                // ---------- ② 菲涅尔折射 ----------
                var map = EnsureDisplacementMap(sender, rect, radius);
                if (map != null)
                {
                    img = new DisplacementMapEffect
                    {
                        Source = img,
                        DisplacementMap = map,
                        Amount = 10f,
                        XChannelSelect = EffectChannelSelect.Red,
                        YChannelSelect = EffectChannelSelect.Green,
                    };
                }
                ds.DrawImage(img, rect, new Windows.Foundation.Rect(0, 0, rect.Width, rect.Height));
            }
        }

        // ---------- ② 边缘亮度提升（+5~8%） ----------
        var edgeGlow = new CanvasCommandList(sender);
        using (var cl = edgeGlow.CreateDrawingSession())
        {
            cl.DrawRoundedRectangle((float)rect.X, (float)rect.Y, (float)rect.Width, (float)rect.Height,
                radius, radius, Microsoft.UI.Colors.White, 7f);
        }
        ds.Blend = CanvasBlend.Add;
        ds.DrawImage(new GaussianBlurEffect { Source = edgeGlow, BlurAmount = 5f }, rect);
        ds.Blend = CanvasBlend.SourceOver;

        // ---------- ③ 黑色渐变叠加 ----------
        // 有内容：内容区接近不透明纯黑，边缘保留渐变过渡
        using (var layer = ds.CreateLayer(1f, rect))
        {
            var gradient = new CanvasLinearGradientBrush(sender, new[]
            {
                new Microsoft.Graphics.Canvas.Brushes.CanvasGradientStop { Position = 0f, Color = Microsoft.UI.Colors.FromArgb(248, 0, 0, 0) },
                new Microsoft.Graphics.Canvas.Brushes.CanvasGradientStop { Position = 0.75f, Color = Microsoft.UI.Colors.FromArgb(238, 0, 0, 0) },
                new Microsoft.Graphics.Canvas.Brushes.CanvasGradientStop { Position = 1f, Color = Microsoft.UI.Colors.FromArgb(215, 0, 0, 0) },
            })
            {
                StartPoint = new System.Numerics.Vector2((float)rect.X, (float)rect.Y),
                EndPoint = new System.Numerics.Vector2((float)rect.X, (float)(rect.Y + rect.Height)),
            };
            var inset = new Windows.Foundation.Rect(rect.X + 2, rect.Y + 2, rect.Width - 4, rect.Height - 3);
            ds.FillRoundedRectangle(inset, radius - 1, radius - 1, gradient);
        }

        // ---------- ④ 实时高光（缓慢漂移 + 彩虹色散，圆角处沿弧线） ----------
        double t = args.Timing.TotalTime.TotalSeconds;
        float drift = (float)(Math.Sin(t / 10.0 * Math.PI * 2) * 3.0); // 周期 10 秒，幅度 ±3px
        var highlightClip = new Windows.Foundation.Rect(rect.X + drift - 12, rect.Y - 1, rect.Width * 0.5, rect.Height * 0.55);
        using (var layer = ds.CreateLayer(1f, highlightClip))
        {
            var geo = CanvasGeometry.CreateRoundedRect(sender,
                (float)rect.X + 1.5f, (float)rect.Y + 2.5f,
                (float)rect.Width - 3f, (float)rect.Height - 4f, radius - 2, radius - 2);
            // 三条微偏移的 RGB 描边模拟彩虹色散
            ds.DrawGeometry(geo, Microsoft.UI.Colors.FromArgb(70, 255, 130, 130), 1.2f);
            var geo2 = CanvasGeometry.CreateRoundedRect(sender,
                (float)rect.X + 1.5f, (float)rect.Y + 3.2f,
                (float)rect.Width - 3f, (float)rect.Height - 4f, radius - 2, radius - 2);
            ds.DrawGeometry(geo2, Microsoft.UI.Colors.FromArgb(110, 130, 255, 130), 1.2f);
            var geo3 = CanvasGeometry.CreateRoundedRect(sender,
                (float)rect.X + 1.5f, (float)rect.Y + 3.9f,
                (float)rect.Width - 3f, (float)rect.Height - 4f, radius - 2, radius - 2);
            ds.DrawGeometry(geo3, Microsoft.UI.Colors.FromArgb(70, 130, 130, 255), 1.2f);
        }

        // ---------- ⑤ 边框描边 ----------
        ds.DrawRoundedRectangle((float)rect.X, (float)rect.Y, (float)rect.Width, (float)rect.Height,
            radius, radius, Microsoft.UI.Colors.FromArgb(20, 255, 255, 255), 1f);
        var topClip = new Windows.Foundation.Rect(rect.X, rect.Y, rect.Width, rect.Height - 10);
        using (var layer = ds.CreateLayer(1f, topClip))
        {
            ds.DrawRoundedRectangle((float)rect.X, (float)rect.Y, (float)rect.Width, (float)rect.Height,
                radius, radius, Microsoft.UI.Colors.FromArgb(64, 255, 255, 255), 1f);
        }
    }

    /// <summary>生成菲涅尔折射位移贴图（边缘向内弯曲，强度线性衰减）</summary>
    private CanvasBitmap? EnsureDisplacementMap(ICanvasResourceCreator device, Windows.Foundation.Rect rect, float radius)
    {
        int w = Math.Max(4, (int)Math.Ceiling(rect.Width));
        int h = Math.Max(4, (int)Math.Ceiling(rect.Height));
        if (_dispMap != null && _dispMapSize.Width == w && _dispMapSize.Height == h)
            return _dispMap;

        const int Band = 6;          // 边缘折射带宽度（px）
        const float MaxDisp = 0.12f; // 位移强度（归一化）
        var bytes = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                // 圆角矩形 SDF（内侧为正）
                float d = RoundedRectSDF(x + 0.5f, y + 0.5f, w, h, radius);
                int off = (y * w + x) * 4;
                float r = 0.5f, g = 0.5f;
                if (d < Band)
                {
                    // 距边界越近强度越大（线性衰减）
                    float k = (Band - d) / Band;
                    // 内法线方向：梯度
                    float nx, ny;
                    RoundedRectNormal(x + 0.5f, y + 0.5f, w, h, radius, out nx, out ny);
                    r = 0.5f + nx * MaxDisp * k;
                    g = 0.5f + ny * MaxDisp * k;
                }
                bytes[off + 0] = (byte)Math.Clamp(g * 255, 0, 255);
                bytes[off + 1] = (byte)Math.Clamp(r * 255, 0, 255);
                bytes[off + 2] = 0;
                bytes[off + 3] = 255;
            }
        }
        _dispMap?.Dispose();
        _dispMap = CanvasBitmap.CreateFromBytes(device, bytes, w, h,
            Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized, 96, CanvasAlphaMode.Premultiplied);
        _dispMapSize = new Size(w, h);
        return _dispMap;
    }

    private static float RoundedRectSDF(float px, float py, float w, float h, float r)
    {
        float cx = Math.Abs(px - w / 2f) - (w / 2f - r);
        float cy = Math.Abs(py - h / 2f) - (h / 2f - r);
        float ox = Math.Max(cx, 0), oy = Math.Max(cy, 0);
        float dist = MathF.Sqrt(ox * ox + oy * oy) + MathF.Min(Math.Max(cx, cy), 0) - r;
        return -dist; // 内侧为正
    }

    private static void RoundedRectNormal(float px, float py, float w, float h, float r, out float nx, out float ny)
    {
        float cx = Math.Abs(px - w / 2f) - (w / 2f - r);
        float cy = Math.Abs(py - h / 2f) - (h / 2f - r);
        float sx = Math.Sign(px - w / 2f), sy = Math.Sign(py - h / 2f);
        if (cx > 0 && cy > 0)
        {
            float len = MathF.Sqrt(cx * cx + cy * cy);
            nx = sx * cx / Math.Max(len, 1e-6f);
            ny = sy * cy / Math.Max(len, 1e-6f);
        }
        else if (cx > cy)
        {
            nx = sx; ny = 0;
        }
        else
        {
            nx = 0; ny = sy;
        }
    }
}
