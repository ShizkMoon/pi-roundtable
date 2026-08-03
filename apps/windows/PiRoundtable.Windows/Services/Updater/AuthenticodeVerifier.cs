using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PiRoundtable.Windows.Services.Updater;

internal interface IAuthenticodeVerifier
{
    bool IsTrusted(string filePath, SafeFileHandle fileHandle);
}
internal sealed class WindowsAuthenticodeVerifier : IAuthenticodeVerifier
{
    private static readonly Guid GenericVerifyV2Action = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    public bool IsTrusted(string filePath, SafeFileHandle fileHandle)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(fileHandle);
        if (fileHandle.IsInvalid || fileHandle.IsClosed)
        {
            throw new ArgumentException("Authenticode requires one open file handle.", nameof(fileHandle));
        }

        var filePathPointer = Marshal.StringToCoTaskMemUni(filePath);
        var fileInfoPointer = IntPtr.Zero;
        var addedReference = false;
        try
        {
            fileHandle.DangerousAddRef(ref addedReference);
            var fileInfo = new WinTrustFileInfo
            {
                StructSize = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
                FilePath = filePathPointer,
                FileHandle = fileHandle.DangerousGetHandle(),
            };
            fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);

            var trustData = CreateTrustData(fileInfoPointer, stateAction: 1);
            var action = GenericVerifyV2Action;
            try
            {
                return WinVerifyTrust(IntPtr.Zero, ref action, ref trustData) == 0;
            }
            finally
            {
                if (trustData.StateData != IntPtr.Zero)
                {
                    var closeData = trustData;
                    closeData.StateAction = 2;
                    _ = WinVerifyTrust(IntPtr.Zero, ref action, ref closeData);
                }
            }
        }
        finally
        {
            if (fileInfoPointer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(fileInfoPointer);
            }
            if (addedReference)
            {
                fileHandle.DangerousRelease();
            }
            Marshal.FreeCoTaskMem(filePathPointer);
        }
    }

    private static WinTrustData CreateTrustData(IntPtr fileInfoPointer, uint stateAction)
    {
        return new WinTrustData
        {
            StructSize = (uint)Marshal.SizeOf<WinTrustData>(),
            UiChoice = 2,
            RevocationChecks = 1,
            UnionChoice = 1,
            FileInfo = fileInfoPointer,
            StateAction = stateAction,
            ProviderFlags = 0x00000080,
            UiContext = 0,
        };
    }

    [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int WinVerifyTrust(
        IntPtr windowHandle,
        [In] ref Guid actionId,
        [In, Out] ref WinTrustData trustData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint StructSize;
        public IntPtr FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        public uint StructSize;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfo;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }
}
