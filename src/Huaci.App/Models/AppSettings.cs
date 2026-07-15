namespace Huaci.App.Models;

/// <summary>
/// Non-sensitive application settings. API credentials are deliberately stored separately.
/// </summary>
public sealed class AppSettings
{
    public bool AutoCaptureEnabled { get; set; } = true;

    public bool ClipboardFallbackEnabled { get; set; }

    public int CaptureDelayMs { get; set; } = 60;

    public int PopupDurationSeconds { get; set; } = 5;

    public string ApiBaseUrl { get; set; } = "https://api.deepseek.com";

    public string Model { get; set; } = "deepseek-chat";

    public string SourceLanguage { get; set; } = "auto";

    public string TargetLanguage { get; set; } = "zh-CN";

    public TranslationRouteMode TranslationMode { get; set; } = TranslationRouteMode.OfflineFirst;

    public double? MainWindowLeft { get; set; }

    public double? MainWindowTop { get; set; }

    public double? MainWindowWidth { get; set; }

    public double? MainWindowHeight { get; set; }
}
