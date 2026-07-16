using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Huaci.App.Services.Notebook;

namespace Huaci.App.Views;

/// <summary>
/// A non-activating, persistent alarm banner that flies across the top of the
/// Windows virtual desktop until the user explicitly dismisses it.
/// </summary>
public partial class AlarmBannerWindow : Window
{
    private const int GwlExStyle = -20;
    private const nint WsExNoActivate = 0x08000000;
    private const nint WsExToolWindow = 0x00000080;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const int WmDisplayChange = 0x007E;
    private const int WmDpiChanged = 0x02E0;
    private const int WmNcHitTest = 0x0084;
    private const int HtTransparent = -1;
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;
    private const double HostHeightDip = 108;
    private const double FlightSpeedDipPerSecond = 185;
    private static readonly nint HwndTopmost = new(-1);

    private readonly IAlarmFlybySound _flybySound;
    private readonly List<AnimationBinding> _secondaryAnimations = [];
    private HwndSource? _windowSource;
    private AnimationClock? _flightAnimationClock;
    private Guid? _activeAlarmId;
    private Guid? _lastSoundAlarmId;
    private bool _animationPaused;
    private bool _allowClose;
    private bool _displayUpdateQueued;
    private bool _soundDisposed;

    public AlarmBannerWindow()
        : this(new AlarmFlybySound())
    {
    }

    public AlarmBannerWindow(IAlarmFlybySound flybySound)
    {
        _flybySound = flybySound ?? throw new ArgumentNullException(nameof(flybySound));
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        SizeChanged += OnWindowSizeChanged;
    }

    /// <summary>Raised only when the user clicks the banner's close button.</summary>
    public event Action? Dismissed;

    /// <summary>The complete reminder text; the visible banner may ellipsize it.</summary>
    public string CurrentText { get; private set; } = string.Empty;

    public bool IsReminderActive => IsVisible && CurrentText.Length > 0;

    /// <summary>
    /// Shows or updates the reminder. The banner loops indefinitely and does
    /// not activate or steal keyboard focus from the user's current app.
    /// </summary>
    public void ShowReminder(string? text) => ShowReminder(Guid.NewGuid(), text);

    public void ShowReminder(Guid alarmId, string? text)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.InvokeAsync(
                () => ShowReminder(alarmId, text),
                DispatcherPriority.Normal);
            return;
        }

        if (_allowClose || Dispatcher.HasShutdownStarted)
        {
            return;
        }

        bool wasActive = IsReminderActive;
        bool isNewAlarm = _activeAlarmId != alarmId;
        _activeAlarmId = alarmId;
        CurrentText = NormalizeReminderText(text);
        MessageTextBlock.Text = CurrentText;
        MessageTextBlock.ToolTip = CurrentText;

        if (!IsVisible)
        {
            Show();
        }

        PositionAcrossVirtualDesktop();
        QueueAnimationRestart();
        if (!wasActive || _secondaryAnimations.Count == 0)
        {
            StartSecondaryAnimations();
        }

        if (isNewAlarm && _lastSoundAlarmId != alarmId)
        {
            _lastSoundAlarmId = alarmId;
            _flybySound.Play();
        }
    }

    /// <summary>
    /// Hides the banner without treating it as a user dismissal, then removes
    /// the reminder text from the retained window.
    /// </summary>
    public void HideReminder()
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.InvokeAsync(HideReminder, DispatcherPriority.Normal);
            return;
        }

        HideReminderCore();
    }

    /// <summary>Destroys the retained WPF window during application exit.</summary>
    public void CloseForExit()
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.InvokeAsync(CloseForExit, DispatcherPriority.Send);
            return;
        }

        if (_allowClose)
        {
            return;
        }

        _allowClose = true;
        StopFlightAnimation();
        StopSecondaryAnimations();
        DisposeFlybySound();
        ClearReminderState();
        if (_windowSource is not null)
        {
            _windowSource.RemoveHook(WindowMessageHook);
            _windowSource = null;
        }

        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            DismissFromUser();
            return;
        }

        StopFlightAnimation();
        StopSecondaryAnimations();
        DisposeFlybySound();
        base.OnClosing(e);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _windowSource = PresentationSource.FromVisual(this) as HwndSource;
        if (_windowSource is null)
        {
            return;
        }

        nint extendedStyle = GetWindowLongPtr(_windowSource.Handle, GwlExStyle);
        _ = SetWindowLongPtr(
            _windowSource.Handle,
            GwlExStyle,
            extendedStyle | WsExNoActivate | WsExToolWindow);
        _windowSource.AddHook(WindowMessageHook);
    }

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!IsVisible || CurrentText.Length == 0)
        {
            return;
        }

        UpdateResponsiveMessageWidth();
        QueueAnimationRestart();
    }

    private void CloseReminderButton_OnClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        DismissFromUser();
    }

    private void FlightAssembly_OnMouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        ClockController? controller = _flightAnimationClock?.Controller;
        if (_animationPaused || controller is null)
        {
            return;
        }

        controller.Pause();
        foreach (AnimationBinding binding in _secondaryAnimations)
        {
            binding.Clock.Controller?.Pause();
        }

        _animationPaused = true;
    }

    private void FlightAssembly_OnMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        ClockController? controller = _flightAnimationClock?.Controller;
        if (!_animationPaused || controller is null)
        {
            return;
        }

        controller.Resume();
        foreach (AnimationBinding binding in _secondaryAnimations)
        {
            binding.Clock.Controller?.Resume();
        }

        _animationPaused = false;
    }

    private void DismissFromUser()
    {
        bool wasActive = IsReminderActive;
        HideReminderCore();
        if (wasActive)
        {
            Dismissed?.Invoke();
        }
    }

    private void HideReminderCore()
    {
        StopFlightAnimation();
        StopSecondaryAnimations();
        _flybySound.Stop();
        if (IsVisible)
        {
            Hide();
        }

        ClearReminderState();
    }

    private void ClearReminderState()
    {
        _activeAlarmId = null;
        CurrentText = string.Empty;
        MessageTextBlock.Text = string.Empty;
        MessageTextBlock.ToolTip = null;
        _displayUpdateQueued = false;
    }

    private void PositionAcrossVirtualDesktop()
    {
        if (_windowSource is null)
        {
            return;
        }

        int virtualLeft = GetSystemMetrics(SmXVirtualScreen);
        int virtualTop = GetSystemMetrics(SmYVirtualScreen);
        int virtualWidth = Math.Max(1, GetSystemMetrics(SmCxVirtualScreen));
        _ = GetSystemMetrics(SmCyVirtualScreen); // Forces fresh virtual metrics after topology changes.

        uint dpi = GetDpiForWindow(_windowSource.Handle);
        if (dpi == 0)
        {
            dpi = 96;
        }

        int hostHeightPixels = Math.Max(
            1,
            (int)Math.Ceiling(HostHeightDip * dpi / 96d));

        _ = SetWindowPos(
            _windowSource.Handle,
            HwndTopmost,
            virtualLeft,
            virtualTop,
            virtualWidth,
            hostHeightPixels,
            SwpNoActivate | SwpShowWindow);
    }

    private void QueueDisplayUpdate()
    {
        if (_displayUpdateQueued || !IsVisible)
        {
            return;
        }

        _displayUpdateQueued = true;
        _ = Dispatcher.InvokeAsync(
            () =>
            {
                _displayUpdateQueued = false;
                if (!IsVisible || CurrentText.Length == 0)
                {
                    return;
                }

                PositionAcrossVirtualDesktop();
                UpdateResponsiveMessageWidth();
                RestartFlightAnimation();
            },
            DispatcherPriority.Loaded);
    }

    private void QueueAnimationRestart()
    {
        if (!IsVisible || CurrentText.Length == 0)
        {
            return;
        }

        _ = Dispatcher.InvokeAsync(
            () =>
            {
                if (!IsVisible || CurrentText.Length == 0)
                {
                    return;
                }

                UpdateResponsiveMessageWidth();
                RestartFlightAnimation();
            },
            DispatcherPriority.Loaded);
    }

    private void UpdateResponsiveMessageWidth()
    {
        double viewportWidth = ActualWidth > 0 ? ActualWidth : Width;
        MessageTextBlock.Width = Math.Clamp(viewportWidth * 0.30, 220, 520);
        FlightAssembly.UpdateLayout();
    }

    private void RestartFlightAnimation()
    {
        StopFlightAnimation();
        UpdateLayout();

        double assemblyWidth = Math.Max(FlightAssembly.ActualWidth, 320);
        double viewportWidth = Math.Max(ActualWidth, 1);
        double from = -assemblyWidth - 20;
        double to = viewportWidth + 20;
        double distance = to - from;
        double durationSeconds = Math.Clamp(distance / FlightSpeedDipPerSecond, 8, 45);

        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = TimeSpan.FromSeconds(durationSeconds),
            RepeatBehavior = RepeatBehavior.Forever
        };
        _flightAnimationClock =
            (AnimationClock)animation.CreateClock(hasControllableRoot: true);
        FlightTranslate.ApplyAnimationClock(
            System.Windows.Media.TranslateTransform.XProperty,
            _flightAnimationClock,
            HandoffBehavior.SnapshotAndReplace);
        _animationPaused = false;
    }

    private void StopFlightAnimation()
    {
        if (_flightAnimationClock is not null)
        {
            _flightAnimationClock.Controller?.Stop();
            _flightAnimationClock = null;
        }

        FlightTranslate.ApplyAnimationClock(
            System.Windows.Media.TranslateTransform.XProperty,
            null);
        FlightTranslate.X = 0;
        _animationPaused = false;
    }

    private void StartSecondaryAnimations()
    {
        StopSecondaryAnimations();

        ApplySecondaryAnimation(
            PlaneBobTranslate,
            System.Windows.Media.TranslateTransform.YProperty,
            CreateOscillation(-1.8, 1.8, TimeSpan.FromMilliseconds(820)));
        ApplySecondaryAnimation(
            PlanePitchRotate,
            System.Windows.Media.RotateTransform.AngleProperty,
            CreateOscillation(-1.6, 1.4, TimeSpan.FromMilliseconds(1060)));
        ApplySecondaryAnimation(
            WingFlexScale,
            System.Windows.Media.ScaleTransform.ScaleYProperty,
            CreateOscillation(0.97, 1.035, TimeSpan.FromMilliseconds(460)));
        ApplySecondaryAnimation(
            BannerSwayRotate,
            System.Windows.Media.RotateTransform.AngleProperty,
            CreateOscillation(-0.65, 0.65, TimeSpan.FromMilliseconds(1320)));
        ApplySecondaryAnimation(
            BannerBobTranslate,
            System.Windows.Media.TranslateTransform.YProperty,
            CreateOscillation(-0.8, 0.8, TimeSpan.FromMilliseconds(980)));
        ApplySecondaryAnimation(
            NavigationLight,
            OpacityProperty,
            CreateOscillation(0.24, 1, TimeSpan.FromMilliseconds(360)));
        ApplySecondaryAnimation(
            TowLineGlow,
            OpacityProperty,
            CreateOscillation(0.32, 0.95, TimeSpan.FromMilliseconds(540)));
        ApplySecondaryAnimation(
            PlaneShadowScale,
            System.Windows.Media.ScaleTransform.ScaleXProperty,
            CreateOscillation(0.88, 1.06, TimeSpan.FromMilliseconds(820)));
        ApplySecondaryAnimation(
            PlaneShadow,
            OpacityProperty,
            CreateOscillation(0.42, 0.72, TimeSpan.FromMilliseconds(820)));
        ApplySecondaryAnimation(
            EngineFanRotate,
            System.Windows.Media.RotateTransform.AngleProperty,
            new DoubleAnimation
            {
                From = 0,
                To = 360,
                Duration = TimeSpan.FromMilliseconds(430),
                RepeatBehavior = RepeatBehavior.Forever
            });
    }

    private void StopSecondaryAnimations()
    {
        foreach (AnimationBinding binding in _secondaryAnimations)
        {
            binding.Clock.Controller?.Stop();
            binding.Target.ApplyAnimationClock(binding.Property, null);
        }

        _secondaryAnimations.Clear();
        PlaneBobTranslate.Y = 0;
        PlanePitchRotate.Angle = 0;
        WingFlexScale.ScaleX = 1;
        WingFlexScale.ScaleY = 1;
        BannerSwayRotate.Angle = 0;
        BannerBobTranslate.Y = 0;
        NavigationLight.Opacity = 1;
        TowLineGlow.Opacity = 1;
        PlaneShadowScale.ScaleX = 1;
        PlaneShadowScale.ScaleY = 1;
        PlaneShadow.Opacity = 1;
        EngineFanRotate.Angle = 0;
    }

    private void ApplySecondaryAnimation(
        IAnimatable target,
        DependencyProperty property,
        DoubleAnimation animation)
    {
        var clock = (AnimationClock)animation.CreateClock(hasControllableRoot: true);
        target.ApplyAnimationClock(
            property,
            clock,
            HandoffBehavior.SnapshotAndReplace);
        _secondaryAnimations.Add(new AnimationBinding(target, property, clock));
    }

    private static DoubleAnimation CreateOscillation(
        double from,
        double to,
        TimeSpan duration) =>
        new()
        {
            From = from,
            To = to,
            Duration = duration,
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase
            {
                EasingMode = EasingMode.EaseInOut
            }
        };

    private void DisposeFlybySound()
    {
        if (_soundDisposed)
        {
            return;
        }

        _soundDisposed = true;
        _flybySound.Dispose();
    }

    private nint WindowMessageHook(
        nint hwnd,
        int message,
        nint wParam,
        nint lParam,
        ref bool handled)
    {
        if (message == WmNcHitTest && IsVisible)
        {
            if (GetCursorPos(out NativePoint cursor))
            {
                System.Windows.Point local = PointFromScreen(
                    new System.Windows.Point(cursor.X, cursor.Y));
                double top = Canvas.GetTop(FlightAssembly);
                if (double.IsNaN(top))
                {
                    top = 0;
                }

                var interactiveBounds = new Rect(
                    FlightTranslate.X,
                    top,
                    FlightAssembly.ActualWidth,
                    FlightAssembly.ActualHeight);
                if (!interactiveBounds.Contains(local))
                {
                    handled = true;
                    return new nint(HtTransparent);
                }
            }
        }
        else if (message is WmDpiChanged or WmDisplayChange)
        {
            QueueDisplayUpdate();
        }

        return nint.Zero;
    }

    private static string NormalizeReminderText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "提醒时间到了";
        }

        return string.Join(
            ' ',
            text.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private readonly record struct AnimationBinding(
        IAnimatable Target,
        DependencyProperty Property,
        AnimationClock Clock);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint window, int index, nint newValue);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint window,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);
}
