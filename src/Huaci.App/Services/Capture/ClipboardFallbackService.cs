using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Huaci.App.Services.Capture;

/// <summary>
/// Opt-in Ctrl+C fallback hosted on its own STA thread. The previous clipboard
/// is restored before this service completes, and only while the clipboard
/// sequence still matches the value produced by the copy operation.
/// </summary>
public sealed class ClipboardFallbackService : IClipboardFallbackService
{
    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;
    private const ushort VkControl = 0x11;
    private const ushort VkShift = 0x10;
    private const ushort VkMenu = 0x12;
    private const ushort VkLWin = 0x5B;
    private const ushort VkRWin = 0x5C;
    private const ushort VkC = 0x43;
    private static readonly UIntPtr InjectedInputMarker = new(0x48554349); // "HUCI"

    private readonly object _enqueueGate = new();
    private readonly ClipboardFallbackOptions _options;
    private readonly BlockingCollection<CaptureRequest> _requests = new(
        new ConcurrentQueue<CaptureRequest>(),
        boundedCapacity: 1);
    private readonly Thread _workerThread;
    private int _enabled;
    private int _isDisposed;

    public ClipboardFallbackService(ClipboardFallbackOptions? options = null)
    {
        _options = options ?? new ClipboardFallbackOptions();
        ValidateOptions(_options);
        _enabled = _options.Enabled ? 1 : 0;

        _workerThread = new Thread(WorkerMain)
        {
            IsBackground = true,
            Name = "Huaci clipboard fallback",
        };
        _workerThread.SetApartmentState(ApartmentState.STA);
        _workerThread.Start();
    }

    public bool Enabled
    {
        get => Volatile.Read(ref _enabled) != 0;
        set => Volatile.Write(ref _enabled, value ? 1 : 0);
    }

    public Task<SelectionCaptureResult> CaptureAsync(CancellationToken cancellationToken = default)
    {
        if (!Enabled)
        {
            return Task.FromResult(SelectionCaptureResult.WithoutText(
                SelectionCaptureSource.Clipboard,
                SelectionCaptureStatus.Disabled));
        }

        if (Volatile.Read(ref _isDisposed) != 0)
        {
            return Task.FromResult(SelectionCaptureResult.WithoutText(
                SelectionCaptureSource.Clipboard,
                SelectionCaptureStatus.Disposed));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult(SelectionCaptureResult.WithoutText(
                SelectionCaptureSource.Clipboard,
                SelectionCaptureStatus.Cancelled));
        }

        var request = new CaptureRequest(cancellationToken);

        lock (_enqueueGate)
        {
            if (Volatile.Read(ref _isDisposed) != 0 || _requests.IsAddingCompleted)
            {
                request.Complete(NoText(SelectionCaptureStatus.Disposed));
                return request.Task;
            }

            if (!_requests.TryAdd(request))
            {
                if (_requests.TryTake(out var superseded))
                {
                    superseded.Complete(NoText(SelectionCaptureStatus.Superseded));
                }

                if (!_requests.TryAdd(request))
                {
                    request.Complete(NoText(SelectionCaptureStatus.Superseded));
                }
            }
        }

        return request.Task;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
        {
            return;
        }

        lock (_enqueueGate)
        {
            _requests.CompleteAdding();
        }

        if (_workerThread.ManagedThreadId != Environment.CurrentManagedThreadId)
        {
            _ = _workerThread.Join(TimeSpan.FromSeconds(2));
        }

        GC.SuppressFinalize(this);
    }

    private void WorkerMain()
    {
        try
        {
            foreach (var request in _requests.GetConsumingEnumerable())
            {
                if (Volatile.Read(ref _isDisposed) != 0)
                {
                    request.Complete(NoText(SelectionCaptureStatus.Disposed));
                }
                else if (!Enabled)
                {
                    request.Complete(NoText(SelectionCaptureStatus.Disabled));
                }
                else if (request.CancellationToken.IsCancellationRequested)
                {
                    request.Complete(NoText(SelectionCaptureStatus.Cancelled));
                }
                else
                {
                    try
                    {
                        request.Complete(CaptureCore(request.CancellationToken));
                    }
                    catch (Exception exception)
                    {
                        request.Complete(NoText(
                            SelectionCaptureStatus.Failed,
                            $"{exception.GetType().Name}: {exception.Message}"));
                    }
                }
            }
        }
        finally
        {
            while (_requests.TryTake(out var pending))
            {
                pending.Complete(NoText(SelectionCaptureStatus.Disposed));
            }

            _requests.Dispose();
        }
    }

    private SelectionCaptureResult CaptureCore(CancellationToken cancellationToken)
    {
        var foregroundWindow = GetForegroundWindow();
        if (foregroundWindow == nint.Zero)
        {
            return NoText(SelectionCaptureStatus.NoElement);
        }

        _ = GetWindowThreadProcessId(foregroundWindow, out var foregroundProcessId);
        if (foregroundProcessId == unchecked((uint)Environment.ProcessId))
        {
            return SelectionCaptureResult.WithoutText(
                SelectionCaptureSource.Clipboard,
                SelectionCaptureStatus.OwnProcess,
                Environment.ProcessId);
        }

        if (IsKeyDown(VkShift) || IsKeyDown(VkMenu) || IsKeyDown(VkLWin) || IsKeyDown(VkRWin))
        {
            return NoText(
                SelectionCaptureStatus.Failed,
                "Ctrl+C was not injected while Shift, Alt, or Windows was held.");
        }

        var sequenceBeforeSnapshot = GetClipboardSequenceNumber();
        if (sequenceBeforeSnapshot == 0)
        {
            return NoText(
                SelectionCaptureStatus.ClipboardUnavailable,
                "Windows did not provide a usable clipboard sequence number.");
        }

        var snapshotResult = TryCreateSnapshot(cancellationToken);
        if (!snapshotResult.Succeeded || snapshotResult.Snapshot is null)
        {
            return NoText(
                cancellationToken.IsCancellationRequested
                    ? SelectionCaptureStatus.Cancelled
                    : SelectionCaptureStatus.ClipboardUnavailable,
                snapshotResult.Diagnostic);
        }

        // Materializing delayed formats can advance the sequence. Re-acquire
        // once so the snapshot always corresponds to the final baseline; the
        // same check also catches another app changing the clipboard midway.
        var copyBaselineSequence = GetClipboardSequenceNumber();
        if (copyBaselineSequence == 0)
        {
            return NoText(SelectionCaptureStatus.ClipboardUnavailable);
        }

        if (copyBaselineSequence != sequenceBeforeSnapshot)
        {
            var secondSnapshotBaseline = copyBaselineSequence;
            snapshotResult = TryCreateSnapshot(cancellationToken);
            if (!snapshotResult.Succeeded || snapshotResult.Snapshot is null)
            {
                return NoText(
                    cancellationToken.IsCancellationRequested
                        ? SelectionCaptureStatus.Cancelled
                        : SelectionCaptureStatus.ClipboardUnavailable,
                    snapshotResult.Diagnostic);
            }

            copyBaselineSequence = GetClipboardSequenceNumber();
            if (copyBaselineSequence == 0 || copyBaselineSequence != secondSnapshotBaseline)
            {
                return NoText(
                    SelectionCaptureStatus.ClipboardUnavailable,
                    "Clipboard changed while its snapshot was being materialized.");
            }
        }

        if (GetForegroundWindow() != foregroundWindow)
        {
            return NoText(SelectionCaptureStatus.ForegroundChanged);
        }

        var clipboardSnapshot = snapshotResult.Snapshot!;

        var restoreGuardSequence = copyBaselineSequence;
        var clipboardWasChanged = false;
        var copyWasInjected = false;

        try
        {
            copyWasInjected = true;
            if (!SendCopyShortcut())
            {
                var sequenceAfterFailedInput = GetClipboardSequenceNumber();
                if (sequenceAfterFailedInput != 0 && sequenceAfterFailedInput != copyBaselineSequence)
                {
                    clipboardWasChanged = true;
                    restoreGuardSequence = sequenceAfterFailedInput;
                }

                return NoText(
                    SelectionCaptureStatus.Failed,
                    "SendInput could not deliver Ctrl+C to the foreground application.");
            }

            var deadline = DateTime.UtcNow + _options.CopyTimeout;
            var stableSince = DateTime.UtcNow;
            var lastObservedSequence = copyBaselineSequence;
            string? selectedText = null;
            var selectedTextIsStable = false;

            while (DateTime.UtcNow <= deadline)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return NoText(SelectionCaptureStatus.Cancelled);
                }

                // Read even before observing a sequence change. Some delayed
                // renderers advance the sequence only when the format is read.
                var sequenceBeforeRead = GetClipboardSequenceNumber();
                var readSucceeded = TryReadClipboardText(out var candidateText);
                var sequenceAfterRead = GetClipboardSequenceNumber();

                if (sequenceBeforeRead == 0 || sequenceAfterRead == 0)
                {
                    if (cancellationToken.WaitHandle.WaitOne(_options.PollInterval))
                    {
                        return NoText(SelectionCaptureStatus.Cancelled);
                    }

                    continue;
                }

                if (sequenceAfterRead != copyBaselineSequence)
                {
                    clipboardWasChanged = true;
                    restoreGuardSequence = sequenceAfterRead;

                    if (sequenceAfterRead != lastObservedSequence)
                    {
                        lastObservedSequence = sequenceAfterRead;
                        stableSince = DateTime.UtcNow;
                        selectedText = null;
                        selectedTextIsStable = false;
                    }

                    // Requiring a stable sequence across the read prevents an
                    // old clipboard value from being mistaken for the copy.
                    if (sequenceBeforeRead == sequenceAfterRead &&
                        readSucceeded &&
                        !string.IsNullOrWhiteSpace(candidateText))
                    {
                        selectedText = candidateText;
                        if (DateTime.UtcNow - stableSince >= _options.ClipboardSettleDelay)
                        {
                            selectedTextIsStable = true;
                            break;
                        }
                    }
                }

                if (cancellationToken.WaitHandle.WaitOne(_options.PollInterval))
                {
                    return NoText(SelectionCaptureStatus.Cancelled);
                }
            }

            if (!selectedTextIsStable || string.IsNullOrWhiteSpace(selectedText))
            {
                return NoText(
                    clipboardWasChanged
                        ? SelectionCaptureStatus.EmptyText
                        : SelectionCaptureStatus.ClipboardUnchanged);
            }

            if (selectedText.Length > _options.MaxTextLength)
            {
                selectedText = selectedText[.._options.MaxTextLength];
            }

            return new SelectionCaptureResult(
                SelectionCaptureSource.Clipboard,
                SelectionCaptureStatus.Success,
                selectedText,
                Array.Empty<ScreenRect>(),
                unchecked((int)foregroundProcessId));
        }
        catch (Exception exception)
        {
            return NoText(
                SelectionCaptureStatus.Failed,
                $"{exception.GetType().Name}: {exception.Message}");
        }
        finally
        {
            // Do not overwrite anything another application/user placed on
            // the clipboard after our last observation. This comparison is
            // deliberately equality-only because sequence numbers can wrap.
            if (copyWasInjected && clipboardWasChanged)
            {
                TryRestoreSnapshot(clipboardSnapshot, restoreGuardSequence);
            }
        }
    }

    private SnapshotAttempt TryCreateSnapshot(CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + _options.ClipboardAccessTimeout;
        Exception? lastException = null;

        while (DateTime.UtcNow <= deadline && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                var source = System.Windows.Clipboard.GetDataObject();
                if (source is null)
                {
                    return SnapshotAttempt.Success(ClipboardSnapshot.Empty);
                }

                var formats = source.GetFormats(autoConvert: false);
                if (formats.Length == 0)
                {
                    return SnapshotAttempt.Success(ClipboardSnapshot.Empty);
                }

                var materialized = new System.Windows.DataObject();
                foreach (var format in formats)
                {
                    if (!source.GetDataPresent(format, autoConvert: false))
                    {
                        continue;
                    }

                    var value = source.GetData(format, autoConvert: false);
                    if (value is null)
                    {
                        continue;
                    }

                    if (!TryCloneClipboardValue(value, out var clone))
                    {
                        return SnapshotAttempt.Failure(
                            $"Clipboard format '{format}' cannot be safely materialized.");
                    }

                    materialized.SetData(format, clone!, autoConvert: false);
                }

                return SnapshotAttempt.Success(new ClipboardSnapshot(false, materialized));
            }
            catch (ExternalException exception)
            {
                lastException = exception;
                Thread.Sleep(8);
            }
            catch (Exception exception)
            {
                return SnapshotAttempt.Failure(
                    $"{exception.GetType().Name}: {exception.Message}");
            }
        }

        return SnapshotAttempt.Failure(lastException is null
            ? "Clipboard snapshot was cancelled or timed out."
            : $"{lastException.GetType().Name}: {lastException.Message}");
    }

    private static bool TryCloneClipboardValue(object value, out object? clone)
    {
        switch (value)
        {
            case string text:
                clone = text;
                return true;

            case string[] strings:
                clone = strings.ToArray();
                return true;

            case byte[] bytes:
                clone = bytes.ToArray();
                return true;

            case MemoryStream memoryStream:
                clone = new MemoryStream(memoryStream.ToArray(), writable: false);
                return true;

            case System.Windows.Media.Imaging.BitmapSource bitmapSource:
                var bitmapClone = bitmapSource.Clone();
                if (bitmapClone.CanFreeze)
                {
                    bitmapClone.Freeze();
                }

                clone = bitmapClone;
                return true;

            case ICloneable cloneable:
                clone = cloneable.Clone();
                return clone is not null;

            default:
                var type = value.GetType();
                if (type.IsValueType)
                {
                    clone = value;
                    return true;
                }

                clone = null;
                return false;
        }
    }

    private static bool TryReadClipboardText(out string? text)
    {
        try
        {
            if (!System.Windows.Clipboard.ContainsText(System.Windows.TextDataFormat.UnicodeText))
            {
                text = null;
                return false;
            }

            text = System.Windows.Clipboard.GetText(System.Windows.TextDataFormat.UnicodeText);
            return true;
        }
        catch (ExternalException)
        {
            text = null;
            return false;
        }
    }

    private static void TryRestoreSnapshot(ClipboardSnapshot snapshot, uint expectedSequence)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (GetClipboardSequenceNumber() != expectedSequence)
            {
                return;
            }

            try
            {
                if (snapshot.WasEmpty)
                {
                    System.Windows.Clipboard.Clear();
                }
                else
                {
                    System.Windows.Clipboard.SetDataObject(snapshot.DataObject!, copy: true);
                }

                return;
            }
            catch (ExternalException)
            {
                Thread.Sleep(8);
            }
            catch
            {
                return;
            }
        }
    }

    private static bool SendCopyShortcut()
    {
        var controlWasDown = IsKeyDown(VkControl);
        var inputs = new List<NativeInput>(controlWasDown ? 2 : 4);

        if (!controlWasDown)
        {
            inputs.Add(CreateKeyInput(VkControl, keyUp: false));
        }

        inputs.Add(CreateKeyInput(VkC, keyUp: false));
        inputs.Add(CreateKeyInput(VkC, keyUp: true));

        if (!controlWasDown)
        {
            inputs.Add(CreateKeyInput(VkControl, keyUp: true));
        }

        var inputArray = inputs.ToArray();
        var sent = SendInput(
            unchecked((uint)inputArray.Length),
            inputArray,
            Marshal.SizeOf<NativeInput>());

        if (sent == unchecked((uint)inputArray.Length))
        {
            return true;
        }

        // Best effort cleanup if SendInput inserted only a prefix.
        var releases = controlWasDown
            ? new[] { CreateKeyInput(VkC, keyUp: true) }
            : new[]
            {
                CreateKeyInput(VkC, keyUp: true),
                CreateKeyInput(VkControl, keyUp: true),
            };
        _ = SendInput(unchecked((uint)releases.Length), releases, Marshal.SizeOf<NativeInput>());
        return false;
    }

    private static NativeInput CreateKeyInput(ushort virtualKey, bool keyUp) =>
        new()
        {
            Type = InputKeyboard,
            Data = new InputUnion
            {
                Keyboard = new KeyboardInput
                {
                    VirtualKey = virtualKey,
                    Flags = keyUp ? KeyEventKeyUp : 0,
                    ExtraInfo = InjectedInputMarker,
                },
            },
        };

    private static bool IsKeyDown(int virtualKey) =>
        (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private static SelectionCaptureResult NoText(
        SelectionCaptureStatus status,
        string? diagnostic = null) =>
        SelectionCaptureResult.WithoutText(
            SelectionCaptureSource.Clipboard,
            status,
            diagnostic: diagnostic);

    private static void ValidateOptions(ClipboardFallbackOptions options)
    {
        if (options.CopyTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options.CopyTimeout));
        }

        if (options.PollInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options.PollInterval));
        }

        if (options.ClipboardSettleDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options.ClipboardSettleDelay));
        }

        if (options.ClipboardSettleDelay >= options.CopyTimeout)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options.ClipboardSettleDelay),
                "Clipboard settle delay must be shorter than the copy timeout.");
        }

        if (options.ClipboardAccessTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options.ClipboardAccessTimeout));
        }

        if (options.MaxTextLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options.MaxTextLength));
        }
    }

    private sealed class CaptureRequest(CancellationToken cancellationToken)
    {
        private readonly TaskCompletionSource<SelectionCaptureResult> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken CancellationToken { get; } = cancellationToken;

        public Task<SelectionCaptureResult> Task => _completion.Task;

        public void Complete(SelectionCaptureResult result) => _completion.TrySetResult(result);
    }

    private sealed record ClipboardSnapshot(bool WasEmpty, System.Windows.DataObject? DataObject)
    {
        public static ClipboardSnapshot Empty { get; } = new(true, null);
    }

    private sealed record SnapshotAttempt(bool Succeeded, ClipboardSnapshot? Snapshot, string? Diagnostic)
    {
        public static SnapshotAttempt Success(ClipboardSnapshot snapshot) => new(true, snapshot, null);

        public static SnapshotAttempt Failure(string diagnostic) => new(false, null, diagnostic);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeInput
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public KeyboardInput Keyboard;

        [FieldOffset(0)]
        public MouseInput Mouse;

        [FieldOffset(0)]
        public HardwareInput Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HardwareInput
    {
        public uint Message;
        public ushort ParameterLow;
        public ushort ParameterHigh;
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, NativeInput[] inputs, int inputSize);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();
}
