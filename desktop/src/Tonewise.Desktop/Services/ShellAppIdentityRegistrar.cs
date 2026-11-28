using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Microsoft.Win32;

namespace Tonewise.Desktop.Services;

/// <summary>
/// Registers an explicit Process AUMID, HKCU AppUserModelId metadata, and a Start Menu shortcut
/// stamped with <c>PKEY_AppUserModel_ID</c> so Windows 11 Quick Settings SMTC displays "Tonewise"
/// and its icon instead of "Unknown app" for unpackaged WinUI 3 deployments.
/// </summary>
internal static class ShellAppIdentityRegistrar
{
    private const uint VtLpwstr = 31;
    private const uint ShcneAssocChanged = 0x08000000;
    private const uint ShcnfIdList = 0x0000;

    private static readonly Guid ClsidShellLink = new("00021401-0000-0000-C000-000000000046");
    private static readonly Guid PkeyAppUserModelIdGuid = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
    private const uint PkeyAppUserModelIdPid = 5;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pvar);

    public static void Register(string appUserModelId, string displayName)
    {
        if (string.IsNullOrWhiteSpace(appUserModelId) || string.IsNullOrWhiteSpace(displayName))
        {
            return;
        }

        try
        {
            _ = SetCurrentProcessExplicitAppUserModelID(appUserModelId);
        }
        catch
        {
            // Ignore on hosts without Shell32 AUMID support
        }

        string exePath = Environment.ProcessPath ?? string.Empty;
        string icoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "tonewise.ico");
        string iconUri = File.Exists(icoPath) ? icoPath : exePath;

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(
                $@"Software\Classes\AppUserModelId\{appUserModelId}");
            key?.SetValue("DisplayName", displayName, RegistryValueKind.String);
            if (!string.IsNullOrWhiteSpace(iconUri))
            {
                key?.SetValue("IconUri", iconUri, RegistryValueKind.String);
            }
            key?.SetValue("IconBackgroundColor", "00000000", RegistryValueKind.String);
        }
        catch
        {
            // Ignore HKCU registry failures under restricted profiles
        }

        try
        {
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            {
                return;
            }
            string fileName = Path.GetFileName(exePath);
            if (fileName.Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase)
                || fileName.StartsWith("testhost", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            EnsureStartMenuShortcut(appUserModelId, displayName, exePath, iconUri);
        }
        catch
        {
            // Non-fatal COM shortcut failure
        }
    }

    private static void EnsureStartMenuShortcut(
        string appUserModelId,
        string displayName,
        string exePath,
        string iconPath)
    {
        string programsDir = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        if (string.IsNullOrWhiteSpace(programsDir))
        {
            return;
        }
        Directory.CreateDirectory(programsDir);

        string lnkPath = Path.Combine(programsDir, $"{displayName}.lnk");
        string legacyLnkPath = Path.Combine(programsDir, "Tonewise.Desktop.lnk");

        Type? shellLinkType = Type.GetTypeFromCLSID(ClsidShellLink, throwOnError: false);
        if (shellLinkType is null)
        {
            return;
        }

        object? comObj = null;
        try
        {
            comObj = Activator.CreateInstance(shellLinkType);
            if (comObj is not IShellLinkW shellLink
                || comObj is not IPropertyStore propStore
                || comObj is not IPersistFile persistFile)
            {
                return;
            }

            VerifyHResult(shellLink.SetPath(exePath));
            string? workDir = Path.GetDirectoryName(exePath);
            if (!string.IsNullOrWhiteSpace(workDir))
            {
                VerifyHResult(shellLink.SetWorkingDirectory(workDir));
            }
            VerifyHResult(shellLink.SetDescription(displayName));
            if (!string.IsNullOrWhiteSpace(iconPath))
            {
                VerifyHResult(shellLink.SetIconLocation(iconPath, 0));
            }

            var aumidKey = new PropertyKey(PkeyAppUserModelIdGuid, PkeyAppUserModelIdPid);
            var pv = new PropVariant
            {
                vt = (ushort)VtLpwstr,
                pointerValue = Marshal.StringToCoTaskMemUni(appUserModelId),
            };
            try
            {
                VerifyHResult(propStore.SetValue(ref aumidKey, ref pv));
                VerifyHResult(propStore.Commit());
            }
            finally
            {
                _ = PropVariantClear(ref pv);
            }

            persistFile.Save(lnkPath, fRemember: true);
        }
        finally
        {
            if (comObj is not null && Marshal.IsComObject(comObj))
            {
                _ = Marshal.FinalReleaseComObject(comObj);
            }
        }

        if (!string.Equals(legacyLnkPath, lnkPath, StringComparison.OrdinalIgnoreCase)
            && File.Exists(legacyLnkPath))
        {
            try
            {
                File.Delete(legacyLnkPath);
            }
            catch
            {
                // Ignore if locked
            }
        }

        try
        {
            SHChangeNotify(ShcneAssocChanged, ShcnfIdList, IntPtr.Zero, IntPtr.Zero);
        }
        catch
        {
            // Ignore shell notify failures
        }
    }

    private static void VerifyHResult(int hr)
    {
        if (hr < 0)
        {
            Marshal.ThrowExceptionForHR(hr);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey(Guid fmtid, uint pid)
    {
        public Guid fmtid = fmtid;
        public uint pid = pid;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant
    {
        [FieldOffset(0)]
        public ushort vt;

        [FieldOffset(8)]
        public IntPtr pointerValue;
    }

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        [PreserveSig]
        int GetPath(
            [Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszFile,
            int cchMaxPath,
            IntPtr pfd,
            uint fFlags);

        [PreserveSig]
        int GetIDList(out IntPtr ppidl);

        [PreserveSig]
        int SetIDList(IntPtr pidl);

        [PreserveSig]
        int GetDescription(
            [Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszName,
            int cchMaxName);

        [PreserveSig]
        int SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);

        [PreserveSig]
        int GetWorkingDirectory(
            [Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszDir,
            int cchMaxPath);

        [PreserveSig]
        int SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);

        [PreserveSig]
        int GetArguments(
            [Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszArgs,
            int cchMaxPath);

        [PreserveSig]
        int SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);

        [PreserveSig]
        int GetHotkey(out short pwHotkey);

        [PreserveSig]
        int SetHotkey(short wHotkey);

        [PreserveSig]
        int GetShowCmd(out int piShowCmd);

        [PreserveSig]
        int SetShowCmd(int iShowCmd);

        [PreserveSig]
        int GetIconLocation(
            [Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszIconPath,
            int cchIconPath,
            out int piIcon);

        [PreserveSig]
        int SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);

        [PreserveSig]
        int SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);

        [PreserveSig]
        int Resolve(IntPtr hwnd, uint fFlags);

        [PreserveSig]
        int SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig]
        int GetCount(out uint cProps);

        [PreserveSig]
        int GetAt(uint iProp, out PropertyKey pkey);

        [PreserveSig]
        int GetValue(ref PropertyKey key, out PropVariant pv);

        [PreserveSig]
        int SetValue(ref PropertyKey key, ref PropVariant pv);

        [PreserveSig]
        int Commit();
    }
}
