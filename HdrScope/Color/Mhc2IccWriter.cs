using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace HdrScope.Color;

public sealed class Mhc2ProfileSpec
{
    public required string Description { get; init; }
    public double MinLuminanceNits { get; init; } = 0.0;
    public double MaxLuminanceNits { get; init; } = 400;
    /// <summary>False = plain display profile without the MHC2 tag (for SDR association).</summary>
    public bool IncludeMhc2 { get; init; } = true;
    /// <summary>Regamma LUT in PQ domain, values [0..1]; same LUT applied to R,G,B. Null = identity 2-point.</summary>
    public double[]? RegammaLut { get; init; }
    /// <summary>3x3 color matrix (row-major); null = identity.</summary>
    public double[,]? Matrix { get; init; }
    public (double x, double y) RedPrimary { get; init; } = (0.640, 0.330);
    public (double x, double y) GreenPrimary { get; init; } = (0.300, 0.600);
    public (double x, double y) BluePrimary { get; init; } = (0.150, 0.060);
    public (double x, double y) WhitePoint { get; init; } = (0.3127, 0.3290);
    /// <summary>ICC lumi tag value (nits of reference white).</summary>
    public double LumiNits { get; init; } = 203;
}

/// <summary>
/// Minimal ICC v4 display profile writer with the Microsoft MHC2 tag
/// (Windows Advanced Color scanout calibration: min/max luminance + matrix + regamma LUTs).
/// Layout per dantmnf/MHC2 pipeline.md.
/// </summary>
public static class Mhc2IccWriter
{
    private static void WriteU32(List<byte> b, uint v)
    {
        b.Add((byte)(v >> 24)); b.Add((byte)(v >> 16)); b.Add((byte)(v >> 8)); b.Add((byte)v);
    }

    private static void WriteU16(List<byte> b, ushort v) { b.Add((byte)(v >> 8)); b.Add((byte)v); }

    private static void WriteTag(List<byte> b, string sig)
    {
        foreach (char c in sig) b.Add((byte)c);
    }

    private static int ToS15F16(double v) => (int)Math.Round(v * 65536.0);

    private static void WriteS15F16(List<byte> b, double v) => WriteU32(b, unchecked((uint)ToS15F16(v)));

    private static byte[] XyzTag(double x, double y, double z)
    {
        var b = new List<byte>();
        WriteTag(b, "XYZ "); WriteU32(b, 0);
        WriteS15F16(b, x); WriteS15F16(b, y); WriteS15F16(b, z);
        return b.ToArray();
    }

    private static byte[] MlucTag(string text)
    {
        var b = new List<byte>();
        WriteTag(b, "mluc"); WriteU32(b, 0);
        WriteU32(b, 1);      // record count
        WriteU32(b, 12);     // record size
        WriteTag(b, "enUS");
        var utf16 = Encoding.BigEndianUnicode.GetBytes(text);
        WriteU32(b, (uint)utf16.Length);
        WriteU32(b, 28);     // offset from tag start
        b.AddRange(utf16);
        return b.ToArray();
    }

    private static byte[] ParaGammaTag(double gamma)
    {
        var b = new List<byte>();
        WriteTag(b, "para"); WriteU32(b, 0);
        WriteU16(b, 0);  // curve type 0: Y = X^g
        WriteU16(b, 0);
        WriteS15F16(b, gamma);
        return b.ToArray();
    }

    private static byte[] ChadTag(double[,] m)
    {
        var b = new List<byte>();
        WriteTag(b, "sf32"); WriteU32(b, 0);
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                WriteS15F16(b, m[i, j]);
        return b.ToArray();
    }

    private static byte[] Mhc2Tag(Mhc2ProfileSpec spec)
    {
        double[] lut = spec.RegammaLut ?? [0.0, 1.0];
        int lutSize = lut.Length;

        var b = new List<byte>();
        WriteTag(b, "MHC2"); WriteU32(b, 0);
        WriteU32(b, (uint)lutSize);
        WriteS15F16(b, spec.MinLuminanceNits);
        WriteS15F16(b, spec.MaxLuminanceNits);

        const int headerSize = 36; // through the 4 offsets
        int matrixOffset = headerSize;
        int matrixSize = 12 * 4;
        int lutBlockSize = 8 + lutSize * 4; // 'sf32' + reserved + entries
        int lut0 = matrixOffset + matrixSize;
        int lut1 = lut0 + lutBlockSize;
        int lut2 = lut1 + lutBlockSize;

        WriteU32(b, (uint)matrixOffset);
        WriteU32(b, (uint)lut0);
        WriteU32(b, (uint)lut1);
        WriteU32(b, (uint)lut2);

        var m = spec.Matrix;
        for (int row = 0; row < 3; row++)
        {
            for (int col = 0; col < 3; col++)
                WriteS15F16(b, m is null ? (row == col ? 1.0 : 0.0) : m[row, col]);
            WriteS15F16(b, 0.0); // offset column
        }

        for (int ch = 0; ch < 3; ch++)
        {
            WriteTag(b, "sf32"); WriteU32(b, 0);
            foreach (var v in lut) WriteS15F16(b, v);
        }

        return b.ToArray();
    }

    public static void Write(string path, Mhc2ProfileSpec spec)
    {
        // Primaries: RGB->XYZ at native white, Bradford-adapted to D50 (ICC PCS)
        var rgbToXyz = ColorMatrix.RgbToXyz(spec.RedPrimary, spec.GreenPrimary, spec.BluePrimary, spec.WhitePoint);
        var nativeWhite = ColorMatrix.XyToXyz(spec.WhitePoint.x, spec.WhitePoint.y);
        var chad = ColorMatrix.BradfordAdaptation(nativeWhite, ColorMatrix.D50);
        var adapted = ColorMatrix.Multiply(chad, rgbToXyz);

        var trc = ParaGammaTag(2.2);
        var tags = new List<(string sig, byte[] data)>
        {
            ("desc", MlucTag(spec.Description)),
            ("cprt", MlucTag("HdrScope generated. No copyright.")),
            ("wtpt", XyzTag(ColorMatrix.D50.X, ColorMatrix.D50.Y, ColorMatrix.D50.Z)),
            ("chad", ChadTag(chad)),
            ("rXYZ", XyzTag(adapted[0, 0], adapted[1, 0], adapted[2, 0])),
            ("gXYZ", XyzTag(adapted[0, 1], adapted[1, 1], adapted[2, 1])),
            ("bXYZ", XyzTag(adapted[0, 2], adapted[1, 2], adapted[2, 2])),
            ("rTRC", trc),
            ("gTRC", trc),
            ("bTRC", trc),
            ("lumi", XyzTag(0, spec.LumiNits, 0)),
        };
        if (spec.IncludeMhc2) tags.Add(("MHC2", Mhc2Tag(spec)));

        int headerSize = 128;
        int tagTableSize = 4 + tags.Count * 12;
        int offset = headerSize + tagTableSize;
        var placements = new List<(string sig, int offset, int size)>();
        foreach (var (sig, data) in tags)
        {
            placements.Add((sig, offset, data.Length));
            offset += (data.Length + 3) & ~3;
        }
        uint totalSize = (uint)offset;

        var f = new List<byte>((int)totalSize);
        // --- header ---
        WriteU32(f, totalSize);
        WriteU32(f, 0);                    // CMM
        WriteU32(f, 0x04300000);           // version 4.3
        WriteTag(f, "mntr");
        WriteTag(f, "RGB ");
        WriteTag(f, "XYZ ");
        var now = DateTime.UtcNow;
        WriteU16(f, (ushort)now.Year); WriteU16(f, (ushort)now.Month); WriteU16(f, (ushort)now.Day);
        WriteU16(f, (ushort)now.Hour); WriteU16(f, (ushort)now.Minute); WriteU16(f, (ushort)now.Second);
        WriteTag(f, "acsp");
        WriteTag(f, "MSFT");
        WriteU32(f, 0);                    // flags
        WriteU32(f, 0);                    // manufacturer
        WriteU32(f, 0);                    // model
        WriteU32(f, 0); WriteU32(f, 0);    // attributes
        WriteU32(f, 0);                    // rendering intent: perceptual
        WriteS15F16(f, ColorMatrix.D50.X); WriteS15F16(f, ColorMatrix.D50.Y); WriteS15F16(f, ColorMatrix.D50.Z);
        WriteU32(f, 0);                    // creator
        for (int i = 0; i < 44; i++) f.Add(0); // profile ID (16) + reserved (28)

        if (f.Count != headerSize) throw new InvalidOperationException($"ICC header size mismatch: {f.Count}");

        // --- tag table ---
        WriteU32(f, (uint)tags.Count);
        for (int i = 0; i < tags.Count; i++)
        {
            WriteTag(f, placements[i].sig);
            WriteU32(f, (uint)placements[i].offset);
            WriteU32(f, (uint)placements[i].size);
        }

        // --- tag data (4-byte aligned) ---
        foreach (var (_, data) in tags)
        {
            f.AddRange(data);
            while (f.Count % 4 != 0) f.Add(0);
        }

        if (f.Count != totalSize) throw new InvalidOperationException($"ICC size mismatch: {f.Count} vs {totalSize}");
        File.WriteAllBytes(path, f.ToArray());
    }
}
