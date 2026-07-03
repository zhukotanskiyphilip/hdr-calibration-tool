using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using HdrScope.Capture;

namespace HdrScope.Analysis;

public sealed record PatchResult(float TargetNits, float MeanNits, float MinNits, float MaxNits, float StdDevNits)
{
    public float DeltaNits => MeanNits - TargetNits;
    public float DeltaPercent => TargetNits <= 0 ? 0 : 100f * DeltaNits / TargetNits;
}

public static class HdrAnalyzer
{
    public static List<PatchResult> AnalyzePatches(HdrFrame frame, Patch[] patches, double insetX = 0.15, double insetY = 0.15)
    {
        var results = new List<PatchResult>();
        foreach (var raw in patches)
        {
            var p = PatchLayout.Inset(raw, insetX, insetY);
            double sum = 0, sumSq = 0;
            float min = float.MaxValue, max = float.MinValue;
            long n = 0;
            int x1 = Math.Clamp(p.X, 0, frame.Width - 1);
            int y1 = Math.Clamp(p.Y, 0, frame.Height - 1);
            int x2 = Math.Clamp(p.X + p.Width, 0, frame.Width);
            int y2 = Math.Clamp(p.Y + p.Height, 0, frame.Height);

            for (int y = y1; y < y2; y++)
            {
                for (int x = x1; x < x2; x++)
                {
                    float v = frame.NitsAt(x, y);
                    sum += v;
                    sumSq += (double)v * v;
                    if (v < min) min = v;
                    if (v > max) max = v;
                    n++;
                }
            }

            double mean = n > 0 ? sum / n : 0;
            double variance = n > 0 ? Math.Max(0, sumSq / n - mean * mean) : 0;
            results.Add(new PatchResult(raw.TargetNits, (float)mean, n > 0 ? min : 0, n > 0 ? max : 0, (float)Math.Sqrt(variance)));
        }
        return results;
    }

    public static int[] BuildHistogram(HdrFrame frame, int buckets, float maxNits)
    {
        var hist = new int[buckets];
        float scale = buckets / maxNits;
        foreach (var v in frame.Nits)
        {
            int idx = (int)(Math.Clamp(v, 0, maxNits) * scale);
            if (idx >= buckets) idx = buckets - 1;
            hist[idx]++;
        }
        return hist;
    }

    public static void SavePatchReportCsv(string path, List<PatchResult> results)
    {
        var sb = new StringBuilder();
        sb.AppendLine("TargetNits,MeanNits,MinNits,MaxNits,StdDevNits,DeltaNits,DeltaPercent");
        foreach (var r in results)
            sb.AppendLine($"{r.TargetNits:F2},{r.MeanNits:F2},{r.MinNits:F2},{r.MaxNits:F2},{r.StdDevNits:F2},{r.DeltaNits:F2},{r.DeltaPercent:F1}");
        File.WriteAllText(path, sb.ToString());
    }

    public static void SaveHistogramCsv(string path, int[] histogram, float maxNits)
    {
        var sb = new StringBuilder();
        sb.AppendLine("NitsBucketStart,PixelCount");
        float step = maxNits / histogram.Length;
        for (int i = 0; i < histogram.Length; i++)
            sb.AppendLine($"{i * step:F1},{histogram[i]}");
        File.WriteAllText(path, sb.ToString());
    }

    public static void SaveFalseColorPng(HdrFrame frame, string path, float maxNits)
    {
        using var bmp = new Bitmap(frame.Width, frame.Height, PixelFormat.Format24bppRgb);
        var rect = new Rectangle(0, 0, frame.Width, frame.Height);
        var data = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        unsafe
        {
            for (int y = 0; y < frame.Height; y++)
            {
                byte* row = (byte*)data.Scan0 + y * data.Stride;
                for (int x = 0; x < frame.Width; x++)
                {
                    float t = Math.Clamp(frame.NitsAt(x, y) / maxNits, 0f, 1f);
                    var (r, g, b) = HeatColor(t);
                    int o = x * 3;
                    row[o + 0] = b;
                    row[o + 1] = g;
                    row[o + 2] = r;
                }
            }
        }
        bmp.UnlockBits(data);
        bmp.Save(path, ImageFormat.Png);
    }

    private static (byte R, byte G, byte B) HeatColor(float t)
    {
        // 0=black, 0.25=blue, 0.5=green, 0.75=yellow, 1=red (clipping)
        float r, g, b;
        if (t < 0.25f) { float u = t / 0.25f; r = 0; g = 0; b = u; }
        else if (t < 0.5f) { float u = (t - 0.25f) / 0.25f; r = 0; g = u; b = 1 - u; }
        else if (t < 0.75f) { float u = (t - 0.5f) / 0.25f; r = u; g = 1; b = 0; }
        else { float u = (t - 0.75f) / 0.25f; r = 1; g = 1 - u; b = 0; }
        return ((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
    }
}
