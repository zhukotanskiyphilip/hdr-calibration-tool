using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace HdrScope.Interop;

[StructLayout(LayoutKind.Sequential)]
public struct LuidValue { public uint LowPart; public int HighPart; }

public sealed record AdvancedColorStatus(
    uint TargetId,
    uint SourceId,
    LuidValue AdapterId,
    bool Supported,
    bool Enabled,
    uint ColorEncoding,
    uint BitsPerColorChannel,
    double SdrWhiteLevelNits);

public static class AdvancedColorInfo
{
    [StructLayout(LayoutKind.Sequential)] private struct RATIONAL { public uint Numerator; public uint Denominator; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PATH_SOURCE_INFO { public LuidValue adapterId; public uint id; public uint modeInfoIdx; public uint statusFlags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PATH_TARGET_INFO
    {
        public LuidValue adapterId; public uint id; public uint modeInfoIdx; public uint outputTechnology;
        public uint rotation; public uint scaling; public RATIONAL refreshRate; public uint scanLineOrdering;
        public int targetAvailable; public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PATH_INFO { public PATH_SOURCE_INFO sourceInfo; public PATH_TARGET_INFO targetInfo; public uint flags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MODE_INFO
    {
        public uint infoType; public uint id; public LuidValue adapterId;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 48)] public byte[] union;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVICE_INFO_HEADER { public uint type; public uint size; public LuidValue adapterId; public uint id; }

    [StructLayout(LayoutKind.Sequential)]
    private struct GET_ADVANCED_COLOR_INFO { public DEVICE_INFO_HEADER header; public uint value; public uint colorEncoding; public uint bitsPerColorChannel; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SDR_WHITE_LEVEL { public DEVICE_INFO_HEADER header; public uint SDRWhiteLevel; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SET_ADVANCED_COLOR_STATE { public DEVICE_INFO_HEADER header; public uint value; }

    [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);
    [DllImport("user32.dll")] private static extern int QueryDisplayConfig(uint flags, ref uint numPathArrayElements, [Out] PATH_INFO[] pathArray, ref uint numModeInfoArrayElements, [Out] MODE_INFO[] modeInfoArray, IntPtr currentTopologyId);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref GET_ADVANCED_COLOR_INFO requestPacket);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref SDR_WHITE_LEVEL requestPacket);
    [DllImport("user32.dll")] private static extern int DisplayConfigSetDeviceInfo(ref SET_ADVANCED_COLOR_STATE setPacket);

    private const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;
    private const uint DISPLAYCONFIG_PATH_ACTIVE = 0x00000001;
    private const uint DEVICE_INFO_GET_ADVANCED_COLOR_INFO = 9;
    private const uint DEVICE_INFO_SET_ADVANCED_COLOR_STATE = 10;
    private const uint DEVICE_INFO_GET_SDR_WHITE_LEVEL = 11;

    /// <summary>Enables/disables HDR (advanced color) on the given target — same effect as Win+Alt+B.</summary>
    public static bool SetHdr(LuidValue adapterId, uint targetId, bool enable)
    {
        var pkt = new SET_ADVANCED_COLOR_STATE();
        pkt.header.type = DEVICE_INFO_SET_ADVANCED_COLOR_STATE;
        pkt.header.size = (uint)Marshal.SizeOf<SET_ADVANCED_COLOR_STATE>();
        pkt.header.adapterId = adapterId;
        pkt.header.id = targetId;
        pkt.value = enable ? 1u : 0u;
        return DisplayConfigSetDeviceInfo(ref pkt) == 0;
    }

    /// <summary>Turns HDR off and back on so the system reloads the color profile. Blocks ~2 s.</summary>
    public static bool RestartHdr()
    {
        var acs = QueryAll().FirstOrDefault(a => a.Supported);
        if (acs is null) return false;
        if (acs.Enabled)
        {
            if (!SetHdr(acs.AdapterId, acs.TargetId, false)) return false;
            Thread.Sleep(1500);
        }
        return SetHdr(acs.AdapterId, acs.TargetId, true);
    }

    public static List<AdvancedColorStatus> QueryAll()
    {
        var results = new List<AdvancedColorStatus>();
        int err = GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint pathCount, out uint modeCount);
        if (err != 0) return results;

        var paths = new PATH_INFO[pathCount];
        var modes = new MODE_INFO[modeCount];
        for (int i = 0; i < modes.Length; i++) modes[i].union = new byte[48];

        err = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
        if (err != 0) return results;

        for (int i = 0; i < pathCount; i++)
        {
            var p = paths[i];
            if ((p.flags & DISPLAYCONFIG_PATH_ACTIVE) == 0) continue;

            var colorInfo = new GET_ADVANCED_COLOR_INFO();
            colorInfo.header.type = DEVICE_INFO_GET_ADVANCED_COLOR_INFO;
            colorInfo.header.size = (uint)Marshal.SizeOf<GET_ADVANCED_COLOR_INFO>();
            colorInfo.header.adapterId = p.targetInfo.adapterId;
            colorInfo.header.id = p.targetInfo.id;
            int r1 = DisplayConfigGetDeviceInfo(ref colorInfo);
            if (r1 != 0) continue;

            var white = new SDR_WHITE_LEVEL();
            white.header.type = DEVICE_INFO_GET_SDR_WHITE_LEVEL;
            white.header.size = (uint)Marshal.SizeOf<SDR_WHITE_LEVEL>();
            white.header.adapterId = p.targetInfo.adapterId;
            white.header.id = p.targetInfo.id;
            int r2 = DisplayConfigGetDeviceInfo(ref white);
            double nits = r2 == 0 ? white.SDRWhiteLevel / 1000.0 * 80.0 : double.NaN;

            results.Add(new AdvancedColorStatus(
                p.targetInfo.id,
                p.sourceInfo.id,
                p.targetInfo.adapterId,
                (colorInfo.value & 0x1) != 0,
                (colorInfo.value & 0x2) != 0,
                colorInfo.colorEncoding,
                colorInfo.bitsPerColorChannel,
                nits));
        }
        return results;
    }
}
