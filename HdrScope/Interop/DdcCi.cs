using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace HdrScope.Interop;

public sealed class VcpValue
{
    public byte Code { get; init; }
    public uint Current { get; init; }
    public uint Maximum { get; init; }
}

/// <summary>
/// DDC/CI monitor control via dxva2.dll. All writes are to volatile monitor settings
/// (same as pressing OSD buttons); monitor factory reset always restores defaults.
/// </summary>
public sealed class DdcCi : IDisposable
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PHYSICAL_MONITOR
    {
        public IntPtr hPhysicalMonitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szPhysicalMonitorDescription;
    }

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint count);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint count, [Out] PHYSICAL_MONITOR[] monitors);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool DestroyPhysicalMonitors(uint count, PHYSICAL_MONITOR[] monitors);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr hMonitor, byte vcpCode, out uint vcpType, out uint currentValue, out uint maximumValue);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool SetVCPFeature(IntPtr hMonitor, byte vcpCode, uint newValue);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetCapabilitiesStringLength(IntPtr hMonitor, out uint length);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool CapabilitiesRequestAndCapabilitiesReply(IntPtr hMonitor, [Out] byte[] asciiCaps, uint length);

    public const byte VcpBrightness = 0x10;
    public const byte VcpContrast = 0x12;
    public const byte VcpGainRed = 0x16;
    public const byte VcpGainGreen = 0x18;
    public const byte VcpGainBlue = 0x1A;
    public const byte VcpColorPreset = 0x14;
    public const byte VcpDisplayMode = 0xDC;

    private PHYSICAL_MONITOR[] _monitors = [];
    private bool _disposed;

    public IntPtr Handle => _monitors.Length > 0 ? _monitors[0].hPhysicalMonitor : IntPtr.Zero;
    public string Description => _monitors.Length > 0 ? _monitors[0].szPhysicalMonitorDescription : "";

    public static DdcCi? Open(IntPtr hmonitor)
    {
        if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hmonitor, out uint count) || count == 0) return null;
        var mons = new PHYSICAL_MONITOR[count];
        if (!GetPhysicalMonitorsFromHMONITOR(hmonitor, count, mons)) return null;
        return new DdcCi { _monitors = mons };
    }

    public VcpValue? Read(byte code)
    {
        if (!GetVCPFeatureAndVCPFeatureReply(Handle, code, out _, out uint cur, out uint max)) return null;
        return new VcpValue { Code = code, Current = cur, Maximum = max };
    }

    public bool Write(byte code, uint value) => SetVCPFeature(Handle, code, value);

    public string? ReadCapabilities()
    {
        if (!GetCapabilitiesStringLength(Handle, out uint len) || len == 0) return null;
        var buf = new byte[len];
        if (!CapabilitiesRequestAndCapabilitiesReply(Handle, buf, len)) return null;
        return Encoding.ASCII.GetString(buf).TrimEnd('\0');
    }

    /// <summary>Snapshot commonly used codes for logging/restore.</summary>
    public Dictionary<string, VcpValue?> Snapshot()
    {
        return new Dictionary<string, VcpValue?>
        {
            ["brightness(0x10)"] = Read(VcpBrightness),
            ["contrast(0x12)"] = Read(VcpContrast),
            ["gainR(0x16)"] = Read(VcpGainRed),
            ["gainG(0x18)"] = Read(VcpGainGreen),
            ["gainB(0x1A)"] = Read(VcpGainBlue),
            ["colorPreset(0x14)"] = Read(VcpColorPreset),
            ["displayMode(0xDC)"] = Read(VcpDisplayMode),
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_monitors.Length > 0) DestroyPhysicalMonitors((uint)_monitors.Length, _monitors);
    }
}
