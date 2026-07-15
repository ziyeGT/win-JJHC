using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Cryptography;
using System.Text;

namespace Huaci.App.Services.Settings;

public sealed class CredentialManagerSecretStore
{
    public const string CredentialTarget = "Huaci/TranslationApi";

    private const uint CredentialTypeGeneric = 1;
    private const uint CredentialPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;
    private const int MaxCredentialBlobSize = 2_560;

    public string? Read()
    {
        EnsureWindows();

        if (!CredRead(CredentialTarget, CredentialTypeGeneric, 0, out IntPtr credentialPointer))
        {
            int error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound)
            {
                return null;
            }

            throw new Win32Exception(error, "无法从 Windows 凭据管理器读取翻译 API 密钥。");
        }

        try
        {
            NativeCredential credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
            {
                return string.Empty;
            }

            if (credential.CredentialBlobSize > MaxCredentialBlobSize)
            {
                throw new InvalidDataException("Windows 凭据中的翻译 API 密钥长度异常。");
            }

            byte[] secretBytes = new byte[credential.CredentialBlobSize];
            try
            {
                Marshal.Copy(credential.CredentialBlob, secretBytes, 0, secretBytes.Length);
                return Encoding.Unicode.GetString(secretBytes).TrimEnd('\0');
            }
            finally
            {
                CryptographicOperations.ZeroMemory(secretBytes);
            }
        }
        finally
        {
            CredFree(credentialPointer);
        }
    }

    public void Write(string apiKey)
    {
        ArgumentNullException.ThrowIfNull(apiKey);
        EnsureWindows();

        string normalized = apiKey.Trim();
        if (normalized.Length == 0)
        {
            Delete();
            return;
        }

        byte[] secretBytes = Encoding.Unicode.GetBytes(normalized);
        if (secretBytes.Length > MaxCredentialBlobSize)
        {
            CryptographicOperations.ZeroMemory(secretBytes);
            throw new ArgumentOutOfRangeException(nameof(apiKey), "API 密钥超过 Windows 凭据管理器允许的长度。");
        }

        GCHandle pinnedSecret = default;
        try
        {
            pinnedSecret = GCHandle.Alloc(secretBytes, GCHandleType.Pinned);
            NativeCredential credential = new()
            {
                Type = CredentialTypeGeneric,
                TargetName = CredentialTarget,
                CredentialBlobSize = (uint)secretBytes.Length,
                CredentialBlob = pinnedSecret.AddrOfPinnedObject(),
                Persist = CredentialPersistLocalMachine,
                UserName = Environment.UserName
            };

            if (!CredWrite(ref credential, 0))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "无法将翻译 API 密钥保存到 Windows 凭据管理器。");
            }
        }
        finally
        {
            if (pinnedSecret.IsAllocated)
            {
                pinnedSecret.Free();
            }

            CryptographicOperations.ZeroMemory(secretBytes);
        }
    }

    public bool Delete()
    {
        EnsureWindows();

        if (CredDelete(CredentialTarget, CredentialTypeGeneric, 0))
        {
            return true;
        }

        int error = Marshal.GetLastWin32Error();
        if (error == ErrorNotFound)
        {
            return false;
        }

        throw new Win32Exception(error, "无法从 Windows 凭据管理器删除翻译 API 密钥。");
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows 凭据管理器仅在 Windows 上可用。");
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string TargetName;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? Comment;

        public FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? TargetAlias;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(
        string target,
        uint type,
        uint reservedFlag,
        out IntPtr credentialPtr);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite([In] ref NativeCredential userCredential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
