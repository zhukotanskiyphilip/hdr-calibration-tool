using System;
using System.Threading;
using HdrScope.Interop;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;

namespace HdrScope.Capture;

public sealed class HdrFrame
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    /// <summary>Luminance in nits (cd/m^2) per pixel, row-major.</summary>
    public required float[] Nits { get; init; }

    public float NitsAt(int x, int y) => Nits[y * Width + x];
}

public static class HdrFrameCapture
{
    public static HdrFrame CaptureMonitor(IntPtr hmonitor, TimeSpan timeout = default)
    {
        if (timeout == default) timeout = TimeSpan.FromSeconds(2);

        var (device, context, winrtDevice) = Direct3D11Interop.CreateSharedDevice();
        using var _device = device;
        using var _context = context;

        var item = MonitorCaptureItem.CreateForMonitor(hmonitor);

        using var framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            winrtDevice,
            DirectXPixelFormat.R16G16B16A16Float,
            1,
            item.Size);

        using var session = framePool.CreateCaptureSession(item);
        try { session.IsCursorCaptureEnabled = false; } catch { /* not supported on this OS build */ }
        try { session.IsBorderRequired = false; } catch { /* not supported on this OS build */ }

        session.StartCapture();

        Direct3D11CaptureFrame? frame = null;
        var start = DateTime.UtcNow;
        while (frame is null)
        {
            frame = framePool.TryGetNextFrame();
            if (frame is null)
            {
                if (DateTime.UtcNow - start > timeout)
                    throw new TimeoutException("Не вдалося отримати кадр захоплення від Windows.Graphics.Capture.");
                Thread.Sleep(15);
            }
        }

        using (frame)
        {
            using var capturedTexture = Direct3D11Interop.GetTexture2DFromSurface(frame.Surface);
            var desc = capturedTexture.Description;

            var stagingDesc = new Texture2DDescription
            {
                Width = desc.Width,
                Height = desc.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = desc.Format,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
                MiscFlags = ResourceOptionFlags.None,
            };

            using var staging = device.CreateTexture2D(stagingDesc);
            context.CopyResource(staging, capturedTexture);

            var mapped = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            var nits = new float[desc.Width * desc.Height];
            unsafe
            {
                byte* basePtr = (byte*)mapped.DataPointer;
                for (int y = 0; y < desc.Height; y++)
                {
                    Half* row = (Half*)(basePtr + y * mapped.RowPitch);
                    for (int x = 0; x < desc.Width; x++)
                    {
                        float r = (float)row[x * 4 + 0];
                        float g = (float)row[x * 4 + 1];
                        float b = (float)row[x * 4 + 2];
                        float luma = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                        nits[y * desc.Width + x] = luma * 80.0f;
                    }
                }
            }
            context.Unmap(staging, 0);

            return new HdrFrame { Width = (int)desc.Width, Height = (int)desc.Height, Nits = nits };
        }
    }
}
