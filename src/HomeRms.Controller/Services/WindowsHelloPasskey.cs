using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace HomeRms.Controller.Services;

/// <summary>
/// Prompts the user for native Windows Hello / Passkey / Biometric / Windows PIN
/// verification using CredUIPromptForWindowsCredentials.
/// </summary>
public static class WindowsHelloPasskey
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDUI_INFO
    {
        public int cbSize;
        public IntPtr hwndParent;
        public string pszMessageText;
        public string pszCaptionText;
        public IntPtr hbmBanner;
    }

    [DllImport("credui.dll", CharSet = CharSet.Unicode)]
    private static extern int CredUIPromptForWindowsCredentials(
        ref CREDUI_INFO pUiInfo,
        int dwAuthError,
        ref uint pulAuthPackage,
        IntPtr pvInAuthBuffer,
        uint ulInAuthBufferSize,
        out IntPtr ppvOutAuthBuffer,
        out uint pulOutAuthBufferSize,
        ref bool pfSave,
        uint dwFlags);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr pv);

    private const uint CREDUIWIN_GENERIC = 0x1;
    private const uint CREDUIWIN_ENUMERATE_CURRENT_USER = 0x200;

    public static bool Verify(Window owner, string message = "Verify your identity to unlock Home RMS")
    {
        var hwnd = new WindowInteropHelper(owner).Handle;
        var info = new CREDUI_INFO
        {
            cbSize = Marshal.SizeOf<CREDUI_INFO>(),
            hwndParent = hwnd,
            pszCaptionText = "Home RMS — Windows Hello / Passkey",
            pszMessageText = message
        };

        uint authPkg = 0;
        bool save = false;
        var result = CredUIPromptForWindowsCredentials(
            ref info,
            0,
            ref authPkg,
            IntPtr.Zero,
            0,
            out var outBuf,
            out _,
            ref save,
            CREDUIWIN_GENERIC | CREDUIWIN_ENUMERATE_CURRENT_USER);

        if (outBuf != IntPtr.Zero)
            CoTaskMemFree(outBuf);

        return result == 0;
    }
}
