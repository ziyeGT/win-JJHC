using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;
using Huaci.App.Models;

namespace Huaci.App.Services.Notebook;

public sealed class QuickNotebookService : IQuickNotebookService
{
    private const string AlarmFileName = "alarms.json";
    private const int PreviewCharacterLimit = 140;
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);
    private static readonly JsonSerializerOptions AlarmJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
    private readonly SemaphoreSlim _fileGate = new(1, 1);

    public QuickNotebookService(string? preferredDirectory = null)
    {
        string portableDirectory = preferredDirectory
            ?? Path.Combine(AppContext.BaseDirectory, "save");
        string fallbackDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Huaci",
            "save");

        StorageDirectory = ResolveWritableDirectory(portableDirectory, fallbackDirectory);
        AlarmStoragePath = Path.Combine(StorageDirectory, AlarmFileName);
    }

    public event EventHandler? AlarmsChanged;

    public string StorageDirectory { get; }

    public string AlarmStoragePath { get; }

    public async Task<IReadOnlyList<QuickNotebookEntry>> GetHistoryAsync(
        CancellationToken cancellationToken = default)
    {
        await _fileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(
                () => ReadHistory(cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _fileGate.Release();
        }
    }

    public async Task<QuickNotebookEntry> SaveTextAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("笔记内容不能为空。", nameof(text));
        }

        string normalizedText = text.Replace("\0", string.Empty, StringComparison.Ordinal);
        string destination = CreateDestinationPath("txt");
        string temporaryPath = destination + ".tmp";

        await _fileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(StorageDirectory);
            try
            {
                await File.WriteAllTextAsync(
                    temporaryPath,
                    normalizedText,
                    Utf8WithoutBom,
                    cancellationToken).ConfigureAwait(false);
                File.Move(temporaryPath, destination);
            }
            finally
            {
                TryDeleteTemporaryFile(temporaryPath);
            }

            return CreateEntry(destination, normalizedText);
        }
        finally
        {
            _fileGate.Release();
        }
    }

    public async Task<QuickNotebookEntry> SaveImageAsync(
        BitmapSource image,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        cancellationToken.ThrowIfCancellationRequested();

        byte[] pngBytes = EncodePng(image);
        string destination = CreateDestinationPath("png");
        string temporaryPath = destination + ".tmp";

        await _fileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(StorageDirectory);
            try
            {
                await File.WriteAllBytesAsync(
                    temporaryPath,
                    pngBytes,
                    cancellationToken).ConfigureAwait(false);
                File.Move(temporaryPath, destination);
            }
            finally
            {
                TryDeleteTemporaryFile(temporaryPath);
            }

            return CreateEntry(destination, "PNG 图片");
        }
        finally
        {
            _fileGate.Release();
        }
    }

    public async Task<string> ReadTextAsync(
        QuickNotebookEntry entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        EnsureEntryBelongsToStorage(entry, QuickNotebookContentType.Text);
        return await File.ReadAllTextAsync(
            entry.FilePath,
            Utf8WithoutBom,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<byte[]> ReadImageAsync(
        QuickNotebookEntry entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        EnsureEntryBelongsToStorage(entry, QuickNotebookContentType.Image);
        return await File.ReadAllBytesAsync(entry.FilePath, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(
        QuickNotebookEntry entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        EnsureEntryBelongsToStorage(entry, entry.ContentType);

        await _fileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(entry.FilePath))
            {
                File.Delete(entry.FilePath);
            }
        }
        finally
        {
            _fileGate.Release();
        }
    }

    public async Task<IReadOnlyList<QuickNotebookAlarm>> GetPendingAlarmsAsync(
        CancellationToken cancellationToken = default)
    {
        await _fileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IReadOnlyList<QuickNotebookAlarm> alarms =
                await ReadAlarmsCoreAsync(cancellationToken).ConfigureAwait(false);
            return alarms
                .OrderBy(alarm => alarm.DueAt)
                .ThenBy(alarm => alarm.CreatedAt)
                .ToArray();
        }
        finally
        {
            _fileGate.Release();
        }
    }

    public async Task<QuickNotebookAlarm> ScheduleAlarmAsync(
        string message,
        DateTimeOffset dueAt,
        CancellationToken cancellationToken = default)
    {
        string normalizedMessage = NormalizeAlarmMessage(message);
        DateTimeOffset now = DateTimeOffset.Now;
        if (dueAt <= now)
        {
            throw new ArgumentOutOfRangeException(
                nameof(dueAt),
                dueAt,
                "闹铃时间必须晚于当前时间。");
        }

        var alarm = new QuickNotebookAlarm(
            Guid.NewGuid(),
            normalizedMessage,
            dueAt.ToUniversalTime(),
            now.ToUniversalTime());

        await _fileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var alarms = (await ReadAlarmsCoreAsync(cancellationToken).ConfigureAwait(false))
                .ToList();
            alarms.Add(alarm);
            await WriteAlarmsCoreAsync(alarms, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _fileGate.Release();
        }

        AlarmsChanged?.Invoke(this, EventArgs.Empty);
        return alarm;
    }

    public async Task<bool> DeleteAlarmAsync(
        Guid alarmId,
        CancellationToken cancellationToken = default)
    {
        if (alarmId == Guid.Empty)
        {
            return false;
        }

        bool removed;
        await _fileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var alarms = (await ReadAlarmsCoreAsync(cancellationToken).ConfigureAwait(false))
                .ToList();
            removed = alarms.RemoveAll(alarm => alarm.Id == alarmId) > 0;
            if (removed)
            {
                await WriteAlarmsCoreAsync(alarms, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _fileGate.Release();
        }

        if (removed)
        {
            AlarmsChanged?.Invoke(this, EventArgs.Empty);
        }

        return removed;
    }

    public void OpenStorageFolder()
    {
        Directory.CreateDirectory(StorageDirectory);
        Process.Start(new ProcessStartInfo
        {
            FileName = StorageDirectory,
            UseShellExecute = true
        });
    }

    private IReadOnlyList<QuickNotebookEntry> ReadHistory(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(StorageDirectory);
        var entries = new List<QuickNotebookEntry>();

        foreach (string filePath in Directory.EnumerateFiles(StorageDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string extension = Path.GetExtension(filePath);
            if (!extension.Equals(".txt", StringComparison.OrdinalIgnoreCase)
                && !extension.Equals(".png", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                bool isText = extension.Equals(".txt", StringComparison.OrdinalIgnoreCase);
                string preview = isText
                    ? ReadTextPreview(filePath, cancellationToken)
                    : "PNG 图片";
                entries.Add(CreateEntry(filePath, preview));
            }
            catch (IOException)
            {
                // A file can be replaced while history is refreshing; skip it this time.
            }
            catch (UnauthorizedAccessException)
            {
                // Keep the rest of the history available if one file is inaccessible.
            }
        }

        return entries
            .OrderByDescending(entry => entry.CreatedAt)
            .ToArray();
    }

    private static string ResolveWritableDirectory(string preferredDirectory, string fallbackDirectory)
    {
        Exception? preferredFailure = null;
        try
        {
            return EnsureWritableDirectory(preferredDirectory);
        }
        catch (Exception exception) when (IsStorageException(exception))
        {
            preferredFailure = exception;
        }

        try
        {
            return EnsureWritableDirectory(fallbackDirectory);
        }
        catch (Exception fallbackFailure) when (IsStorageException(fallbackFailure))
        {
            throw new InvalidOperationException(
                "无法创建快速笔记保存目录。",
                new AggregateException(preferredFailure!, fallbackFailure));
        }
    }

    private static string EnsureWritableDirectory(string directory)
    {
        string fullPath = Path.GetFullPath(directory);
        Directory.CreateDirectory(fullPath);

        string probePath = Path.Combine(fullPath, $".huaci-write-{Guid.NewGuid():N}.tmp");
        using (new FileStream(
                   probePath,
                   FileMode.CreateNew,
                   FileAccess.Write,
                   FileShare.None,
                   bufferSize: 1,
                   FileOptions.DeleteOnClose))
        {
        }

        return fullPath;
    }

    private static bool IsStorageException(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or System.Security.SecurityException
            or ArgumentException;

    private string CreateDestinationPath(string extension)
    {
        string timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        string suffix = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..6];
        return Path.Combine(StorageDirectory, $"{timestamp}-{suffix}.{extension}");
    }

    private static QuickNotebookEntry CreateEntry(string filePath, string preview)
    {
        var info = new FileInfo(filePath);
        bool isText = info.Extension.Equals(".txt", StringComparison.OrdinalIgnoreCase);
        return new QuickNotebookEntry(
            info.FullName,
            info.Name,
            isText ? QuickNotebookContentType.Text : QuickNotebookContentType.Image,
            new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
            info.Length,
            NormalizePreview(preview));
    }

    private void EnsureEntryBelongsToStorage(
        QuickNotebookEntry entry,
        QuickNotebookContentType expectedContentType)
    {
        if (entry.ContentType != expectedContentType)
        {
            throw new InvalidOperationException("笔记类型不匹配。");
        }

        string storageRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(StorageDirectory));
        string candidate = Path.GetFullPath(entry.FilePath);
        string relativePath = Path.GetRelativePath(storageRoot, candidate);

        if (Path.IsPathRooted(relativePath)
            || relativePath.Equals("..", StringComparison.Ordinal)
            || relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("笔记文件不在保存目录内。");
        }

        string expectedExtension = expectedContentType == QuickNotebookContentType.Text ? ".txt" : ".png";
        if (!Path.GetExtension(candidate).Equals(expectedExtension, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("笔记文件格式不匹配。");
        }
    }

    private static string ReadTextPreview(string filePath, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(
            stream,
            Utf8WithoutBom,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 512,
            leaveOpen: false);

        var buffer = new char[PreviewCharacterLimit + 1];
        int read = reader.ReadBlock(buffer, 0, buffer.Length);
        cancellationToken.ThrowIfCancellationRequested();
        return new string(buffer, 0, read);
    }

    private static string NormalizePreview(string value)
    {
        string normalized = string.Join(
            " ",
            value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Length <= PreviewCharacterLimit)
        {
            return normalized;
        }

        return normalized[..PreviewCharacterLimit] + "…";
    }

    private async Task<IReadOnlyList<QuickNotebookAlarm>> ReadAlarmsCoreAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(AlarmStoragePath))
        {
            return [];
        }

        try
        {
            await using FileStream stream = new(
                AlarmStoragePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
            AlarmDocument? document = await JsonSerializer.DeserializeAsync<AlarmDocument>(
                stream,
                AlarmJsonOptions,
                cancellationToken).ConfigureAwait(false);
            return document?.Alarms
                .Where(alarm => alarm.Id != Guid.Empty
                                && !string.IsNullOrWhiteSpace(alarm.Message)
                                && alarm.DueAt != default)
                .Select(alarm => alarm with
                {
                    Message = alarm.Message.Replace("\0", string.Empty, StringComparison.Ordinal).Trim()
                })
                .ToArray()
                ?? [];
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("闹铃文件 alarms.json 格式已损坏。", exception);
        }
    }

    private async Task WriteAlarmsCoreAsync(
        IReadOnlyCollection<QuickNotebookAlarm> alarms,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(StorageDirectory);
        string temporaryPath = AlarmStoragePath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            var document = new AlarmDocument
            {
                Alarms = alarms
                    .OrderBy(alarm => alarm.DueAt)
                    .ThenBy(alarm => alarm.CreatedAt)
                    .ToList()
            };
            await using (FileStream stream = new(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 4096,
                             FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    document,
                    AlarmJsonOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, AlarmStoragePath, overwrite: true);
        }
        finally
        {
            TryDeleteTemporaryFile(temporaryPath);
        }
    }

    private static string NormalizeAlarmMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            throw new ArgumentException("提醒内容不能为空。", nameof(message));
        }

        string normalized = message
            .Replace("\0", string.Empty, StringComparison.Ordinal)
            .Trim();
        if (normalized.Length == 0)
        {
            throw new ArgumentException("提醒内容不能为空。", nameof(message));
        }

        return normalized;
    }

    private static byte[] EncodePng(BitmapSource image)
    {
        BitmapSource source = image;
        if (!source.IsFrozen)
        {
            source = source.Clone();
            source.Freeze();
        }

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static void TryDeleteTemporaryFile(string path)
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

    private sealed class AlarmDocument
    {
        public int Version { get; init; } = 1;

        public List<QuickNotebookAlarm> Alarms { get; init; } = [];
    }
}
