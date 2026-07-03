using System;
using System.Threading;
using System.Windows.Forms;
using HdrScope.Analysis;
using HdrScope.Capture;
using HdrScope.Interop;
using HdrScope.Rendering;

namespace HdrScope;

public static class SelfTest
{
    public static void Run()
    {
        Console.WriteLine("=== HdrScope self-test ===");
        foreach (var st in AdvancedColorInfo.QueryAll())
            Console.WriteLine($"Target {st.TargetId}: Supported={st.Supported} Enabled={st.Enabled} Encoding={st.ColorEncoding} Bits={st.BitsPerColorChannel} SDRWhite={st.SdrWhiteLevelNits:F0} nits");

        var screen = Screen.PrimaryScreen!;
        float[] nits = [1, 2, 4, 10, 25, 50, 100, 203, 300, 400, 500];
        Console.WriteLine($"Screen: {screen.DeviceName} {screen.Bounds}");

        using var pattern = new PatternForm(screen.Bounds, nits);
        pattern.Show();
        for (int i = 0; i < 15; i++) { Application.DoEvents(); Thread.Sleep(30); }

        try
        {
            var hmon = MonitorHelper.GetHMonitor(screen.Bounds);
            Console.WriteLine($"HMONITOR = {hmon}");
            var frame = HdrFrameCapture.CaptureMonitor(hmon);
            Console.WriteLine($"Captured {frame.Width}x{frame.Height}");

            var results = HdrAnalyzer.AnalyzePatches(frame, pattern.Patches);
            Console.WriteLine("Target\tMean\tMin\tMax\tStdDev\tDeltaPct");
            foreach (var r in results)
                Console.WriteLine($"{r.TargetNits:F0}\t{r.MeanNits:F1}\t{r.MinNits:F1}\t{r.MaxNits:F1}\t{r.StdDevNits:F2}\t{r.DeltaPercent:F1}%");

            HdrAnalyzer.SaveFalseColorPng(frame, "selftest-falsecolor.png", 500);
            Console.WriteLine("Saved selftest-falsecolor.png");
        }
        catch (Exception ex)
        {
            Console.WriteLine("SELFTEST ERROR: " + ex);
        }
        finally
        {
            pattern.Close();
        }

        Console.WriteLine("=== ICC writer test ===");
        try
        {
            var edid = Interop.Edid.ReadForMonitor("DELD175");
            Console.WriteLine(edid is null
                ? "EDID: not found"
                : $"EDID {edid.MonitorId}: R({edid.Red.x:F3},{edid.Red.y:F3}) G({edid.Green.x:F3},{edid.Green.y:F3}) B({edid.Blue.x:F3},{edid.Blue.y:F3}) W({edid.White.x:F4},{edid.White.y:F4}) HDRmax={edid.HdrMaxLuminance:F0} HDRmin={edid.HdrMinLuminance:F4}");

            var spec = new Color.Mhc2ProfileSpec
            {
                Description = "HdrScope selftest",
                MinLuminanceNits = 0.05,
                MaxLuminanceNits = 450,
                RegammaLut = Color.Tf.BuildSdrGammaFixLut(4096, 204),
                RedPrimary = edid?.Red ?? (0.64, 0.33),
                GreenPrimary = edid?.Green ?? (0.30, 0.60),
                BluePrimary = edid?.Blue ?? (0.15, 0.06),
                WhitePoint = edid?.White ?? (0.3127, 0.3290),
                LumiNits = 204,
            };
            Color.Mhc2IccWriter.Write("selftest-profile.icm", spec);
            var bytes = System.IO.File.ReadAllBytes("selftest-profile.icm");
            uint declared = (uint)((bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3]);
            string magic = System.Text.Encoding.ASCII.GetString(bytes, 36, 4);
            Console.WriteLine($"ICC written: {bytes.Length} bytes, declared {declared}, magic '{magic}'");

            // sanity: LUT monotonic and endpoints
            var lut = Color.Tf.BuildSdrGammaFixLut(4096, 204);
            bool monotonic = true;
            for (int i = 1; i < lut.Length; i++) if (lut[i] < lut[i - 1] - 1e-9) { monotonic = false; break; }
            Console.WriteLine($"LUT: [0]={lut[0]:F6} [last]={lut[^1]:F6} monotonic={monotonic}");
            // spot check: at SDR white boundary the LUT must be identity
            double pqW = Color.Tf.NitsToPq(204);
            int idx = (int)Math.Round(pqW * 4095);
            Console.WriteLine($"LUT at SDR white (pq={pqW:F4}): in={idx / 4095.0:F4} out={lut[idx]:F4} (should be ~equal)");
            // spot check shadow darkening: 2 nits input
            double pq2 = Color.Tf.NitsToPq(2.0);
            int i2 = (int)Math.Round(pq2 * 4095);
            Console.WriteLine($"LUT at 2 nits: outNits={Color.Tf.PqToNits(lut[i2]):F3} (expected ~1.5, i.e. darker shadows)");

            // DDC/CI smoke test
            var hmon2 = Interop.MonitorHelper.GetHMonitor(Screen.PrimaryScreen!.Bounds);
            using var ddc = Interop.DdcCi.Open(hmon2);
            if (ddc is null) Console.WriteLine("DDC/CI: unavailable");
            else
            {
                Console.WriteLine($"DDC/CI open: '{ddc.Description}'");
                foreach (var kv in ddc.Snapshot())
                    Console.WriteLine($"  {kv.Key}: {(kv.Value is null ? "n/a" : $"{kv.Value.Current}/{kv.Value.Maximum}")}");
                var caps = ddc.ReadCapabilities();
                Console.WriteLine($"  caps: {(caps is null ? "n/a" : caps.Length > 200 ? caps[..200] + "..." : caps)}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("ICC/DDC TEST ERROR: " + ex);
        }

        Console.WriteLine("=== Done ===");
    }
}
