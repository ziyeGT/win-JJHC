using System.Text.Json;
using Huaci.App.Models;

namespace Huaci.App.Services.Settings;

public sealed class SettingsService : IDisposable
{
    private const string DefaultApiBaseUrl = "https://api.deepseek.com";
    private const string DefaultModel = "deepseek-chat";
    private const string DefaultSourceLanguage = "auto";
    private const string DefaultTargetLanguage = "zh-CN";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = true
    };

    private readonly SemaphoreSlim _ioGate = new(1, 1);
    private bool _disposed;

    public SettingsService(string? settingsPath = null)
    {
        SettingsPath = string.IsNullOrWhiteSpace(settingsPath)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Huaci",
                "settings.json")
            : Path.GetFullPath(settingsPath);
    }

    public string SettingsPath { get; }

    public AppSettings Load()
    {
        ThrowIfDisposed();
        _ioGate.Wait();

        try
        {
            return LoadCore();
        }
        finally
        {
            _ioGate.Release();
        }
    }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (!File.Exists(SettingsPath))
            {
                return new AppSettings();
            }

            try
            {
                await using FileStream stream = new(
                    SettingsPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 4096,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);

                AppSettings? settings = await JsonSerializer.DeserializeAsync<AppSettings>(
                    stream,
                    SerializerOptions,
                    cancellationToken).ConfigureAwait(false);

                return ValidateAndNormalize(settings);
            }
            catch (JsonException)
            {
                return new AppSettings();
            }
            catch (IOException)
            {
                return new AppSettings();
            }
            catch (UnauthorizedAccessException)
            {
                return new AppSettings();
            }
        }
        finally
        {
            _ioGate.Release();
        }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ThrowIfDisposed();
        _ioGate.Wait();

        try
        {
            AppSettings normalized = ValidateAndNormalize(settings);
            string? directory = Path.GetDirectoryName(SettingsPath);
            if (string.IsNullOrEmpty(directory))
            {
                throw new InvalidOperationException("设置文件路径缺少有效目录。");
            }

            Directory.CreateDirectory(directory);
            string temporaryPath = CreateTemporaryPath(directory);

            try
            {
                string json = JsonSerializer.Serialize(normalized, SerializerOptions);
                File.WriteAllText(temporaryPath, json);
                ReplaceAtomically(temporaryPath);
            }
            finally
            {
                TryDelete(temporaryPath);
            }
        }
        finally
        {
            _ioGate.Release();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ThrowIfDisposed();
        await _ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            AppSettings normalized = ValidateAndNormalize(settings);
            string? directory = Path.GetDirectoryName(SettingsPath);
            if (string.IsNullOrEmpty(directory))
            {
                throw new InvalidOperationException("设置文件路径缺少有效目录。");
            }

            Directory.CreateDirectory(directory);
            string temporaryPath = CreateTemporaryPath(directory);

            try
            {
                await using (FileStream stream = new(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 4096,
                    FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await JsonSerializer.SerializeAsync(
                        stream,
                        normalized,
                        SerializerOptions,
                        cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                ReplaceAtomically(temporaryPath);
            }
            finally
            {
                TryDelete(temporaryPath);
            }
        }
        finally
        {
            _ioGate.Release();
        }
    }

    public static AppSettings ValidateAndNormalize(AppSettings? settings)
    {
        settings ??= new AppSettings();

        return new AppSettings
        {
            AutoCaptureEnabled = settings.AutoCaptureEnabled,
            ClipboardFallbackEnabled = settings.ClipboardFallbackEnabled,
            ScreenshotTranslationEnabled = settings.ScreenshotTranslationEnabled,
            StartWithWindowsEnabled = settings.StartWithWindowsEnabled,
            CaptureDelayMs = Math.Clamp(settings.CaptureDelayMs, 50, 100),
            PopupDurationSeconds = Math.Clamp(settings.PopupDurationSeconds, 2, 60),
            ApiBaseUrl = NormalizeApiBaseUrl(settings.ApiBaseUrl),
            Model = NormalizeText(settings.Model, DefaultModel, 200),
            SourceLanguage = NormalizeText(settings.SourceLanguage, DefaultSourceLanguage, 32),
            TargetLanguage = NormalizeText(settings.TargetLanguage, DefaultTargetLanguage, 32),
            TranslationMode = Enum.IsDefined(typeof(TranslationRouteMode), settings.TranslationMode)
                ? settings.TranslationMode
                : TranslationRouteMode.OfflineFirst,
            MainWindowLeft = NormalizeCoordinate(settings.MainWindowLeft),
            MainWindowTop = NormalizeCoordinate(settings.MainWindowTop),
            MainWindowWidth = NormalizeDimension(settings.MainWindowWidth, 320, 1_600),
            MainWindowHeight = NormalizeDimension(settings.MainWindowHeight, 360, 1_200)
        };
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _ioGate.Dispose();
    }

    private AppSettings LoadCore()
    {
        if (!File.Exists(SettingsPath))
        {
            return new AppSettings();
        }

        try
        {
            string json = File.ReadAllText(SettingsPath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new AppSettings();
            }

            return ValidateAndNormalize(JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions));
        }
        catch (JsonException)
        {
            return new AppSettings();
        }
        catch (IOException)
        {
            return new AppSettings();
        }
        catch (UnauthorizedAccessException)
        {
            return new AppSettings();
        }
    }

    private void ReplaceAtomically(string temporaryPath)
    {
        if (!File.Exists(SettingsPath))
        {
            File.Move(temporaryPath, SettingsPath);
            return;
        }

        try
        {
            File.Replace(temporaryPath, SettingsPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
        catch (PlatformNotSupportedException)
        {
            File.Move(temporaryPath, SettingsPath, overwrite: true);
        }
        catch (IOException)
        {
            File.Move(temporaryPath, SettingsPath, overwrite: true);
        }
    }

    private static string NormalizeApiBaseUrl(string? value)
    {
        string candidate = NormalizeText(value, DefaultApiBaseUrl, 2_048).TrimEnd('/');
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return DefaultApiBaseUrl;
        }

        return candidate;
    }

    private static string NormalizeText(string? value, string fallback, int maximumLength)
    {
        string normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
        {
            return fallback;
        }

        return normalized.Length <= maximumLength
            ? normalized
            : normalized[..maximumLength];
    }

    private static double? NormalizeCoordinate(double? value)
    {
        if (value is null || !double.IsFinite(value.Value) || Math.Abs(value.Value) > 100_000)
        {
            return null;
        }

        return value;
    }

    private static double? NormalizeDimension(double? value, double minimum, double maximum)
    {
        if (value is null || !double.IsFinite(value.Value))
        {
            return null;
        }

        return Math.Clamp(value.Value, minimum, maximum);
    }

    private static string CreateTemporaryPath(string directory) =>
        Path.Combine(directory, $"settings.{Guid.NewGuid():N}.tmp");

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
