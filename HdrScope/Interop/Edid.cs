using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace HdrScope.Interop;

public sealed class EdidInfo
{
    public string? MonitorId { get; init; }
    public (double x, double y) Red { get; init; }
    public (double x, double y) Green { get; init; }
    public (double x, double y) Blue { get; init; }
    public (double x, double y) White { get; init; }
    /// <summary>From CTA-861 HDR static metadata block, if present. NaN if absent.</summary>
    public double HdrMaxLuminance { get; init; } = double.NaN;
    public double HdrMaxFrameAvgLuminance { get; init; } = double.NaN;
    public double HdrMinLuminance { get; init; } = double.NaN;
}

public static class Edid
{
    /// <summary>Reads and parses the EDID of the first connected monitor matching the PNP id substring (e.g. "DELD175").</summary>
    public static EdidInfo? ReadForMonitor(string pnpIdSubstring)
    {
        using var displayKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\DISPLAY");
        if (displayKey is null) return null;

        foreach (var modelName in displayKey.GetSubKeyNames())
        {
            if (!modelName.Contains(pnpIdSubstring, StringComparison.OrdinalIgnoreCase)) continue;
            using var modelKey = displayKey.OpenSubKey(modelName);
            if (modelKey is null) continue;
            foreach (var instName in modelKey.GetSubKeyNames())
            {
                using var paramsKey = modelKey.OpenSubKey(instName + @"\Device Parameters");
                if (paramsKey?.GetValue("EDID") is byte[] edid && edid.Length >= 128)
                    return Parse(edid, modelName);
            }
        }
        return null;
    }

    public static EdidInfo Parse(byte[] edid, string monitorId)
    {
        // Standard EDID chromaticity coordinates, bytes 0x19..0x22
        int lowRxRy = edid[0x19];
        int lowGxGyBxBy = edid[0x1A];
        double Coord(int high, int lowBits) => ((high << 2) | lowBits) / 1024.0;

        var red = (Coord(edid[0x1B], (lowRxRy >> 6) & 3), Coord(edid[0x1C], (lowRxRy >> 4) & 3));
        var green = (Coord(edid[0x1D], (lowRxRy >> 2) & 3), Coord(edid[0x1E], lowRxRy & 3));
        var blue = (Coord(edid[0x1F], (lowGxGyBxBy >> 6) & 3), Coord(edid[0x20], (lowGxGyBxBy >> 4) & 3));
        var white = (Coord(edid[0x21], (lowGxGyBxBy >> 2) & 3), Coord(edid[0x22], lowGxGyBxBy & 3));

        double hdrMax = double.NaN, hdrAvg = double.NaN, hdrMin = double.NaN;

        // CTA-861 extension blocks: HDR static metadata (extended tag 6)
        int extCount = edid.Length > 126 ? edid[126] : 0;
        for (int block = 1; block <= extCount && (block + 1) * 128 <= edid.Length; block++)
        {
            int b0 = block * 128;
            if (edid[b0] != 0x02) continue; // CTA ext tag
            int dtdStart = edid[b0 + 2];
            int i = b0 + 4;
            int end = b0 + (dtdStart == 0 ? 127 : dtdStart);
            while (i < end)
            {
                int tag = (edid[i] >> 5) & 7;
                int len = edid[i] & 0x1F;
                if (tag == 7 && len >= 2 && edid[i + 1] == 6) // extended tag 6 = HDR static metadata
                {
                    if (len >= 4 && edid[i + 4] != 0) hdrMax = 50.0 * Math.Pow(2, edid[i + 4] / 32.0);
                    if (len >= 5 && edid[i + 5] != 0) hdrAvg = 50.0 * Math.Pow(2, edid[i + 5] / 32.0);
                    if (len >= 6 && !double.IsNaN(hdrMax))
                        hdrMin = hdrMax * Math.Pow(edid[i + 6] / 255.0, 2) / 100.0;
                }
                i += len + 1;
            }
        }

        return new EdidInfo
        {
            MonitorId = monitorId,
            Red = red,
            Green = green,
            Blue = blue,
            White = white,
            HdrMaxLuminance = hdrMax,
            HdrMaxFrameAvgLuminance = hdrAvg,
            HdrMinLuminance = hdrMin,
        };
    }
}
