using System.Windows;
using Microsoft.Web.WebView2.Wpf;

namespace Huaci.App.Services.Translation;

internal sealed class OfflineTranslationHostWindow : Window
{
    public OfflineTranslationHostWindow(WebView2 webView)
    {
        ArgumentNullException.ThrowIfNull(webView);

        Title = "Huaci Offline Engine";
        Width = 1;
        Height = 1;
        MinWidth = 1;
        MinHeight = 1;
        Left = SystemParameters.VirtualScreenLeft - 10_000;
        Top = SystemParameters.VirtualScreenTop - 10_000;
        WindowStartupLocation = WindowStartupLocation.Manual;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        Background = System.Windows.Media.Brushes.Black;
        Content = webView;
    }
}
