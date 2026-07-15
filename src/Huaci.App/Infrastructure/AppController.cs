using System.Windows;
using System.Windows.Threading;
using Huaci.App.Models;
using Huaci.App.Services;
using Huaci.App.Services.Capture;
using Huaci.App.Services.Settings;
using Huaci.App.Services.Translation;
using Huaci.App.Views;

namespace Huaci.App.Infrastructure;

public sealed class AppController : IDisposable
{
    private readonly System.Windows.Application _application;
    private readonly SettingsService _settingsService;
    private readonly CredentialManagerSecretStore _secretStore;
    private readonly BergamotOfflineTranslationProvider _offlineTranslationProvider;
    private readonly OpenAiCompatibleTranslationProvider _onlineTranslationProvider;
    private readonly ITranslationService _translationService;
    private readonly GlobalMouseHook _mouseHook;
    private readonly TextSelectionService _textSelection;
    private readonly ClipboardFallbackService _clipboardFallback;
    private readonly LauncherWindow _mainWindow;
    private readonly SettingsWindow _settingsWindow;
    private readonly ManualTranslationWindow _manualTranslationWindow;
    private readonly TranslationPopupWindow _popup;
    private readonly TrayIconService _tray;
    private readonly SelectionTranslationCoordinator _coordinator;
    private readonly DispatcherTimer _placementSaveTimer;

    private AppSettings _settings;
    private CancellationTokenSource? _manualTranslation;
    private bool _initialized;
    private bool _disposed;

    public AppController(System.Windows.Application application)
    {
        _application = application;
        _settingsService = new SettingsService();
        _secretStore = new CredentialManagerSecretStore();
        _offlineTranslationProvider = new BergamotOfflineTranslationProvider(_application.Dispatcher);
        _onlineTranslationProvider = new OpenAiCompatibleTranslationProvider();
        _translationService = new TranslationService(
            _offlineTranslationProvider,
            _onlineTranslationProvider,
            _secretStore.Read);
        _settings = _settingsService.Load();

        _mainWindow = new LauncherWindow();
        _settingsWindow = new SettingsWindow();
        _manualTranslationWindow = new ManualTranslationWindow();
        _application.MainWindow = _mainWindow;
        _popup = new TranslationPopupWindow();
        var hasApiKey = HasApiKey();
        _mainWindow.LoadSettings(_settings, IsTranslationReady(_settings, hasApiKey));
        _settingsWindow.LoadSettings(_settings, hasApiKey, _offlineTranslationProvider.IsAvailable);
        _settingsWindow.SetOfflineAvailability(
            _offlineTranslationProvider.IsAvailable,
            _offlineTranslationProvider.AvailabilityMessage);

        _mouseHook = new GlobalMouseHook();
        _textSelection = new TextSelectionService();
        _clipboardFallback = new ClipboardFallbackService(new ClipboardFallbackOptions
        {
            Enabled = _settings.ClipboardFallbackEnabled
        });
        _coordinator = new SelectionTranslationCoordinator(
            _mouseHook,
            _textSelection,
            _clipboardFallback,
            _translationService,
            _popup,
            _application.Dispatcher,
            GetSettings);

        _tray = new TrayIconService(_settings.AutoCaptureEnabled);
        _placementSaveTimer = new DispatcherTimer(DispatcherPriority.Background, _application.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(650)
        };
        _placementSaveTimer.Tick += PlacementSaveTimer_OnTick;
        WireEvents();
    }

    public void Initialize()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        var requestedCaptureState = _settings.AutoCaptureEnabled;
        var effectiveCaptureState = ApplyCaptureState(requestedCaptureState);
        if (effectiveCaptureState != requestedCaptureState)
        {
            var rolledBack = CopySettings(GetSettings());
            rolledBack.AutoCaptureEnabled = effectiveCaptureState;
            SetAndSaveSettings(rolledBack);
        }

        PublishAutoCaptureState(effectiveCaptureState);

        var hasApiKey = HasApiKey();
        if (!IsTranslationReady(_settings, hasApiKey))
        {
            var message = _settings.TranslationMode == TranslationRouteMode.OnlineOnly
                ? "请先配置在线翻译 API Key"
                : _offlineTranslationProvider.AvailabilityMessage;
            _mainWindow.SetStatus(message, true);
            _settingsWindow.SetStatus(message, true);
        }
        else if (_settings.TranslationMode != TranslationRouteMode.OnlineOnly
                 && _offlineTranslationProvider.IsAvailable)
        {
            _mainWindow.SetStatus("离线翻译已就绪");
            _settingsWindow.SetStatus("内置英译中可断网使用");
        }

        ShowMainWindow();
        if (_settings.TranslationMode != TranslationRouteMode.OnlineOnly
            && _offlineTranslationProvider.IsAvailable)
        {
            _ = WarmUpOfflineTranslationAsync();
        }
    }

    public void ActivateFromExternalLaunch()
    {
        if (!_disposed)
        {
            ShowMainWindow();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _placementSaveTimer.Stop();
        SaveWindowPlacement();
        _disposed = true;

        _manualTranslation?.Cancel();
        _manualTranslation?.Dispose();
        _manualTranslation = null;

        _coordinator.Dispose();
        _mouseHook.Stop();
        _mouseHook.Dispose();
        _textSelection.Dispose();
        _clipboardFallback.Dispose();
        _popup.CloseForExit();
        _manualTranslationWindow.CloseForExit();
        _settingsWindow.CloseForExit();
        _mainWindow.CloseForExit();
        _tray.Dispose();
        _offlineTranslationProvider.Dispose();
        _onlineTranslationProvider.Dispose();
        _settingsService.Dispose();
    }

    private AppSettings GetSettings() => Volatile.Read(ref _settings);

    private void WireEvents()
    {
        _tray.OpenRequested += ShowMainWindow;
        _tray.ToggleWindowRequested += ToggleMainWindow;
        _tray.SettingsRequested += ShowSettings;
        _tray.AutoCaptureChanged += SetAutoCapture;
        _tray.ExitRequested += RequestExit;

        _mainWindow.HideRequested += MainWindow_OnHidden;
        _mainWindow.AutoCaptureChanged += SetAutoCapture;
        _mainWindow.OpenManualTranslationRequested += ShowManualTranslation;
        _mainWindow.OpenSettingsRequested += ShowSettings;
        _settingsWindow.SaveSettingsRequested += SaveSettings;
        _manualTranslationWindow.TranslateRequested += text => _ = TranslateManuallyAsync(text);
        _mainWindow.LocationChanged += (_, _) => SchedulePlacementSave();
        _mainWindow.SizeChanged += (_, _) => SchedulePlacementSave();
    }

    private void ToggleMainWindow()
    {
        if (_mainWindow.IsVisible && _mainWindow.WindowState != WindowState.Minimized)
        {
            _mainWindow.HideToTray();
            return;
        }

        ShowMainWindow();
    }

    private void ShowMainWindow()
    {
        EnsureMainWindowVisible();
        _mainWindow.ShowAndActivate();
    }

    private void MainWindow_OnHidden()
    {
        _placementSaveTimer.Stop();
        SaveWindowPlacement();
    }

    private void ShowSettings()
    {
        _settingsWindow.ShowAndActivate(SettingsSection.Capture);
    }

    private void ShowServiceSettings()
    {
        _settingsWindow.ShowAndActivate(SettingsSection.Service);
    }

    private void ShowManualTranslation()
    {
        _manualTranslationWindow.ShowAndActivate();
    }

    private void SetAutoCapture(bool enabled)
    {
        try
        {
            var updated = CopySettings(GetSettings());
            updated.AutoCaptureEnabled = enabled;
            SetAndSaveSettings(updated);

            var effective = ApplyCaptureState(enabled);
            if (effective != enabled)
            {
                var rolledBack = CopySettings(GetSettings());
                rolledBack.AutoCaptureEnabled = effective;
                SetAndSaveSettings(rolledBack);
            }

            PublishAutoCaptureState(effective);
            if (effective != enabled)
            {
                const string message = "无法启动全局取词，请重启程序后再试";
                _mainWindow.SetStatus(message, true);
                _settingsWindow.SetStatus(message, true);
                _tray.ShowInfo(message);
                return;
            }

            var status = effective ? "自动划词已开启" : "自动划词已暂停";
            _mainWindow.SetStatus(status);
            _settingsWindow.SetStatus(status);
        }
        catch (Exception)
        {
            var canonical = GetSettings().AutoCaptureEnabled;
            PublishAutoCaptureState(canonical);
            _mainWindow.SetStatus("自动划词状态保存失败", true);
            _settingsWindow.SetStatus("自动划词状态保存失败", true);
        }
    }

    private bool ApplyCaptureState(bool enabled)
    {
        _clipboardFallback.Enabled = GetSettings().ClipboardFallbackEnabled;
        if (!enabled)
        {
            _coordinator.CancelPending(hidePopup: true);
            _mouseHook.Stop();
            return false;
        }

        try
        {
            _mouseHook.Start();
            return true;
        }
        catch (Exception)
        {
            _mouseHook.Stop();
            return false;
        }
    }

    private void PublishAutoCaptureState(bool enabled)
    {
        _tray.SetAutoCapture(enabled);
        _mainWindow.SetAutoCaptureState(enabled);
        _settingsWindow.SetAutoCaptureState(enabled);
    }

    private void SaveSettings(SettingsWindowInput input)
    {
        try
        {
            if (input.TranslationMode != TranslationRouteMode.OfflineOnly)
            {
                _ = OpenAiCompatibleTranslationProvider.ResolveEndpoint(input.ApiBaseUrl);
                if (string.IsNullOrWhiteSpace(input.Model))
                {
                    _settingsWindow.SetStatus("模型名称不能为空", true);
                    return;
                }
            }

            if (input.ApiKey is not null)
            {
                _secretStore.Write(input.ApiKey);
            }

            var current = GetSettings();
            var updated = CopySettings(current);
            updated.AutoCaptureEnabled = input.AutoCaptureEnabled;
            updated.ClipboardFallbackEnabled = input.ClipboardFallbackEnabled;
            updated.CaptureDelayMs = input.CaptureDelayMs;
            updated.PopupDurationSeconds = input.PopupDurationSeconds;
            updated.TranslationMode = input.TranslationMode;
            updated.ApiBaseUrl = input.ApiBaseUrl;
            updated.Model = input.Model;
            CaptureWindowPlacement(updated);
            SetAndSaveSettings(updated);

            var effectiveCaptureState = ApplyCaptureState(GetSettings().AutoCaptureEnabled);
            if (effectiveCaptureState != GetSettings().AutoCaptureEnabled)
            {
                var rolledBack = CopySettings(GetSettings());
                rolledBack.AutoCaptureEnabled = effectiveCaptureState;
                SetAndSaveSettings(rolledBack);
            }

            PublishAutoCaptureState(effectiveCaptureState);
            var canonical = GetSettings();
            var hasApiKey = HasApiKey();
            _settingsWindow.LoadSettings(canonical, hasApiKey, _offlineTranslationProvider.IsAvailable);
            _settingsWindow.SetOfflineAvailability(
                _offlineTranslationProvider.IsAvailable,
                _offlineTranslationProvider.AvailabilityMessage);
            _mainWindow.SetServiceConfigured(IsTranslationReady(canonical, hasApiKey));

            if (effectiveCaptureState != input.AutoCaptureEnabled)
            {
                const string message = "设置已保存，但全局取词启动失败";
                _settingsWindow.SetStatus(message, true);
                _mainWindow.SetStatus(message, true);
                return;
            }

            _settingsWindow.SetStatus("设置已保存");
            _mainWindow.SetStatus("设置已保存");

            if (canonical.TranslationMode != TranslationRouteMode.OnlineOnly
                && _offlineTranslationProvider.IsAvailable)
            {
                _ = WarmUpOfflineTranslationAsync();
            }
        }
        catch (TranslationProviderException exception)
        {
            _settingsWindow.SetStatus(exception.Message, true);
        }
        catch (Exception)
        {
            _settingsWindow.SetStatus("设置保存失败，请稍后重试", true);
        }
    }

    private async Task TranslateManuallyAsync(string value)
    {
        var source = TextHeuristics.Normalize(value);
        if (source.Length == 0)
        {
            _manualTranslationWindow.SetStatus("请输入需要翻译的内容", true);
            return;
        }

        _manualTranslation?.Cancel();
        var requestCancellation = new CancellationTokenSource();
        _manualTranslation = requestCancellation;
        _manualTranslationWindow.SetBusy(true);

        try
        {
            var settings = GetSettings();
            _manualTranslationWindow.SetStatus("正在翻译…");
            var result = await _translationService.TranslateAsync(
                new TranslationRequest(source, settings.SourceLanguage, settings.TargetLanguage),
                settings,
                requestCancellation.Token);

            if (!IsCurrentManualRequest(requestCancellation))
            {
                return;
            }

            _manualTranslationWindow.SetTranslation(source, result.TranslatedText);
            _manualTranslationWindow.SetStatus(
                result.Origin == TranslationOrigin.Offline
                    ? "翻译完成 · 离线"
                    : result.UsedFallback
                        ? "翻译完成 · 在线回退"
                        : "翻译完成 · 在线");
        }
        catch (OperationCanceledException)
        {
        }
        catch (TranslationProviderException exception)
        {
            if (IsCurrentManualRequest(requestCancellation))
            {
                _manualTranslationWindow.SetStatus(exception.Message, true);
            }
        }
        catch (Exception)
        {
            if (IsCurrentManualRequest(requestCancellation))
            {
                _manualTranslationWindow.SetStatus("翻译失败，请检查翻译设置", true);
            }
        }
        finally
        {
            if (ReferenceEquals(_manualTranslation, requestCancellation))
            {
                _manualTranslation = null;
                _manualTranslationWindow.SetBusy(false);
            }

            requestCancellation.Dispose();
        }
    }

    private bool IsCurrentManualRequest(CancellationTokenSource request) =>
        ReferenceEquals(_manualTranslation, request) && !request.IsCancellationRequested;

    private async Task WarmUpOfflineTranslationAsync()
    {
        try
        {
            await _offlineTranslationProvider.WarmUpAsync();
            if (_disposed)
            {
                return;
            }

            _settingsWindow.SetOfflineAvailability(true, "内置英语 → 简体中文模型已加载，可断网使用");
            _mainWindow.SetServiceConfigured(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (TranslationProviderException exception)
        {
            if (_disposed)
            {
                return;
            }

            _settingsWindow.SetOfflineAvailability(false, exception.Message);
            var settings = GetSettings();
            if (!IsTranslationReady(settings, HasApiKey())
                || settings.TranslationMode == TranslationRouteMode.OfflineOnly)
            {
                _mainWindow.SetServiceConfigured(false);
                _mainWindow.SetStatus(exception.Message, true);
            }
        }
        catch (Exception)
        {
            if (!_disposed)
            {
                const string message = "内置离线模型加载失败，请重启 Huaci 后重试";
                _settingsWindow.SetOfflineAvailability(false, message);
                _mainWindow.SetStatus(message, true);
            }
        }
    }

    private bool IsTranslationReady(AppSettings settings, bool hasApiKey) => settings.TranslationMode switch
    {
        TranslationRouteMode.OfflineOnly => _offlineTranslationProvider.IsAvailable,
        TranslationRouteMode.OnlineOnly => hasApiKey,
        _ => _offlineTranslationProvider.IsAvailable || hasApiKey
    };

    private bool HasApiKey()
    {
        try
        {
            return !string.IsNullOrWhiteSpace(_secretStore.Read());
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void SchedulePlacementSave()
    {
        if (!_initialized || !_mainWindow.IsVisible)
        {
            return;
        }

        _placementSaveTimer.Stop();
        _placementSaveTimer.Start();
    }

    private void PlacementSaveTimer_OnTick(object? sender, EventArgs e)
    {
        _placementSaveTimer.Stop();
        SaveWindowPlacement();
    }

    private void SaveWindowPlacement()
    {
        if (!_initialized || _disposed)
        {
            return;
        }

        try
        {
            var updated = CopySettings(GetSettings());
            CaptureWindowPlacement(updated);
            SetAndSaveSettings(updated);
        }
        catch (Exception)
        {
            // Window placement is best-effort and must never interrupt translation.
        }
    }

    private void CaptureWindowPlacement(AppSettings target)
    {
        if (_mainWindow.WindowState != WindowState.Normal)
        {
            return;
        }

        target.MainWindowLeft = _mainWindow.Left;
        target.MainWindowTop = _mainWindow.Top;
        target.MainWindowWidth = _mainWindow.ActualWidth > 0 ? _mainWindow.ActualWidth : _mainWindow.Width;
        target.MainWindowHeight = _mainWindow.ActualHeight > 0 ? _mainWindow.ActualHeight : _mainWindow.Height;
    }

    private void EnsureMainWindowVisible()
    {
        if (double.IsNaN(_mainWindow.Left) || double.IsNaN(_mainWindow.Top))
        {
            return;
        }

        var centerX = _mainWindow.Left + (_mainWindow.Width / 2);
        var centerY = _mainWindow.Top + (_mainWindow.Height / 2);
        var visible = centerX >= SystemParameters.VirtualScreenLeft
            && centerX <= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth
            && centerY >= SystemParameters.VirtualScreenTop
            && centerY <= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;

        if (!visible)
        {
            var work = SystemParameters.WorkArea;
            _mainWindow.Left = work.Left + Math.Max(0, (work.Width - _mainWindow.Width) / 2);
            _mainWindow.Top = work.Top + Math.Max(0, (work.Height - _mainWindow.Height) / 2);
        }
    }

    private void SetAndSaveSettings(AppSettings settings)
    {
        var normalized = SettingsService.ValidateAndNormalize(settings);
        _settingsService.Save(normalized);
        Volatile.Write(ref _settings, normalized);
    }

    private void RequestExit()
    {
        if (_disposed)
        {
            return;
        }

        Dispose();
        _application.Shutdown();
    }

    private static AppSettings CopySettings(AppSettings source) => new()
    {
        AutoCaptureEnabled = source.AutoCaptureEnabled,
        ClipboardFallbackEnabled = source.ClipboardFallbackEnabled,
        CaptureDelayMs = source.CaptureDelayMs,
        PopupDurationSeconds = source.PopupDurationSeconds,
        ApiBaseUrl = source.ApiBaseUrl,
        Model = source.Model,
        SourceLanguage = source.SourceLanguage,
        TargetLanguage = source.TargetLanguage,
        TranslationMode = source.TranslationMode,
        MainWindowLeft = source.MainWindowLeft,
        MainWindowTop = source.MainWindowTop,
        MainWindowWidth = source.MainWindowWidth,
        MainWindowHeight = source.MainWindowHeight
    };
}
