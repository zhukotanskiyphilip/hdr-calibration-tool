using System;

namespace HdrScope.Color;

public static class Tf
{
    // ST 2084 (PQ) constants
    private const double M1 = 0.1593017578125;
    private const double M2 = 78.84375;
    private const double C1 = 0.8359375;
    private const double C2 = 18.8515625;
    private const double C3 = 18.6875;

    /// <summary>PQ signal [0..1] -> absolute nits [0..10000].</summary>
    public static double PqToNits(double e)
    {
        if (e <= 0) return 0;
        double p = Math.Pow(e, 1.0 / M2);
        double num = Math.Max(p - C1, 0);
        double den = C2 - C3 * p;
        return 10000.0 * Math.Pow(num / den, 1.0 / M1);
    }

    /// <summary>Absolute nits [0..10000] -> PQ signal [0..1].</summary>
    public static double NitsToPq(double nits)
    {
        if (nits <= 0) return 0;
        double y = Math.Pow(nits / 10000.0, M1);
        return Math.Pow((C1 + C2 * y) / (1 + C3 * y), M2);
    }

    /// <summary>Piecewise sRGB EOTF decode: code [0..1] -> linear [0..1].</summary>
    public static double SrgbDecode(double c)
    {
        if (c <= 0) return 0;
        if (c >= 1) return 1;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    /// <summary>Piecewise sRGB encode: linear [0..1] -> code [0..1].</summary>
    public static double SrgbEncode(double l)
    {
        if (l <= 0) return 0;
        if (l >= 1) return 1;
        return l <= 0.0031308 ? l * 12.92 : 1.055 * Math.Pow(l, 1.0 / 2.4) - 0.055;
    }

    /// <summary>
    /// Builds the scanout regamma LUT (PQ domain, N entries in [0..1]) that re-maps
    /// Windows' piecewise-sRGB handling of SDR content to a pure power gamma
    /// below the SDR white level. Above SDR white the mapping is identity
    /// (slope at the joint is ~1.02, visually continuous).
    /// </summary>
    public static double[] BuildSdrGammaFixLut(int size, double sdrWhiteNits, double targetGamma = 2.2)
    {
        var lut = new double[size];
        for (int i = 0; i < size; i++)
        {
            double pqIn = (double)i / (size - 1);
            double nits = PqToNits(pqIn);
            double nitsOut;
            if (nits < sdrWhiteNits)
            {
                double code = SrgbEncode(nits / sdrWhiteNits);
                nitsOut = sdrWhiteNits * Math.Pow(code, targetGamma);
            }
            else
            {
                nitsOut = nits;
            }
            lut[i] = NitsToPq(nitsOut);
        }
        return lut;
    }

    /// <summary>
    /// Competitive shadow-lift LUT (PQ domain): below kneeNits applies a power
    /// curve (strength &lt; 1 brightens shadows), identity above. A software
    /// equivalent of the monitor's "Dark Stabilizer" for HDR mode, where the
    /// OSD control is locked.
    /// </summary>
    public static double[] BuildShadowLiftLut(int size, double kneeNits = 25, double strength = 0.75)
    {
        var lut = new double[size];
        for (int i = 0; i < size; i++)
        {
            double pqIn = (double)i / (size - 1);
            double nits = PqToNits(pqIn);
            double nitsOut = nits < kneeNits
                ? kneeNits * Math.Pow(nits / kneeNits, strength)
                : nits;
            lut[i] = NitsToPq(nitsOut);
        }
        return lut;
    }

    public static double[] BuildIdentityLut(int size)
    {
        var lut = new double[size];
        for (int i = 0; i < size; i++) lut[i] = (double)i / (size - 1);
        return lut;
    }
}

public static class ColorMatrix
{
    // Bradford cone response matrix and its inverse
    private static readonly double[,] MA =
    {
        { 0.8951,  0.2664, -0.1614 },
        { -0.7502, 1.7135,  0.0367 },
        { 0.0389, -0.0685,  1.0296 },
    };

    public static (double X, double Y, double Z) XyToXyz(double x, double y)
        => (x / y, 1.0, (1 - x - y) / y);

    public static double[,] Multiply(double[,] a, double[,] b)
    {
        var r = new double[3, 3];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                for (int k = 0; k < 3; k++)
                    r[i, j] += a[i, k] * b[k, j];
        return r;
    }

    public static double[] Apply(double[,] m, double[] v)
    {
        var r = new double[3];
        for (int i = 0; i < 3; i++)
            for (int k = 0; k < 3; k++)
                r[i] += m[i, k] * v[k];
        return r;
    }

    public static double[,] Invert(double[,] m)
    {
        double a = m[0, 0], b = m[0, 1], c = m[0, 2];
        double d = m[1, 0], e = m[1, 1], f = m[1, 2];
        double g = m[2, 0], h = m[2, 1], i = m[2, 2];
        double det = a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
        var inv = new double[3, 3]
        {
            { (e * i - f * h) / det, (c * h - b * i) / det, (b * f - c * e) / det },
            { (f * g - d * i) / det, (a * i - c * g) / det, (c * d - a * f) / det },
            { (d * h - e * g) / det, (b * g - a * h) / det, (a * e - b * d) / det },
        };
        return inv;
    }

    /// <summary>Bradford chromatic adaptation matrix from source white XYZ to dest white XYZ.</summary>
    public static double[,] BradfordAdaptation((double X, double Y, double Z) srcWhite, (double X, double Y, double Z) dstWhite)
    {
        var src = Apply(MA, [srcWhite.X, srcWhite.Y, srcWhite.Z]);
        var dst = Apply(MA, [dstWhite.X, dstWhite.Y, dstWhite.Z]);
        var scale = new double[3, 3];
        scale[0, 0] = dst[0] / src[0];
        scale[1, 1] = dst[1] / src[1];
        scale[2, 2] = dst[2] / src[2];
        return Multiply(Invert(MA), Multiply(scale, MA));
    }

    /// <summary>RGB->XYZ matrix from primaries and white point (all CIE xy).</summary>
    public static double[,] RgbToXyz(
        (double x, double y) r, (double x, double y) g, (double x, double y) b, (double x, double y) w)
    {
        var xr = XyToXyz(r.x, r.y);
        var xg = XyToXyz(g.x, g.y);
        var xb = XyToXyz(b.x, b.y);
        var xw = XyToXyz(w.x, w.y);
        var m = new double[3, 3]
        {
            { xr.X, xg.X, xb.X },
            { xr.Y, xg.Y, xb.Y },
            { xr.Z, xg.Z, xb.Z },
        };
        var s = Apply(Invert(m), [xw.X, xw.Y, xw.Z]);
        var res = new double[3, 3];
        for (int i = 0; i < 3; i++)
        {
            res[i, 0] = m[i, 0] * s[0];
            res[i, 1] = m[i, 1] * s[1];
            res[i, 2] = m[i, 2] * s[2];
        }
        return res;
    }

    public static readonly (double X, double Y, double Z) D50 = (0.96422, 1.0, 0.82521);
    public static readonly (double X, double Y, double Z) D65 = (0.95047, 1.0, 1.08883);
}
