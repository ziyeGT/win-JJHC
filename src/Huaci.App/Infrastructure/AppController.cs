using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Huaci.App.Models;
using Huaci.App.Services;
using Huaci.App.Services.Capture;
using Huaci.App.Services.Notebook;
using Huaci.App.Services.Ocr;
using Huaci.App.Services.Settings;
using Huaci.App.Services.Translation;
using Huaci.App.Views;

namespace Huaci.App.Infrastructure;

public sealed class AppController : IDisposable
{
    private readonly System.Windows.Application _application;
    private readonly SettingsService _settingsService;
    private readonly WindowsStartupService _startupService;
    private readonly CredentialManagerSecretStore _secretStore;
    private readonly BergamotOfflineTranslationProvider _offlineTranslationProvider;
    private readonly OpenAiCompatibleTranslationProvider _onlineTranslationProvider;
    private readonly ITranslationService _translationService;
    private readonly GlobalMouseHook _mouseHook;
    private readonly IScreenRegionCaptureService _screenRegionCapture;
    private readonly IOcrService _ocrService;
    private readonly TextSelectionService _textSelection;
    private readonly ClipboardFallbackService _clipboardFallback;
    private readonly LauncherWindow _mainWindow;
    private readonly SettingsWindow _settingsWindow;
    private readonly ManualTranslationWindow _manualTranslationWindow;
    private readonly QuickNotebookService _quickNotebookService;
    private readonly QuickNotebookWindow _quickNotebookWindow;
    private readonly AlarmBannerWindow _alarmBannerWindow;
    private readonly QuickNotebookAlarmCoordinator _alarmCoordinator;
    private readonly TranslationPopupWindow _popup;
    private readonly ScreenshotTranslationOverlayWindow _screenshotOverlay;
    private readonly TrayIconService _tray;
    private readonly GlobalHotKeyService _globalHotKey;
    private readonly SelectionTranslationCoordinator _coordinator;
    private readonly DispatcherTimer _placementSaveTimer;

    private AppSettings _settings;
    private CancellationTokenSource? _manualTranslation;
    private readonly object _screenshotRequestGate = new();
    private CancellationTokenSource? _screenshotTranslation;
    private bool _initialized;
    private bool _disposed;

    public AppController(System.Windows.Application application)
    {
        _application = application;
        _settingsService = new SettingsService();
        _startupService = new WindowsStartupService();
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
        _quickNotebookService = new QuickNotebookService();
        _quickNotebookWindow = new QuickNotebookWindow(_quickNotebookService);
        _alarmBannerWindow = new AlarmBannerWindow();
        _alarmCoordinator = new QuickNotebookAlarmCoordinator(
            _quickNotebookService,
            _alarmBannerWindow,
            _application.Dispatcher);
        _application.MainWindow = _mainWindow;
        _popup = new TranslationPopupWindow();
        _screenshotOverlay = new ScreenshotTranslationOverlayWindow();
        _globalHotKey = new GlobalHotKeyService(_mainWindow);
        var hasApiKey = HasApiKey();
        _mainWindow.LoadSettings(_settings, IsTranslationReady(_settings, hasApiKey));
        _settingsWindow.LoadSettings(_settings, hasApiKey, _offlineTranslationProvider.IsAvailable);
        _settingsWindow.SetOfflineAvailability(
            _offlineTranslationProvider.IsAvailable,
            _offlineTranslationProvider.AvailabilityMessage);

        _mouseHook = new GlobalMouseHook();
        _screenRegionCapture = new ScreenRegionCaptureService(
            _mouseHook,
            dispatcher: _application.Dispatcher,
            captureSurfaceReady: _mainWindow.KeepAboveWithoutActivation);
        _ocrService = new RapidOcrService();
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
        var hotKeyRegistered = _globalHotKey.TryRegister();
        if (!_settings.AutoCaptureEnabled)
        {
            var enabledAtStartup = CopySettings(GetSettings());
            enabledAtStartup.AutoCaptureEnabled = true;
            try
            {
                SetAndSaveSettings(enabledAtStartup);
            }
            catch
            {
                // Starting with automatic selection enabled is the runtime
                // default even when the settings file is temporarily locked.
                Volatile.Write(
                    ref _settings,
                    SettingsService.ValidateAndNormalize(enabledAtStartup));
            }
        }

        var requestedCaptureState = _settings.AutoCaptureEnabled;
        var requestedScreenshotState = _settings.ScreenshotTranslationEnabled;
        var effectiveCaptureState = ApplyCaptureState(requestedCaptureState);
        if (effectiveCaptureState.AutoCaptureEnabled != requestedCaptureState
            || effectiveCaptureState.ScreenshotTranslationEnabled != requestedScreenshotState)
        {
            var rolledBack = CopySettings(GetSettings());
            rolledBack.AutoCaptureEnabled = effectiveCaptureState.AutoCaptureEnabled;
            rolledBack.ScreenshotTranslationEnabled = effectiveCaptureState.ScreenshotTranslationEnabled;
            SetAndSaveSettings(rolledBack);
            _settingsWindow.SetScreenshotTranslationState(
                effectiveCaptureState.ScreenshotTranslationEnabled);
        }

        PublishAutoCaptureState(effectiveCaptureState.AutoCaptureEnabled);

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
            _settingsWindow.SetStatus("内置英中互译可断网使用");
        }

        if (effectiveCaptureState.HookStartFailed)
        {
            const string message = "无法启动全局鼠标监听；自动划词和截图手势已关闭";
            _mainWindow.SetStatus(message, true);
            _settingsWindow.SetStatus(message, true);
        }

        var startupRegistrationFailed = false;
        try
        {
            _startupService.SetEnabled(_settings.StartWithWindowsEnabled);
        }
        catch (StartupRegistrationException)
        {
            startupRegistrationFailed = true;
            if (_settings.StartWithWindowsEnabled)
            {
                try
                {
                    var rolledBack = CopySettings(GetSettings());
                    rolledBack.StartWithWindowsEnabled = false;
                    SetAndSaveSettings(rolledBack);
                }
                catch
                {
                    // Startup registration must never prevent the app itself
                    // from starting, even if the settings file is also locked.
                }

                _settingsWindow.SetStartWithWindowsState(false);
            }
        }

        if (startupRegistrationFailed)
        {
            _settingsWindow.SetStatus("开机自动启动注册失败，已保持关闭", true);
        }

        ShowMainWindow();
        _alarmCoordinator.Start();
        if (!hotKeyRegistered)
        {
            string errorSuffix = _globalHotKey.LastRegistrationError == 0
                ? string.Empty
                : $"（Windows 错误 {_globalHotKey.LastRegistrationError}）";
            _mainWindow.SetStatus(
                $"Ctrl+F1 暂时不可用{errorSuffix}，正在自动重试；也可通过托盘打开。",
                true);
        }
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

        CancelScreenshotTranslation(hidePopup: false);

        _coordinator.Dispose();
        _alarmCoordinator.Dispose();
        _globalHotKey.ToggleMainWindowRequested -= ToggleMainWindow;
        _globalHotKey.RegistrationRecovered -= GlobalHotKey_OnRegistrationRecovered;
        _globalHotKey.Dispose();
        _mouseHook.ScreenshotRequested -= MouseHook_OnScreenshotRequested;
        _screenshotOverlay.Dismissed -= ScreenshotOverlay_OnDismissed;
        _screenRegionCapture.Dispose();
        _ocrService.Dispose();
        _mouseHook.Stop();
        _mouseHook.Dispose();
        _textSelection.Dispose();
        _clipboardFallback.Dispose();
        _popup.CloseForExit();
        _screenshotOverlay.CloseForExit();
        _alarmBannerWindow.CloseForExit();
        _manualTranslationWindow.CloseForExit();
        _quickNotebookWindow.CloseForExit();
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
        _tray.ScreenshotTranslationRequested += StartScreenshotTranslation;
        _tray.QuickNotebookRequested += ShowQuickNotebook;
        _tray.SettingsRequested += ShowSettings;
        _tray.AutoCaptureChanged += SetAutoCapture;
        _tray.ExitRequested += RequestExit;
        _globalHotKey.ToggleMainWindowRequested += ToggleMainWindow;
        _globalHotKey.RegistrationRecovered += GlobalHotKey_OnRegistrationRecovered;

        _mainWindow.HideRequested += MainWindow_OnHidden;
        _mainWindow.AutoCaptureChanged += SetAutoCapture;
        _mainWindow.OpenManualTranslationRequested += ShowManualTranslation;
        _mainWindow.OpenScreenshotTranslationRequested += StartScreenshotTranslation;
        _mainWindow.OpenQuickNotebookRequested += ShowQuickNotebook;
        _mainWindow.OpenSettingsRequested += ShowSettings;
        _settingsWindow.SaveSettingsRequested += SaveSettings;
        _manualTranslationWindow.TranslateRequested += text => _ = TranslateManuallyAsync(text);
        _mainWindow.LocationChanged += (_, _) => SchedulePlacementSave();
        _mainWindow.SizeChanged += (_, _) => SchedulePlacementSave();
        _mouseHook.ScreenshotRequested += MouseHook_OnScreenshotRequested;
        _screenshotOverlay.Dismissed += ScreenshotOverlay_OnDismissed;
    }

    private void ToggleMainWindow()
    {
        if (_mainWindow.ShouldHideFromGlobalToggle)
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

    private void GlobalHotKey_OnRegistrationRecovered()
    {
        const string message = "Ctrl+F1 快捷键已恢复";
        _mainWindow.SetStatus(message);
        _tray.ShowInfo(message);
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

    private void ShowQuickNotebook()
    {
        _quickNotebookWindow.ShowAndActivate();
    }

    private void StartScreenshotTranslation() => BeginScreenshotTranslation(
        preferredScreenPoint: null,
        directDragGestureId: null);

    private void MouseHook_OnScreenshotRequested(object? sender, ScreenshotRequestedEventArgs e)
    {
        if (_disposed || !GetSettings().ScreenshotTranslationEnabled)
        {
            return;
        }

        BeginScreenshotTranslation(e.TriggerPoint, e.GestureId);
    }

    private void BeginScreenshotTranslation(
        ScreenPoint? preferredScreenPoint,
        long? directDragGestureId)
    {
        if (_disposed)
        {
            return;
        }

        var request = new CancellationTokenSource();
        CancellationTokenSource? superseded;
        lock (_screenshotRequestGate)
        {
            superseded = _screenshotTranslation;
            _screenshotTranslation = request;
        }

        TryCancel(superseded);
        _screenRegionCapture.Cancel();
        _coordinator.CancelPending(hidePopup: true);
        if (!_application.Dispatcher.HasShutdownStarted)
        {
            _application.Dispatcher.BeginInvoke(_screenshotOverlay.HideOverlay);
        }

        _ = CaptureAndTranslateScreenshotAsync(
            preferredScreenPoint,
            directDragGestureId,
            request);
    }

    private async Task CaptureAndTranslateScreenshotAsync(
        ScreenPoint? preferredScreenPoint,
        long? directDragGestureId,
        CancellationTokenSource request)
    {
        IDisposable? screenshotGestureSuppression = null;
        ScreenRect? overlayBounds = null;

        try
        {
            screenshotGestureSuppression = _mouseHook.SuppressScreenshotRequests();
            await HideTransientWindowsForCaptureAsync(request.Token).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromMilliseconds(70), request.Token).ConfigureAwait(false);

            ScreenRegionCaptureResult capture = await CaptureRegionWithBusyRetryAsync(
                    preferredScreenPoint,
                    directDragGestureId,
                    request.Token)
                .ConfigureAwait(false);

            screenshotGestureSuppression.Dispose();
            screenshotGestureSuppression = null;

            if (capture.Status == ScreenRegionCaptureStatus.Cancelled)
            {
                return;
            }

            if (!capture.IsSuccess || capture.PhysicalBounds is not { } bounds || capture.PngBytes is null)
            {
                string message = capture.Status switch
                {
                    ScreenRegionCaptureStatus.Busy => "已有截图任务正在进行。",
                    ScreenRegionCaptureStatus.EmptySelection => "请拖动框选需要翻译的区域。",
                    _ => "截图失败，请稍后重试。"
                };
                await ShowScreenshotErrorIfCurrentAsync(
                    "截图翻译",
                    message,
                    physicalBounds: null,
                    request).ConfigureAwait(false);
                return;
            }

            overlayBounds = bounds;
            await OnUiAsync(
                () =>
                {
                    _screenshotOverlay.ShowLoading(
                        string.Empty,
                        bounds,
                        "正在识别…",
                        capture.PngBytes);
                    _mainWindow.KeepAboveWithoutActivation();
                },
                request.Token).ConfigureAwait(false);

            OcrRecognitionResult recognition = await _ocrService
                .RecognizeAsync(capture.PngBytes, request.Token)
                .ConfigureAwait(false);

            if (recognition.Status == OcrRecognitionStatus.Cancelled)
            {
                return;
            }

            if (!recognition.IsSuccess)
            {
                string message = recognition.Status switch
                {
                    OcrRecognitionStatus.NoText => "未识别到可翻译的文字，请框选更清晰的区域。",
                    OcrRecognitionStatus.EngineUnavailable => _ocrService.AvailabilityMessage,
                    OcrRecognitionStatus.EmptyImage or OcrRecognitionStatus.InvalidImage => "截图图像无法识别，请重新框选。",
                    _ => "离线 OCR 识别失败，请稍后重试。"
                };
                await ShowScreenshotErrorIfCurrentAsync(
                    "截图翻译",
                    message,
                    bounds,
                    request).ConfigureAwait(false);
                return;
            }

            string source = TextHeuristics.Normalize(
                OcrTextLayout.ReconstructParagraphs(recognition.Blocks, recognition.Text));
            if (source.Length == 0)
            {
                await ShowScreenshotErrorIfCurrentAsync(
                    "截图翻译",
                    "未识别到可翻译的文字。",
                    bounds,
                    request).ConfigureAwait(false);
                return;
            }

            var settings = GetSettings();
            var translationRequest = CreateScreenshotTranslationRequest(source);
            await OnUiAsync(
                () =>
                {
                    _screenshotOverlay.ShowLoading(
                        source,
                        bounds,
                        "正在翻译…");
                    _mainWindow.KeepAboveWithoutActivation();
                },
                request.Token).ConfigureAwait(false);

            TranslationResult result = await _translationService
                .TranslateAsync(translationRequest, settings, request.Token)
                .ConfigureAwait(false);

            if (!IsCurrentScreenshotRequest(request))
            {
                return;
            }

            await OnUiAsync(
                () =>
                {
                    if (IsCurrentScreenshotRequest(request))
                    {
                        _screenshotOverlay.ShowResult(
                            source,
                            result.TranslatedText,
                            bounds,
                            result.Origin,
                            result.UsedFallback,
                            recognition.Blocks);
                        _mainWindow.KeepAboveWithoutActivation();
                    }
                },
                request.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Esc, toast dismissal, shutdown, or a newer screenshot superseded this request.
        }
        catch (TranslationProviderException exception)
        {
            await ShowScreenshotErrorIfCurrentAsync(
                "截图翻译",
                exception.Message,
                overlayBounds,
                request).ConfigureAwait(false);
        }
        catch (Exception)
        {
            await ShowScreenshotErrorIfCurrentAsync(
                "截图翻译",
                "截图翻译失败，请稍后重试。",
                overlayBounds,
                request).ConfigureAwait(false);
        }
        finally
        {
            screenshotGestureSuppression?.Dispose();
            lock (_screenshotRequestGate)
            {
                if (ReferenceEquals(_screenshotTranslation, request))
                {
                    _screenshotTranslation = null;
                }
            }

            request.Dispose();
        }
    }

    private async Task<ScreenRegionCaptureResult> CaptureRegionWithBusyRetryAsync(
        ScreenPoint? preferredScreenPoint,
        long? directDragGestureId,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var capture = await _screenRegionCapture
                .CaptureAsync(preferredScreenPoint, directDragGestureId, cancellationToken)
                .ConfigureAwait(false);
            if (capture.Status != ScreenRegionCaptureStatus.Busy)
            {
                return capture;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(40), cancellationToken).ConfigureAwait(false);
        }

        return ScreenRegionCaptureResult.WithoutImage(ScreenRegionCaptureStatus.Busy);
    }

    private async Task HideTransientWindowsForCaptureAsync(CancellationToken cancellationToken)
    {
        await OnUiAsync(
            () =>
            {
                // Starting a screenshot is not a user request to minimize the
                // launcher. Keep its visibility, size, position, and window
                // state unchanged; only explicit minimize/close actions may
                // hide it.
                _settingsWindow.Hide();
                _manualTranslationWindow.Hide();
                _quickNotebookWindow.Hide();
                _popup.HidePopup();
                _screenshotOverlay.HideOverlay();
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task ShowScreenshotErrorIfCurrentAsync(
        string source,
        string message,
        ScreenRect? physicalBounds,
        CancellationTokenSource request)
    {
        if (!IsCurrentScreenshotRequest(request))
        {
            return;
        }

        try
        {
            await OnUiAsync(
                () =>
                {
                    if (IsCurrentScreenshotRequest(request))
                    {
                        if (physicalBounds is { } bounds)
                        {
                            _screenshotOverlay.ShowError(source, message, bounds);
                            _mainWindow.KeepAboveWithoutActivation();
                        }
                        else
                        {
                            _tray.ShowInfo(message);
                        }
                    }
                },
                request.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // The dispatcher can be shutting down while an error is being surfaced.
        }
    }

    private bool IsCurrentScreenshotRequest(CancellationTokenSource request)
    {
        lock (_screenshotRequestGate)
        {
            return !_disposed
                && ReferenceEquals(_screenshotTranslation, request)
                && !request.IsCancellationRequested;
        }
    }

    private void ScreenshotOverlay_OnDismissed() => CancelScreenshotTranslation(hidePopup: false);

    private void CancelScreenshotTranslation(bool hidePopup)
    {
        CancellationTokenSource? request;
        lock (_screenshotRequestGate)
        {
            request = _screenshotTranslation;
            _screenshotTranslation = null;
        }

        TryCancel(request);
        _screenRegionCapture.Cancel();
        if (hidePopup && !_application.Dispatcher.HasShutdownStarted)
        {
            _application.Dispatcher.BeginInvoke(_screenshotOverlay.HideOverlay);
        }
    }

    private static void TryCancel(CancellationTokenSource? request)
    {
        try
        {
            request?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Completion can race a newer screenshot or application shutdown.
        }
    }

    private Task OnUiAsync(Action action, CancellationToken cancellationToken) =>
        _application.Dispatcher.InvokeAsync(action, DispatcherPriority.Normal, cancellationToken).Task;

    private static TranslationRequest CreateScreenshotTranslationRequest(string source)
    {
        var chineseLetters = 0;
        var otherLetters = 0;
        foreach (Rune rune in source.EnumerateRunes())
        {
            UnicodeCategory category = Rune.GetUnicodeCategory(rune);
            if (category is not (UnicodeCategory.UppercaseLetter
                or UnicodeCategory.LowercaseLetter
                or UnicodeCategory.TitlecaseLetter
                or UnicodeCategory.ModifierLetter
                or UnicodeCategory.OtherLetter))
            {
                continue;
            }

            if (IsChinese(rune.Value))
            {
                chineseLetters++;
            }
            else
            {
                otherLetters++;
            }
        }

        bool translateToEnglish = chineseLetters > 0 && chineseLetters * 2 >= otherLetters;
        return translateToEnglish
            ? new TranslationRequest(source, "zh-CN", "en")
            : new TranslationRequest(source, "en", "zh-CN");
    }

    private static bool IsChinese(int value) => value is >= 0x3400 and <= 0x4DBF
        or >= 0x4E00 and <= 0x9FFF
        or >= 0xF900 and <= 0xFAFF
        or >= 0x20000 and <= 0x2EBEF
        or >= 0x2F800 and <= 0x2FA1F
        or >= 0x30000 and <= 0x323AF;

    private void SetAutoCapture(bool enabled)
    {
        try
        {
            var updated = CopySettings(GetSettings());
            updated.AutoCaptureEnabled = enabled;
            SetAndSaveSettings(updated);

            var effective = ApplyCaptureState(enabled);
            if (effective.AutoCaptureEnabled != enabled
                || effective.ScreenshotTranslationEnabled != updated.ScreenshotTranslationEnabled)
            {
                var rolledBack = CopySettings(GetSettings());
                rolledBack.AutoCaptureEnabled = effective.AutoCaptureEnabled;
                rolledBack.ScreenshotTranslationEnabled = effective.ScreenshotTranslationEnabled;
                SetAndSaveSettings(rolledBack);
                _settingsWindow.SetScreenshotTranslationState(effective.ScreenshotTranslationEnabled);
            }

            PublishAutoCaptureState(effective.AutoCaptureEnabled);
            if (effective.HookStartFailed)
            {
                const string message = "无法启动全局鼠标监听；自动划词和截图手势已关闭";
                _mainWindow.SetStatus(message, true);
                _settingsWindow.SetStatus(message, true);
                _tray.ShowInfo(message);
                return;
            }

            var status = effective.AutoCaptureEnabled ? "自动划词已开启" : "自动划词已暂停";
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

    private CaptureActivationResult ApplyCaptureState(bool enabled)
    {
        var settings = GetSettings();
        _clipboardFallback.Enabled = settings.ClipboardFallbackEnabled;
        _mouseHook.ScreenshotGestureEnabled = settings.ScreenshotTranslationEnabled;

        if (!settings.ScreenshotTranslationEnabled)
        {
            CancelScreenshotTranslation(hidePopup: true);
        }

        if (!enabled)
        {
            _coordinator.CancelPending(hidePopup: true);
        }

        if (!enabled && !settings.ScreenshotTranslationEnabled)
        {
            _mouseHook.Stop();
            return new CaptureActivationResult(false, false, HookStartFailed: false);
        }

        try
        {
            _mouseHook.Start();
            return new CaptureActivationResult(
                enabled,
                settings.ScreenshotTranslationEnabled,
                HookStartFailed: false);
        }
        catch (Exception)
        {
            _mouseHook.Stop();
            _mouseHook.ScreenshotGestureEnabled = false;
            return new CaptureActivationResult(false, false, HookStartFailed: true);
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
            updated.ClipboardFallbackEnabled = input.ClipboardFallbackEnabled;
            updated.ScreenshotTranslationEnabled = input.ScreenshotTranslationEnabled;
            updated.StartWithWindowsEnabled = input.StartWithWindowsEnabled;
            updated.CaptureDelayMs = input.CaptureDelayMs;
            updated.PopupDurationSeconds = input.PopupDurationSeconds;
            updated.TranslationMode = input.TranslationMode;
            updated.ApiBaseUrl = input.ApiBaseUrl;
            updated.Model = input.Model;
            CaptureWindowPlacement(updated);
            SetAndSaveSettings(updated);

            string? startupError = null;
            try
            {
                _startupService.SetEnabled(updated.StartWithWindowsEnabled);
            }
            catch (StartupRegistrationException)
            {
                var rolledBack = CopySettings(GetSettings());
                rolledBack.StartWithWindowsEnabled = current.StartWithWindowsEnabled;
                SetAndSaveSettings(rolledBack);
                _settingsWindow.SetStartWithWindowsState(current.StartWithWindowsEnabled);
                startupError = "其他设置已保存，但开机自动启动设置失败";
            }

            var requestedCaptureSettings = GetSettings();
            var effectiveCaptureState = ApplyCaptureState(requestedCaptureSettings.AutoCaptureEnabled);
            if (effectiveCaptureState.AutoCaptureEnabled != requestedCaptureSettings.AutoCaptureEnabled
                || effectiveCaptureState.ScreenshotTranslationEnabled
                    != requestedCaptureSettings.ScreenshotTranslationEnabled)
            {
                var rolledBack = CopySettings(GetSettings());
                rolledBack.AutoCaptureEnabled = effectiveCaptureState.AutoCaptureEnabled;
                rolledBack.ScreenshotTranslationEnabled = effectiveCaptureState.ScreenshotTranslationEnabled;
                SetAndSaveSettings(rolledBack);
            }

            PublishAutoCaptureState(effectiveCaptureState.AutoCaptureEnabled);
            var canonical = GetSettings();
            var hasApiKey = HasApiKey();
            _settingsWindow.LoadSettings(canonical, hasApiKey, _offlineTranslationProvider.IsAvailable);
            _settingsWindow.SetOfflineAvailability(
                _offlineTranslationProvider.IsAvailable,
                _offlineTranslationProvider.AvailabilityMessage);
            _mainWindow.SetServiceConfigured(IsTranslationReady(canonical, hasApiKey));

            if (effectiveCaptureState.HookStartFailed)
            {
                const string message = "设置已保存，但全局鼠标监听启动失败；自动划词和截图手势已关闭";
                _settingsWindow.SetStatus(message, true);
                _mainWindow.SetStatus(message, true);
                return;
            }

            if (startupError is not null)
            {
                _settingsWindow.SetStatus(startupError, true);
                _mainWindow.SetStatus(startupError, true);
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
            _manualTranslationWindow.SetStatus("正在翻译…");
            var settings = GetSettings();
            var result = await _translationService.TranslateAsync(
                _manualTranslationWindow.CreateTranslationRequest(source),
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

            _settingsWindow.SetOfflineAvailability(true, "内置英语 ↔ 简体中文模型已就绪，可断网使用");
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

    private readonly record struct CaptureActivationResult(
        bool AutoCaptureEnabled,
        bool ScreenshotTranslationEnabled,
        bool HookStartFailed);

    private static AppSettings CopySettings(AppSettings source) => new()
    {
        AutoCaptureEnabled = source.AutoCaptureEnabled,
        ClipboardFallbackEnabled = source.ClipboardFallbackEnabled,
        ScreenshotTranslationEnabled = source.ScreenshotTranslationEnabled,
        StartWithWindowsEnabled = source.StartWithWindowsEnabled,
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
