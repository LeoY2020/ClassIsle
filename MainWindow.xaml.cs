using System.Runtime.InteropServices;
using ClassIsle.Models;
using ClassIsle.Services;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Graphics;
using WinUIEx;

namespace ClassIsle;

public enum IslandState { Collapsed, Expanded, Notifying, BlackScreen }

public sealed partial class MainWindow : Window
{
    private readonly IntPtr _hwnd;
    private NativeMethods.SUBCLASSPROC? _subclassProc;

    private readonly AppSettings _settings;
    private readonly ScheduleService _schedule;

    private IslandState _state = IslandState.Collapsed;
    private DateTime _lastInteraction = DateTime.Now;
    private DateTime _notifyEnd = DateTime.Now;
    private bool _notifyIsNap;
    private readonly DispatcherTimer _fastTimer = new() { Interval = TimeSpan.FromMilliseconds(25) };
    private readonly DispatcherTimer _secondTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTime _lastWeatherRefresh = DateTime.MinValue;
    private WeatherInfo? _weather;
    private NativeMethods.RECT _monitorRect;

    // 玻璃渲染（WinUI Composition）
    private ContainerVisual? _glassRoot;
    private Windows.Foundation.Size _glassSize;

    // 胶囊屏幕区域（窗口坐标系，物理像素）
    private Windows.Foundation.Rect _pillRectDip;
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
        AppWindow.Move(new PointInt32(_monitorRect.Left, _monitorRect.Top));
        AppWindow.Resize(new SizeInt32(_monitorRect.Right - _monitorRect.Left, _monitorRect.Bottom - _monitorRect.Top));

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

        // 构建液态玻璃渲染层（尺寸就绪后由 UpdatePillRect 幂等重建）
        UpdatePillRect();

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
        else if (_state is IslandState.Expanded or IslandState.Notifying)
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
        _clockText = null; _countdownText = null; _countdownBar = null; _countdownCaption = null;
        _currentText = null; _currentColorBlock = null; _moreText = null; _weatherText = null;
        _weatherIcon = null; _dateText = null; _countdownDayText = null; _countdownDayCaption = null;

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
    private static SolidColorBrush DimWhite() => new(Windows.UI.Color.FromArgb(255, 200, 200, 205));

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
                Windows.UI.Color.FromArgb(255, color.R, color.G, color.B));
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
            TryBuildGlass();
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

    // ==================== 液态玻璃渲染（WinUI Composition） ====================

    /// <summary>胶囊尺寸变化时（幂等）重建玻璃视觉树</summary>
    private void TryBuildGlass()
    {
        try
        {
            var w = IslandRoot.ActualWidth;
            var h = Math.Max(40, IslandRoot.ActualHeight);
            if (w <= 0 || h <= 0) return;
            if (_glassRoot != null
                && Math.Abs(_glassSize.Width - w) < 0.5
                && Math.Abs(_glassSize.Height - h) < 0.5)
                return;
            _glassSize = new Windows.Foundation.Size(w, h);
            BuildGlassVisuals((float)w, (float)h);
        }
        catch { }
    }

    /// <summary>
    /// 构建液态玻璃六层视觉（自底向上）：
    /// ⑥ 阴影+黑底座 → ① 实时背景模糊 → ② 菲涅尔（折射近似+边缘亮度） → ③ 黑色渐变 → ④ 漂移高光 → ⑤ 边框描边
    /// </summary>
    private void BuildGlassVisuals(float fw, float fh)
    {
        var compositor = ElementCompositionPreview.GetElementVisual(IslandRoot).Compositor;
        var radius = fh / 2f;

        var root = compositor.CreateContainerVisual();
        root.Size = new System.Numerics.Vector2(fw, fh);

        // 胶囊圆角裁剪（所有层共享）
        var pillGeo = compositor.CreateRoundedRectangleGeometry();
        pillGeo.Size = root.Size;
        pillGeo.CornerRadius = new System.Numerics.Vector2(radius);
        var pillClip = compositor.CreateGeometricClip();
        pillClip.Geometry = pillGeo;
        root.Clip = pillClip;

        // ⑥ 阴影（模糊 28 / 垂直偏移 6 / 黑 30%）+ 不透明黑底座
        var baseVisual = compositor.CreateShapeVisual();
        baseVisual.Size = root.Size;
        var baseShape = compositor.CreateSpriteShape(pillGeo);
        baseShape.FillBrush = compositor.CreateColorBrush(Windows.UI.Color.FromArgb(255, 0, 0, 0));
        baseVisual.Shapes.Add(baseShape);
        var shadow = compositor.CreateDropShadow();
        shadow.BlurRadius = 28f;
        shadow.Offset = new System.Numerics.Vector3(0, 6, 0);
        shadow.Color = Windows.UI.Color.FromArgb(77, 0, 0, 0); // 30%
        baseVisual.Shadow = shadow;
        root.Children.Add(baseVisual);

        // ① 实时背景模糊：Host Backdrop 由系统合成器采样窗口背后内容，
        //    背后窗口移动/切换/变化时实时响应；模糊度足以让文字图标不可辨认
        CompositionBrush backdrop;
        try { backdrop = compositor.CreateHostBackdropBrush(); }
        catch { backdrop = compositor.CreateBackdropBrush(); }
        var blur = compositor.CreateSpriteVisual();
        blur.Brush = backdrop;
        // ② 菲涅尔折射（近似）：模糊层向四周放大 6%，边缘从胶囊外侧采样背景，
        //    产生玻璃边缘向内弯曲的错位感；中心区域不受影响
        blur.Size = new System.Numerics.Vector2(fw * 1.06f, fh * 1.12f);
        blur.Offset = new System.Numerics.Vector3(-fw * 0.03f, -fh * 0.06f, 0);
        root.Children.Add(blur);

        // ② 菲涅尔边缘亮度（边缘 +5~8%，角部两方向叠加略高）
        const float band = 6f; // 边缘带宽（px）
        var fresnelH = compositor.CreateSpriteVisual();
        fresnelH.Size = root.Size;
        fresnelH.Brush = MakeEdgeGradient(compositor, fw, horizontal: true, band);
        root.Children.Add(fresnelH);
        var fresnelV = compositor.CreateSpriteVisual();
        fresnelV.Size = root.Size;
        fresnelV.Brush = MakeEdgeGradient(compositor, fh, horizontal: false, band);
        root.Children.Add(fresnelV);

        // ③ 黑色渐变叠加：上部近不透明纯黑 → 底部微透，透出模糊背景自然融合
        var shade = compositor.CreateSpriteVisual();
        shade.Size = root.Size;
        var blackGrad = compositor.CreateLinearGradientBrush();
        blackGrad.StartPoint = new System.Numerics.Vector2(0, 0);
        blackGrad.EndPoint = new System.Numerics.Vector2(0, fh);
        blackGrad.ColorStops.Insert(0, compositor.CreateColorGradientStop(0.00f, Windows.UI.Color.FromArgb(250, 0, 0, 0)));
        blackGrad.ColorStops.Insert(1, compositor.CreateColorGradientStop(0.72f, Windows.UI.Color.FromArgb(240, 0, 0, 0)));
        blackGrad.ColorStops.Insert(2, compositor.CreateColorGradientStop(0.92f, Windows.UI.Color.FromArgb(226, 0, 0, 0)));
        blackGrad.ColorStops.Insert(3, compositor.CreateColorGradientStop(1.00f, Windows.UI.Color.FromArgb(212, 0, 0, 0)));
        shade.Brush = blackGrad;
        root.Children.Add(shade);

        // ④ 实时高光：顶部内侧 2~4px 处的三条 RGB 微偏移条带（彩虹色散），
        //    水平方向 ±3px 缓慢漂移，周期 10 秒（贝塞尔缓动，持续动画禁止静态）
        var highlight = compositor.CreateContainerVisual();
        float stripW = fw * 0.5f;
        (byte A, byte R, byte G, byte B, float Y)[] tints =
        {
            (70, 255, 130, 130, 2.5f),
            (110, 130, 255, 130, 3.2f),
            (70, 130, 130, 255, 3.9f),
        };
        foreach (var (a, r, g, b, y) in tints)
        {
            var strip = compositor.CreateSpriteVisual();
            strip.Size = new System.Numerics.Vector2(stripW, 1.6f);
            strip.Offset = new System.Numerics.Vector3(0, y, 0);
            var grad = compositor.CreateLinearGradientBrush();
            grad.StartPoint = new System.Numerics.Vector2(0, 0.8f);
            grad.EndPoint = new System.Numerics.Vector2(stripW, 0.8f);
            grad.ColorStops.Insert(0, compositor.CreateColorGradientStop(0f, Windows.UI.Color.FromArgb(0, r, g, b)));
            grad.ColorStops.Insert(1, compositor.CreateColorGradientStop(0.5f, Windows.UI.Color.FromArgb(a, r, g, b)));
            grad.ColorStops.Insert(2, compositor.CreateColorGradientStop(1f, Windows.UI.Color.FromArgb(0, r, g, b)));
            strip.Brush = grad;
            highlight.Children.Add(strip);
        }
        var drift = compositor.CreateVector3KeyFrameAnimation();
        var ease = compositor.CreateCubicBezierEasingFunction(
            new System.Numerics.Vector2(0.42f, 0f), new System.Numerics.Vector2(0.58f, 1f));
        drift.InsertKeyFrame(0f, new System.Numerics.Vector3(-3, 0, 0), ease);
        drift.InsertKeyFrame(0.5f, new System.Numerics.Vector3(3, 0, 0), ease);
        drift.InsertKeyFrame(1f, new System.Numerics.Vector3(-3, 0, 0), ease);
        drift.Duration = TimeSpan.FromSeconds(10);
        drift.IterationBehavior = AnimationIterationBehavior.Forever;
        highlight.StartAnimation("Offset", drift);
        root.Children.Add(highlight);

        // ⑤ 边框描边：顶/侧 25% 白 → 底 8% 白（垂直渐变）
        var borderGeo = compositor.CreateRoundedRectangleGeometry();
        borderGeo.Size = new System.Numerics.Vector2(fw - 2, fh - 2);
        borderGeo.Offset = new System.Numerics.Vector2(1, 1);
        borderGeo.CornerRadius = new System.Numerics.Vector2(Math.Max(1f, radius - 1));
        var borderVisual = compositor.CreateShapeVisual();
        borderVisual.Size = root.Size;
        var borderShape = compositor.CreateSpriteShape(borderGeo);
        var borderGrad = compositor.CreateLinearGradientBrush();
        borderGrad.StartPoint = new System.Numerics.Vector2(0, 0);
        borderGrad.EndPoint = new System.Numerics.Vector2(0, fh);
        borderGrad.ColorStops.Insert(0, compositor.CreateColorGradientStop(0f, Windows.UI.Color.FromArgb(64, 255, 255, 255)));
        borderGrad.ColorStops.Insert(1, compositor.CreateColorGradientStop(1f, Windows.UI.Color.FromArgb(20, 255, 255, 255)));
        borderShape.StrokeBrush = borderGrad;
        borderShape.StrokeThickness = 1.2f;
        borderVisual.Shapes.Add(borderShape);
        root.Children.Add(borderVisual);

        ElementCompositionPreview.SetElementChildVisual(GlassHost, root);
        _glassRoot = root;
    }

    /// <summary>菲涅尔边缘亮度渐变：边缘白色 → 6px 带内线性衰减至透明（横向或纵向）</summary>
    private static CompositionBrush MakeEdgeGradient(Compositor compositor, float extent, bool horizontal, float band)
    {
        var b = compositor.CreateLinearGradientBrush();
        b.StartPoint = new System.Numerics.Vector2(0, 0);
        b.EndPoint = horizontal
            ? new System.Numerics.Vector2(extent, 0)
            : new System.Numerics.Vector2(0, extent);
        var edge = Windows.UI.Color.FromArgb(14, 255, 255, 255);
        var clear = Windows.UI.Color.FromArgb(0, 255, 255, 255);
        var k = Math.Min(0.45f, band / Math.Max(1f, extent));
        b.ColorStops.Insert(0, compositor.CreateColorGradientStop(0f, edge));
        b.ColorStops.Insert(1, compositor.CreateColorGradientStop(k, clear));
        b.ColorStops.Insert(2, compositor.CreateColorGradientStop(1 - k, clear));
        b.ColorStops.Insert(3, compositor.CreateColorGradientStop(1f, edge));
        return b;
    }
}
