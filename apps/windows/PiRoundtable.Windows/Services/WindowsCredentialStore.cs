using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace PiRoundtable.Windows.Services;

internal sealed class WindowsCredentialStore
{
    private const string ReferencePrefix = "wincred://";
    private const uint CredentialTypeGeneric = 1;
    private const uint CredentialPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    public async Task SaveAsync(
        string credentialReference,
        string secret,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new ArgumentException("凭据不能为空。", nameof(secret));
        }
        var targetName = GetTargetName(credentialReference);
        var bytes = Encoding.UTF8.GetBytes(secret);
        if (bytes.Length > 2560)
        {
            throw new ArgumentOutOfRangeException(nameof(secret), "凭据超过 Windows Credential Manager 限制。");
        }

        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var blob = Marshal.AllocCoTaskMem(bytes.Length);
            try
            {
                Marshal.Copy(bytes, 0, blob, bytes.Length);
                var credential = new NativeCredential
                {
                    Type = CredentialTypeGeneric,
                    TargetName = targetName,
                    CredentialBlobSize = (uint)bytes.Length,
                    CredentialBlob = blob,
                    Persist = CredentialPersistLocalMachine,
                    UserName = Environment.UserName,
                };
                if (!CredWrite(ref credential, 0))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
                Marshal.FreeCoTaskMem(blob);
            }
        }, CancellationToken.None);
    }

    public async Task<string?> ReadAsync(
        string credentialReference,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var targetName = GetTargetName(credentialReference);
        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!CredRead(targetName, CredentialTypeGeneric, 0, out var pointer))
            {
                var error = Marshal.GetLastWin32Error();
                if (error == ErrorNotFound)
                {
                    return null;
                }
                throw new Win32Exception(error);
            }
            try
            {
                var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
                if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
                {
                    return string.Empty;
                }
                var bytes = new byte[checked((int)credential.CredentialBlobSize)];
                try
                {
                    Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
                    return Encoding.UTF8.GetString(bytes);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(bytes);
                }
            }
            finally
            {
                CredFree(pointer);
            }
        }, CancellationToken.None);
    }

    public Task DeleteAsync(string credentialReference, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var targetName = GetTargetName(credentialReference);
        if (!CredDelete(targetName, CredentialTypeGeneric, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorNotFound)
            {
                throw new Win32Exception(error);
            }
        }
        return Task.CompletedTask;
    }

    private static string GetTargetName(string credentialReference)
    {
        if (!credentialReference.StartsWith(ReferencePrefix, StringComparison.Ordinal) ||
            credentialReference.Length == ReferencePrefix.Length)
        {
            throw new ArgumentException("凭据引用必须使用 wincred://。", nameof(credentialReference));
        }
        return credentialReference[ReferencePrefix.Length..];
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }

    [DllImport("Advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    [DllImport("Advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("Advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("Advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
