using System.Drawing;
using System.Runtime.InteropServices;

namespace Huaci.App.Services;

public sealed class TrayIconService : IDisposable
{
    private readonly System.Windows.Forms.NotifyIcon _notifyIcon;
    private readonly System.Windows.Forms.ToolStripMenuItem _autoCaptureItem;
    private readonly Icon _icon;
    private bool _disposed;
    private bool _updatingAutoCapture;

    public TrayIconService(bool autoCaptureEnabled)
    {
        _icon = CreateIcon();
        _autoCaptureItem = new System.Windows.Forms.ToolStripMenuItem("自动划词")
        {
            Checked = autoCaptureEnabled,
            CheckOnClick = true
        };

        var openItem = new System.Windows.Forms.ToolStripMenuItem("打开主窗口")
        {
            ShortcutKeyDisplayString = GlobalHotKeyService.ToggleMainWindowDisplayText
        };
        var screenshotItem = new System.Windows.Forms.ToolStripMenuItem("截图翻译");
        var quickNotebookItem = new System.Windows.Forms.ToolStripMenuItem("快速笔记");
        var settingsItem = new System.Windows.Forms.ToolStripMenuItem("设置");
        var exitItem = new System.Windows.Forms.ToolStripMenuItem("退出");

        openItem.Click += (_, _) => OpenRequested?.Invoke();
        screenshotItem.Click += (_, _) => ScreenshotTranslationRequested?.Invoke();
        quickNotebookItem.Click += (_, _) => QuickNotebookRequested?.Invoke();
        settingsItem.Click += (_, _) => SettingsRequested?.Invoke();
        exitItem.Click += (_, _) => ExitRequested?.Invoke();
        _autoCaptureItem.CheckedChanged += (_, _) =>
        {
            if (!_updatingAutoCapture)
            {
                AutoCaptureChanged?.Invoke(_autoCaptureItem.Checked);
            }
        };

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add(openItem);
        menu.Items.Add(screenshotItem);
        menu.Items.Add(quickNotebookItem);
        menu.Items.Add(settingsItem);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(_autoCaptureItem);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(exitItem);

        _notifyIcon = new System.Windows.Forms.NotifyIcon
        {
            Text = "划词翻译",
            Icon = _icon,
            ContextMenuStrip = menu,
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => ToggleWindowRequested?.Invoke();
    }

    public event Action? OpenRequested;
    public event Action? ToggleWindowRequested;
    public event Action? ScreenshotTranslationRequested;
    public event Action? QuickNotebookRequested;
    public event Action? SettingsRequested;
    public event Action<bool>? AutoCaptureChanged;
    public event Action? ExitRequested;

    public void SetAutoCapture(bool enabled)
    {
        if (_autoCaptureItem.Checked != enabled)
        {
            _updatingAutoCapture = true;
            try
            {
                _autoCaptureItem.Checked = enabled;
            }
            finally
            {
                _updatingAutoCapture = false;
            }
        }
    }

    public void ShowInfo(string message)
    {
        _notifyIcon.BalloonTipTitle = "划词翻译";
        _notifyIcon.BalloonTipText = message;
        _notifyIcon.BalloonTipIcon = System.Windows.Forms.ToolTipIcon.Info;
        _notifyIcon.ShowBalloonTip(2500);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.ContextMenuStrip?.Dispose();
        _notifyIcon.Dispose();
        _icon.Dispose();
    }

    private static Icon CreateIcon()
    {
        try
        {
            var iconResource = System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/Assets/Huaci.ico", UriKind.Absolute));
            if (iconResource is not null)
            {
                using (iconResource.Stream)
                using (var embeddedIcon = new Icon(iconResource.Stream))
                {
                    return (Icon)embeddedIcon.Clone();
                }
            }

            string? executablePath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(executablePath) && File.Exists(executablePath))
            {
                using Icon? applicationIcon = Icon.ExtractAssociatedIcon(executablePath);
                if (applicationIcon is not null)
                {
                    return (Icon)applicationIcon.Clone();
                }
            }
        }
        catch (ArgumentException)
        {
            // Fall back to the runtime-drawn icon when the PE icon is unavailable.
        }
        catch (ExternalException)
        {
            // Fall back to the runtime-drawn icon when resource loading or shell extraction fails.
        }
        catch (IOException)
        {
            // Fall back to the runtime-drawn icon when the resource stream cannot be read.
        }

        return CreateFallbackIcon();
    }

    private static Icon CreateFallbackIcon()
    {
        using var bitmap = new Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);
        using var background = new SolidBrush(Color.FromArgb(64, 93, 222));
        graphics.FillRoundedRectangle(background, new Rectangle(1, 1, 30, 30), 7);
        using var font = new Font("Microsoft YaHei UI", 15, FontStyle.Bold, GraphicsUnit.Pixel);
        using var foreground = new SolidBrush(Color.White);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        graphics.DrawString("译", font, foreground, new RectangleF(0, 0, 32, 31), format);

        var handle = bitmap.GetHicon();
        try
        {
            using var icon = Icon.FromHandle(handle);
            return (Icon)icon.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint handle);
}

internal static class GraphicsExtensions
{
    public static void FillRoundedRectangle(this Graphics graphics, Brush brush, Rectangle bounds, int radius)
    {
        using var path = new System.Drawing.Drawing2D.GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        graphics.FillPath(brush, path);
    }
}
