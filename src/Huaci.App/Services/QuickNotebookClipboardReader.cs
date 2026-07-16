using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using DrawingImage = System.Drawing.Image;
using DrawingImageFormat = System.Drawing.Imaging.ImageFormat;
using WpfDataFormats = System.Windows.DataFormats;
using WpfDataObject = System.Windows.IDataObject;

namespace Huaci.App.Services.Notebook;

/// <summary>
/// Materializes clipboard or drag/drop data without accessing the global clipboard.
/// Keeping this conversion separate makes the supported formats deterministic and testable.
/// </summary>
public static class QuickNotebookClipboardReader
{
    private static readonly string[] SupportedImageExtensions =
    [
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".wdp", ".jxr"
    ];

    public static QuickNotebookClipboardContent? Read(WpfDataObject dataObject)
    {
        ArgumentNullException.ThrowIfNull(dataObject);

        BitmapSource? image = TryReadFileDropImage(dataObject);
        if (image is not null)
        {
            return new QuickNotebookClipboardContent(null, image, "图片文件");
        }

        image = TryReadNamedPng(dataObject);
        if (image is not null)
        {
            return new QuickNotebookClipboardContent(null, image, "剪贴板图片");
        }

        if (TryGetData(dataObject, WpfDataFormats.Bitmap, autoConvert: true, out object? bitmapValue))
        {
            image = TryConvertToBitmapSource(bitmapValue);
            if (image is not null)
            {
                return new QuickNotebookClipboardContent(null, image, "剪贴板图片");
            }
        }

        if (TryGetData(dataObject, WpfDataFormats.UnicodeText, autoConvert: true, out object? unicodeText)
            && unicodeText is string text)
        {
            return new QuickNotebookClipboardContent(text, null, "剪贴板文字");
        }

        if (TryGetData(dataObject, WpfDataFormats.Text, autoConvert: true, out object? ansiText)
            && ansiText is string fallbackText)
        {
            return new QuickNotebookClipboardContent(fallbackText, null, "剪贴板文字");
        }

        return null;
    }

    public static bool ContainsSupportedData(WpfDataObject dataObject)
    {
        ArgumentNullException.ThrowIfNull(dataObject);

        try
        {
            if (dataObject.GetDataPresent(WpfDataFormats.Bitmap, autoConvert: true)
                || dataObject.GetDataPresent(WpfDataFormats.UnicodeText, autoConvert: true)
                || dataObject.GetFormats(autoConvert: false)
                    .Any(format => format.Contains("png", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            if (!dataObject.GetDataPresent(WpfDataFormats.FileDrop, autoConvert: true))
            {
                return false;
            }

            return EnumerateFileDropPaths(dataObject.GetData(WpfDataFormats.FileDrop, autoConvert: true))
                .Any(IsSupportedImagePath);
        }
        catch (Exception exception) when (IsClipboardFormatException(exception))
        {
            return false;
        }
    }

    public static BitmapSource? TryConvertToBitmapSource(object? value)
    {
        try
        {
            switch (value)
            {
                case null:
                    return null;
                case BitmapSource bitmapSource:
                    BitmapSource clone = bitmapSource.CloneCurrentValue();
                    clone.Freeze();
                    return clone;
                case byte[] bytes when bytes.Length > 0:
                    using (var stream = new MemoryStream(bytes, writable: false))
                    {
                        return DecodeBitmapStream(stream);
                    }
                case Stream stream:
                    return DecodeBitmapStream(stream);
                case DrawingImage drawingImage:
                    using (var pngStream = new MemoryStream())
                    {
                        drawingImage.Save(pngStream, DrawingImageFormat.Png);
                        pngStream.Position = 0;
                        return DecodeBitmapStream(pngStream);
                    }
                default:
                    return null;
            }
        }
        catch (Exception exception) when (IsImageDecodeException(exception))
        {
            return null;
        }
    }

    private static BitmapSource? TryReadFileDropImage(WpfDataObject dataObject)
    {
        if (!TryGetData(dataObject, WpfDataFormats.FileDrop, autoConvert: true, out object? value))
        {
            return null;
        }

        foreach (string filePath in EnumerateFileDropPaths(value))
        {
            if (!IsSupportedImagePath(filePath))
            {
                continue;
            }

            BitmapSource? image = TryDecodeImageFile(filePath);
            if (image is not null)
            {
                return image;
            }
        }

        return null;
    }

    private static BitmapSource? TryReadNamedPng(WpfDataObject dataObject)
    {
        string[] formats;
        try
        {
            formats = dataObject.GetFormats(autoConvert: false);
        }
        catch (Exception exception) when (IsClipboardFormatException(exception))
        {
            return null;
        }

        foreach (string format in formats
                     .Where(value => value.Contains("png", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(value => value.Equals("PNG", StringComparison.OrdinalIgnoreCase) ? 0 : 1))
        {
            if (!TryGetData(dataObject, format, autoConvert: false, out object? value))
            {
                continue;
            }

            BitmapSource? image = TryConvertToBitmapSource(value);
            if (image is not null)
            {
                return image;
            }
        }

        return null;
    }

    private static bool TryGetData(
        WpfDataObject dataObject,
        string format,
        bool autoConvert,
        out object? value)
    {
        value = null;
        try
        {
            if (!dataObject.GetDataPresent(format, autoConvert))
            {
                return false;
            }

            value = dataObject.GetData(format, autoConvert);
            return value is not null;
        }
        catch (Exception exception) when (IsClipboardFormatException(exception))
        {
            return false;
        }
    }

    private static IEnumerable<string> EnumerateFileDropPaths(object? value)
    {
        return value switch
        {
            string[] paths => paths,
            StringCollection paths => paths.Cast<string>(),
            IEnumerable<string> paths => paths,
            _ => []
        };
    }

    private static bool IsSupportedImagePath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return false;
        }

        string extension = Path.GetExtension(filePath);
        return SupportedImageExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
    }

    private static BitmapSource? TryDecodeImageFile(string filePath)
    {
        try
        {
            using var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return DecodeBitmapStream(stream);
        }
        catch (Exception exception) when (IsImageDecodeException(exception))
        {
            return null;
        }
    }

    private static BitmapSource DecodeBitmapStream(Stream stream)
    {
        long originalPosition = 0;
        if (stream.CanSeek)
        {
            originalPosition = stream.Position;
            stream.Position = 0;
        }

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        finally
        {
            if (stream.CanSeek)
            {
                stream.Position = originalPosition;
            }
        }
    }

    private static bool IsClipboardFormatException(Exception exception) =>
        exception is ExternalException
            or IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or NotSupportedException
            or ArgumentException;

    private static bool IsImageDecodeException(Exception exception) =>
        IsClipboardFormatException(exception)
        || exception is System.Security.SecurityException;
}

public sealed record QuickNotebookClipboardContent(
    string? Text,
    BitmapSource? Image,
    string SourceLabel);
