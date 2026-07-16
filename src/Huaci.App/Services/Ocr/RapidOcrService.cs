using RapidOcrNet;
using SkiaSharp;

namespace Huaci.App.Services.Ocr;

public sealed record OcrModelPaths(
    string Detector,
    string Classifier,
    string Recognizer,
    string Dictionary)
{
    public static OcrModelPaths FromApplicationDirectory(string? applicationDirectory = null)
    {
        var root = Path.GetFullPath(applicationDirectory ?? AppContext.BaseDirectory);
        var bundledModels = Path.Combine(root, "models", "v5");
        var huaciModels = Path.Combine(root, "Ocr", "models", "v5");

        return new OcrModelPaths(
            Path.Combine(bundledModels, "ch_PP-OCRv5_mobile_det.onnx"),
            Path.Combine(bundledModels, "ch_ppocr_mobile_v2.0_cls_infer.onnx"),
            Path.Combine(huaciModels, "ch_PP-OCRv5_rec_mobile.onnx"),
            Path.Combine(huaciModels, "ppocrv5_dict.txt"));
    }

    public IReadOnlyList<string> FindMissingFiles()
    {
        var paths = new[] { Detector, Classifier, Recognizer, Dictionary };
        return paths.Where(path => !File.Exists(path)).ToArray();
    }
}

/// <summary>
/// Portable, fully local OCR backed by RapidOcrNet 2.0.0 and the PP-OCRv5
/// mobile detector/classifier plus the Chinese recognizer. The Chinese model
/// also recognizes Latin letters and digits, so one pipeline covers the
/// product's Chinese/English screenshot-translation use case.
/// </summary>
public sealed class RapidOcrService : IOcrService
{
    private const int DefaultMaximumImageBytes = 64 * 1024 * 1024;

    private readonly SemaphoreSlim _engineGate = new(1, 1);
    private readonly OcrModelPaths _modelPaths;
    private readonly int _maximumImageBytes;
    private RapidOcr? _engine;
    private string? _initializationError;
    private bool _disposed;

    public RapidOcrService(
        OcrModelPaths? modelPaths = null,
        int maximumImageBytes = DefaultMaximumImageBytes)
    {
        if (maximumImageBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumImageBytes));
        }

        _modelPaths = modelPaths ?? OcrModelPaths.FromApplicationDirectory();
        _maximumImageBytes = maximumImageBytes;
    }

    public bool IsAvailable =>
        !_disposed &&
        _initializationError is null &&
        _modelPaths.FindMissingFiles().Count == 0;

    public string AvailabilityMessage
    {
        get
        {
            if (_disposed)
            {
                return "离线 OCR 已关闭。";
            }

            if (_initializationError is not null)
            {
                return $"离线 OCR 初始化失败：{_initializationError}";
            }

            var missing = _modelPaths.FindMissingFiles();
            return missing.Count == 0
                ? "离线 OCR 已就绪（PP-OCRv5 中文/英文）。"
                : $"离线 OCR 模型不完整，缺少：{string.Join("、", missing.Select(Path.GetFileName))}";
        }
    }

    public Task<OcrRecognitionResult> RecognizeAsync(
        ReadOnlyMemory<byte> encodedImage,
        CancellationToken cancellationToken = default)
    {
        if (encodedImage.IsEmpty)
        {
            return Task.FromResult(OcrRecognitionResult.WithoutText(OcrRecognitionStatus.EmptyImage));
        }

        if (encodedImage.Length > _maximumImageBytes)
        {
            return Task.FromResult(OcrRecognitionResult.WithoutText(
                OcrRecognitionStatus.InvalidImage,
                $"Encoded image exceeds the {_maximumImageBytes}-byte safety limit."));
        }

        var ownedBytes = encodedImage.ToArray();
        return Task.Run(
            () => RecognizeCore(ownedBytes, cancellationToken),
            CancellationToken.None);
    }

    public async Task<OcrRecognitionResult> RecognizeAsync(
        Stream encodedImage,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(encodedImage);

        if (!encodedImage.CanRead)
        {
            return OcrRecognitionResult.WithoutText(
                OcrRecognitionStatus.InvalidImage,
                "The image stream is not readable.");
        }

        try
        {
            if (encodedImage.CanSeek && encodedImage.Length - encodedImage.Position > _maximumImageBytes)
            {
                return OcrRecognitionResult.WithoutText(
                    OcrRecognitionStatus.InvalidImage,
                    $"Encoded image exceeds the {_maximumImageBytes}-byte safety limit.");
            }

            using var buffer = new MemoryStream();
            var chunk = new byte[81_920];
            while (true)
            {
                var read = await encodedImage
                    .ReadAsync(chunk.AsMemory(), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (buffer.Length + read > _maximumImageBytes)
                {
                    return OcrRecognitionResult.WithoutText(
                        OcrRecognitionStatus.InvalidImage,
                        $"Encoded image exceeds the {_maximumImageBytes}-byte safety limit.");
                }

                await buffer
                    .WriteAsync(chunk.AsMemory(0, read), cancellationToken)
                    .ConfigureAwait(false);
            }

            return await RecognizeAsync(buffer.ToArray(), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return OcrRecognitionResult.WithoutText(OcrRecognitionStatus.Cancelled);
        }
        catch (Exception exception)
        {
            return OcrRecognitionResult.WithoutText(
                OcrRecognitionStatus.InvalidImage,
                $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    public void Dispose()
    {
        _engineGate.Wait();
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _engine?.Dispose();
            _engine = null;
        }
        finally
        {
            _engineGate.Release();
        }

        GC.SuppressFinalize(this);
    }

    private OcrRecognitionResult RecognizeCore(
        byte[] encodedImage,
        CancellationToken cancellationToken)
    {
        var enteredGate = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _engineGate.Wait(cancellationToken);
            enteredGate = true;

            if (_disposed)
            {
                return OcrRecognitionResult.WithoutText(OcrRecognitionStatus.Disposed);
            }

            if (!TryInitializeEngine(out var initializationDiagnostic))
            {
                return OcrRecognitionResult.WithoutText(
                    OcrRecognitionStatus.EngineUnavailable,
                    initializationDiagnostic);
            }

            cancellationToken.ThrowIfCancellationRequested();
            using var bitmap = SKBitmap.Decode(encodedImage);
            if (bitmap is null || bitmap.Width <= 0 || bitmap.Height <= 0)
            {
                return OcrRecognitionResult.WithoutText(
                    OcrRecognitionStatus.InvalidImage,
                    "SkiaSharp could not decode the image.");
            }

            var rapidResult = _engine!.Detect(bitmap, RapidOcrOptions.Default);
            cancellationToken.ThrowIfCancellationRequested();

            var blocks = rapidResult.TextBlocks
                .Where(block => !string.IsNullOrWhiteSpace(block.Text))
                .Select(block => new OcrTextBlock(
                    block.Text.Trim(),
                    block.CharScores is null ? 0d : block.CharScores.Average(),
                    GetBounds(block.BoxPoints)))
                .ToArray();

            if (blocks.Length == 0)
            {
                return OcrRecognitionResult.WithoutText(OcrRecognitionStatus.NoText);
            }

            return new OcrRecognitionResult(
                OcrRecognitionStatus.Success,
                string.Join(Environment.NewLine, blocks.Select(block => block.Text)),
                blocks);
        }
        catch (OperationCanceledException)
        {
            return OcrRecognitionResult.WithoutText(OcrRecognitionStatus.Cancelled);
        }
        catch (Exception exception)
        {
            return OcrRecognitionResult.WithoutText(
                OcrRecognitionStatus.Failed,
                $"{exception.GetType().Name}: {exception.Message}");
        }
        finally
        {
            if (enteredGate)
            {
                _engineGate.Release();
            }
        }
    }

    private bool TryInitializeEngine(out string? diagnostic)
    {
        if (_engine is not null)
        {
            diagnostic = null;
            return true;
        }

        if (_initializationError is not null)
        {
            diagnostic = _initializationError;
            return false;
        }

        var missing = _modelPaths.FindMissingFiles();
        if (missing.Count > 0)
        {
            diagnostic = $"Missing OCR model files: {string.Join(", ", missing)}";
            return false;
        }

        try
        {
            var engine = new RapidOcr();
            engine.InitModels(
                detPath: _modelPaths.Detector,
                clsPath: _modelPaths.Classifier,
                recPath: _modelPaths.Recognizer,
                keysPath: _modelPaths.Dictionary);

            _engine = engine;
            diagnostic = null;
            return true;
        }
        catch (Exception exception)
        {
            _initializationError = $"{exception.GetType().Name}: {exception.Message}";
            diagnostic = _initializationError;
            return false;
        }
    }

    private static OcrRectangle GetBounds(IReadOnlyList<SKPointI> points)
    {
        if (points.Count == 0)
        {
            return default;
        }

        var left = points.Min(point => point.X);
        var top = points.Min(point => point.Y);
        var right = points.Max(point => point.X);
        var bottom = points.Max(point => point.Y);
        return new OcrRectangle(left, top, right - left, bottom - top);
    }
}
