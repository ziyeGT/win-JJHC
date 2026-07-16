using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Huaci.App.Services.Capture;
using InputKeyEventArgs = System.Windows.Input.KeyEventArgs;
using InputMouseEventArgs = System.Windows.Input.MouseEventArgs;
using MediaBrush = System.Windows.Media.Brush;
using MediaPen = System.Windows.Media.Pen;

namespace Huaci.App.Views;

/// <summary>
/// One physical-monitor segment of the frozen screenshot overlay. A separate
/// window is used per monitor so WPF receives the correct PerMonitorV2 scale
/// even when displays have different DPI settings.
/// </summary>
internal sealed class ScreenshotOverlayWindow : Window
{
    private static readonly nint HwndTopmost = new(-1);
    private const uint SwpShowWindow = 0x0040;

    private readonly ScreenRect _physicalBounds;
    private readonly Action<ScreenPoint> _selectionStarted;
    private readonly Action<ScreenPoint> _selectionChanged;
    private readonly Action<ScreenPoint> _selectionCompleted;
    private readonly Action _captureCancelled;
    private readonly SelectionShade _selectionShade;

    private bool _isDragging;
    private bool _isClosing;

    public ScreenshotOverlayWindow(
        ScreenRect physicalBounds,
        BitmapSource frozenImage,
        double dimOpacity,
        Action<ScreenPoint> selectionStarted,
        Action<ScreenPoint> selectionChanged,
        Action<ScreenPoint> selectionCompleted,
        Action captureCancelled)
    {
        _physicalBounds = physicalBounds;
        _selectionStarted = selectionStarted;
        _selectionChanged = selectionChanged;
        _selectionCompleted = selectionCompleted;
        _captureCancelled = captureCancelled;

        Title = "Huaci screenshot capture";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ShowInTaskbar = false;
        Topmost = true;
        Focusable = true;
        Background = System.Windows.Media.Brushes.Black;
        Cursor = System.Windows.Input.Cursors.Cross;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;

        // These values only seed HWND creation. SourceInitialized applies the
        // authoritative physical-pixel bounds through SetWindowPos.
        Left = physicalBounds.X;
        Top = physicalBounds.Y;
        Width = Math.Max(1, physicalBounds.Width);
        Height = Math.Max(1, physicalBounds.Height);

        var image = new System.Windows.Controls.Image
        {
            Source = frozenImage,
            Stretch = Stretch.Fill,
            SnapsToDevicePixels = true,
        };

        _selectionShade = new SelectionShade(dimOpacity)
        {
            IsHitTestVisible = false,
        };

        var root = new Grid();
        root.Children.Add(image);
        root.Children.Add(_selectionShade);
        Content = root;

        PreviewMouseLeftButtonDown += OnLeftButtonDown;
        PreviewMouseMove += OnMouseMove;
        PreviewMouseLeftButtonUp += OnLeftButtonUp;
        PreviewMouseRightButtonDown += OnRightButtonDown;
        PreviewKeyDown += OnPreviewKeyDown;
        SourceInitialized += OnSourceInitialized;
        Closing += OnClosing;
    }

    public bool Contains(ScreenPoint point) =>
        point.X >= _physicalBounds.X &&
        point.X < _physicalBounds.Right &&
        point.Y >= _physicalBounds.Y &&
        point.Y < _physicalBounds.Bottom;

    public void ActivateForInput()
    {
        _ = Activate();
        _ = Focus();
        Keyboard.Focus(this);
    }

    public void UpdateSelection(ScreenRect? physicalSelection)
    {
        if (physicalSelection is null)
        {
            _selectionShade.Selection = null;
            return;
        }

        var intersection = Intersect(_physicalBounds, physicalSelection.Value);
        if (intersection is null)
        {
            _selectionShade.Selection = null;
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        var scaleX = dpi.DpiScaleX <= 0 ? 1 : dpi.DpiScaleX;
        var scaleY = dpi.DpiScaleY <= 0 ? 1 : dpi.DpiScaleY;
        var rectangle = intersection.Value;

        _selectionShade.Selection = new Rect(
            (rectangle.X - _physicalBounds.X) / scaleX,
            (rectangle.Y - _physicalBounds.Y) / scaleY,
            rectangle.Width / scaleX,
            rectangle.Height / scaleY);
    }

    public void CloseSilently()
    {
        if (_isClosing)
        {
            return;
        }

        _isClosing = true;
        _isDragging = false;
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }

        Close();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        _ = SetWindowPos(
            handle,
            HwndTopmost,
            checked((int)_physicalBounds.X),
            checked((int)_physicalBounds.Y),
            checked((int)_physicalBounds.Width),
            checked((int)_physicalBounds.Height),
            SwpShowWindow);
    }

    private void OnLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_isClosing || e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        ActivateForInput();
        _isDragging = true;
        _ = CaptureMouse();
        _selectionStarted(GetPhysicalPointer(e));
        e.Handled = true;
    }

    private void OnMouseMove(object sender, InputMouseEventArgs e)
    {
        if (!_isDragging || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        _selectionChanged(GetPhysicalPointer(e));
        e.Handled = true;
    }

    private void OnLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging || e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        _isDragging = false;
        var point = GetPhysicalPointer(e);
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }

        _selectionCompleted(point);
        e.Handled = true;
    }

    private void OnRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_isClosing)
        {
            return;
        }

        _captureCancelled();
        e.Handled = true;
    }

    private void OnPreviewKeyDown(object sender, InputKeyEventArgs e)
    {
        if (e.Key != Key.Escape || _isClosing)
        {
            return;
        }

        _captureCancelled();
        e.Handled = true;
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_isClosing)
        {
            return;
        }

        // Alt+F4 and system-initiated closes must complete the shared capture
        // session so its frozen bitmaps and hook suppression leases are freed.
        _isClosing = true;
        _isDragging = false;
        _captureCancelled();
    }

    private ScreenPoint GetPhysicalPointer(InputMouseEventArgs eventArgs)
    {
        if (GetCursorPos(out var nativePoint))
        {
            return new ScreenPoint(nativePoint.X, nativePoint.Y);
        }

        var fallback = PointToScreen(eventArgs.GetPosition(this));
        return new ScreenPoint(
            checked((int)Math.Round(fallback.X)),
            checked((int)Math.Round(fallback.Y)));
    }

    private static ScreenRect? Intersect(ScreenRect first, ScreenRect second)
    {
        var left = Math.Max(first.X, second.X);
        var top = Math.Max(first.Y, second.Y);
        var right = Math.Min(first.Right, second.Right);
        var bottom = Math.Min(first.Bottom, second.Bottom);

        return right <= left || bottom <= top
            ? null
            : new ScreenRect(left, top, right - left, bottom - top);
    }

    private sealed class SelectionShade : FrameworkElement
    {
        private readonly MediaBrush _shadeBrush;
        private readonly MediaPen _selectionShadowPen;
        private readonly MediaPen _selectionPen;
        private Rect? _selection;

        public SelectionShade(double dimOpacity)
        {
            var alpha = (byte)Math.Clamp(Math.Round(dimOpacity * 255), 0, 255);
            _shadeBrush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(alpha, 4, 8, 18));
            _shadeBrush.Freeze();

            _selectionShadowPen = new MediaPen(
                new SolidColorBrush(System.Windows.Media.Color.FromArgb(190, 8, 12, 24)),
                4);
            _selectionShadowPen.Freeze();

            _selectionPen = new MediaPen(
                new SolidColorBrush(System.Windows.Media.Color.FromRgb(122, 162, 255)),
                2.25);
            _selectionPen.Freeze();
        }

        public Rect? Selection
        {
            get => _selection;
            set
            {
                _selection = value;
                InvalidateVisual();
            }
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);

            var full = new Rect(0, 0, ActualWidth, ActualHeight);
            var selection = _selection;
            if (selection is null || selection.Value.IsEmpty)
            {
                drawingContext.DrawRectangle(_shadeBrush, null, full);
                return;
            }

            var selected = Rect.Intersect(full, selection.Value);
            if (selected.IsEmpty)
            {
                drawingContext.DrawRectangle(_shadeBrush, null, full);
                return;
            }

            DrawRectangle(drawingContext, 0, 0, full.Width, selected.Top);
            DrawRectangle(drawingContext, 0, selected.Bottom, full.Width, full.Height - selected.Bottom);
            DrawRectangle(drawingContext, 0, selected.Top, selected.Left, selected.Height);
            DrawRectangle(drawingContext, selected.Right, selected.Top, full.Width - selected.Right, selected.Height);
            // A dark outer stroke keeps the blue outline legible over both
            // light and dark content. It is visual feedback only: capture
            // pixels come from the frozen pre-overlay bitmaps.
            drawingContext.DrawRectangle(null, _selectionShadowPen, selected);
            drawingContext.DrawRectangle(null, _selectionPen, selected);
        }

        private void DrawRectangle(
            DrawingContext drawingContext,
            double x,
            double y,
            double width,
            double height)
        {
            if (width > 0 && height > 0)
            {
                drawingContext.DrawRectangle(_shadeBrush, null, new Rect(x, y, width, height));
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativePoint
    {
        public readonly int X;
        public readonly int Y;
    }

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
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);
}
