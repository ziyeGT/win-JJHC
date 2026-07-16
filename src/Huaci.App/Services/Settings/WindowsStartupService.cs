using System.Security;
using Microsoft.Win32;

namespace Huaci.App.Services.Settings;

/// <summary>
/// Registers the portable executable for the current Windows user. HKCU is
/// used deliberately so enabling startup never requires administrator rights.
/// </summary>
public sealed class WindowsStartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Huaci";

    public const string StartupArgument = "--startup";

    private readonly string _executablePath;

    public WindowsStartupService(string? executablePath = null)
    {
        var candidate = string.IsNullOrWhiteSpace(executablePath)
            ? Environment.ProcessPath
            : executablePath;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            throw new InvalidOperationException("无法确定 Huaci 可执行文件路径。");
        }

        _executablePath = Path.GetFullPath(candidate);
    }

    public string RunCommand => BuildRunCommand(_executablePath);

    public bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
                return key?.GetValue(ValueName) is string command
                    && string.Equals(command, RunCommand, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception exception) when (IsRegistryAccessException(exception))
            {
                return false;
            }
        }
    }

    public void SetEnabled(bool enabled)
    {
        try
        {
            if (!enabled)
            {
                using var existingKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
                existingKey?.DeleteValue(ValueName, throwOnMissingValue: false);
                return;
            }

            if (!File.Exists(_executablePath))
            {
                throw new FileNotFoundException("Huaci 可执行文件不存在。", _executablePath);
            }

            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
                ?? throw new InvalidOperationException("无法打开 Windows 开机启动注册表项。");
            key.SetValue(ValueName, RunCommand, RegistryValueKind.String);
        }
        catch (Exception exception) when (exception is not StartupRegistrationException)
        {
            throw new StartupRegistrationException("无法更新开机自动启动设置。", exception);
        }
    }

    public static bool IsStartupLaunch(IEnumerable<string> arguments) =>
        arguments.Any(argument => string.Equals(
            argument,
            StartupArgument,
            StringComparison.OrdinalIgnoreCase));

    public static string BuildRunCommand(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        if (executablePath.Contains('"', StringComparison.Ordinal))
        {
            throw new ArgumentException("Executable paths cannot contain quote characters.", nameof(executablePath));
        }

        return $"\"{Path.GetFullPath(executablePath)}\" {StartupArgument}";
    }

    private static bool IsRegistryAccessException(Exception exception) =>
        exception is SecurityException
            or UnauthorizedAccessException
            or IOException
            or PlatformNotSupportedException;
}

public sealed class StartupRegistrationException : Exception
{
    public StartupRegistrationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
