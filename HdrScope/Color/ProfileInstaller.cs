using System;
using System.IO;
using System.Runtime.InteropServices;

namespace HdrScope.Color;

public static class ProfileInstaller
{
    private enum WCS_PROFILE_MANAGEMENT_SCOPE { SYSTEM_WIDE = 0, CURRENT_USER = 1 }

    [DllImport("mscms.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool InstallColorProfileW(string? machineName, string profilePath);

    [DllImport("mscms.dll", CharSet = CharSet.Unicode)]
    private static extern int ColorProfileAddDisplayAssociation(
        WCS_PROFILE_MANAGEMENT_SCOPE scope,
        string profileName,
        Interop.LuidValue targetAdapterID,
        uint sourceID,
        [MarshalAs(UnmanagedType.Bool)] bool setAsDefault,
        [MarshalAs(UnmanagedType.Bool)] bool associateAsAdvancedColor);

    private enum COLORPROFILETYPE { CPT_ICC = 0 }
    private enum COLORPROFILESUBTYPE { CPST_STANDARD_DISPLAY_COLOR_MODE = 4, CPST_EXTENDED_DISPLAY_COLOR_MODE = 5 }

    [DllImport("mscms.dll", CharSet = CharSet.Unicode)]
    private static extern int ColorProfileGetDisplayDefault(
        WCS_PROFILE_MANAGEMENT_SCOPE scope,
        Interop.LuidValue targetAdapterID,
        uint sourceID,
        COLORPROFILETYPE profileType,
        COLORPROFILESUBTYPE profileSubType,
        out IntPtr profileName);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    /// <summary>Returns the file name of the current default display profile (HDR slot if advanced=true), or null.</summary>
    public static string? GetDefaultProfileName(Interop.LuidValue adapterId, uint sourceId, bool advanced)
    {
        var subtype = advanced
            ? COLORPROFILESUBTYPE.CPST_EXTENDED_DISPLAY_COLOR_MODE
            : COLORPROFILESUBTYPE.CPST_STANDARD_DISPLAY_COLOR_MODE;
        foreach (var scope in new[] { WCS_PROFILE_MANAGEMENT_SCOPE.CURRENT_USER, WCS_PROFILE_MANAGEMENT_SCOPE.SYSTEM_WIDE })
        {
            try
            {
                if (ColorProfileGetDisplayDefault(scope, adapterId, sourceId, COLORPROFILETYPE.CPT_ICC, subtype, out IntPtr p) == 0 && p != IntPtr.Zero)
                {
                    string? name = Marshal.PtrToStringUni(p);
                    LocalFree(p);
                    if (!string.IsNullOrWhiteSpace(name)) return name;
                }
            }
            catch
            {
                // API missing on older builds — status just shows unknown.
            }
        }
        return null;
    }

    /// <summary>
    /// Installs the .icm into the system color store and associates it with the display
    /// as an advanced-color (HDR) profile for the current user, set as default.
    /// Returns a human-readable status string.
    /// </summary>
    public static string InstallAndAssociateHdr(string profilePath, Interop.LuidValue adapterId, uint sourceId)
    {
        if (!InstallColorProfileW(null, profilePath))
        {
            int err = Marshal.GetLastWin32Error();
            // ERROR_FILE_EXISTS is fine — same name already in store; we overwrite by copy
            if (err != 80 && err != 183)
                return $"InstallColorProfile failed (Win32 error {err}). " +
                       "Встановіть вручну: ПКМ по .icm → 'Install profile', потім colorcpl.exe.";
        }

        string fileName = Path.GetFileName(profilePath);
        int hr = ColorProfileAddDisplayAssociation(
            WCS_PROFILE_MANAGEMENT_SCOPE.CURRENT_USER, fileName, adapterId, sourceId,
            setAsDefault: true, associateAsAdvancedColor: true);

        if (hr != 0)
            return $"Профіль встановлено, але асоціація не вдалася (HRESULT 0x{hr:X8}). " +
                   "Відкрийте colorcpl.exe → вкладка вашого монітора → Add → оберіть профіль, позначте 'Add as HDR profile', Set as Default.";

        return "Профіль встановлено і призначено як HDR-профіль за замовчуванням. " +
               "Якщо ефект не видно одразу — вимкніть/увімкніть HDR (Win+Alt+B).";
    }

    /// <summary>Installs and associates a profile for SDR (standard color) mode, set as default.</summary>
    public static string InstallAndAssociateSdr(string profilePath, Interop.LuidValue adapterId, uint sourceId)
    {
        if (!InstallColorProfileW(null, profilePath))
        {
            int err = Marshal.GetLastWin32Error();
            if (err != 80 && err != 183)
                return $"InstallColorProfile failed (Win32 error {err}).";
        }

        string fileName = Path.GetFileName(profilePath);
        int hr = ColorProfileAddDisplayAssociation(
            WCS_PROFILE_MANAGEMENT_SCOPE.CURRENT_USER, fileName, adapterId, sourceId,
            setAsDefault: true, associateAsAdvancedColor: false);

        if (hr != 0)
            return $"Профіль встановлено, але асоціація не вдалася (HRESULT 0x{hr:X8}). " +
                   "Додайте вручну: colorcpl.exe → Add → Set as Default (без позначки HDR).";

        return "SDR-профіль встановлено як типовий для звичайного (не-HDR) режиму.";
    }
}
