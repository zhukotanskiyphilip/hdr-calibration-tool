using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using HdrScope.Analysis;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace HdrScope.Rendering;

/// <summary>A rectangle in normalized [0..1] screen coordinates with a linear scRGB color (1.0 = 80 nits).</summary>
public sealed record ScenePatch(double X, double Y, double W, double H, float R, float G, float B)
{
    public static ScenePatch Gray(double x, double y, double w, double h, double nits)
    {
        float v = (float)(nits / 80.0);
        return new ScenePatch(x, y, w, h, v, v, v);
    }
}

public sealed class PatternForm : Form
{
    private ID3D11Device? _device;
    private ID3D11DeviceContext1? _context1;
    private IDXGISwapChain1? _swapChain;
    private ID3D11RenderTargetView? _rtv;

    private List<ScenePatch> _scene = new();
    private float _bgR, _bgG, _bgB;

    public Patch[] Patches { get; private set; } = [];

    private readonly Rectangle _targetBounds;

    public PatternForm(Rectangle screenBounds)
    {
        _targetBounds = screenBounds;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = screenBounds;
        TopMost = true;
        BackColor = System.Drawing.Color.Black;
        ShowInTaskbar = false;
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // DPI virtualization can shift/scale the window after creation; force exact physical bounds
        if (Bounds != _targetBounds)
        {
            Bounds = _targetBounds;
            ResizeSwapChainToClient();
        }
        Render();
    }

    private void ResizeSwapChainToClient()
    {
        if (_swapChain is null || _device is null) return;
        var desc = _swapChain.Description1;
        if (desc.Width == (uint)ClientSize.Width && desc.Height == (uint)ClientSize.Height) return;
        _rtv?.Dispose();
        _swapChain.ResizeBuffers(2, (uint)ClientSize.Width, (uint)ClientSize.Height, Vortice.DXGI.Format.R16G16B16A16_Float, Vortice.DXGI.SwapChainFlags.None);
        using var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        _rtv = _device.CreateRenderTargetView(backBuffer);
    }

    /// <summary>Legacy constructor: vertical gray columns at the given nits levels.</summary>
    public PatternForm(Rectangle screenBounds, float[] targetNits) : this(screenBounds)
    {
        var scene = new List<ScenePatch>();
        double colW = 1.0 / targetNits.Length;
        for (int i = 0; i < targetNits.Length; i++)
            scene.Add(ScenePatch.Gray(i * colW, 0, colW, 1, targetNits[i]));
        _scene = scene;
        _legacyNits = targetNits;
    }

    private float[]? _legacyNits;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        InitD3D();
        Render();
    }

    public void SetScene(IEnumerable<ScenePatch> patches, double bgNits = 0)
    {
        _scene = new List<ScenePatch>(patches);
        float bg = (float)(bgNits / 80.0);
        _bgR = _bgG = _bgB = bg;
        if (_device is not null) Render();
    }

    private void InitD3D()
    {
        FeatureLevel[] levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0];
        D3D11.D3D11CreateDevice(
            null,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            levels,
            out ID3D11Device device).CheckError();
        _device = device;
        _context1 = device.ImmediateContext.QueryInterface<ID3D11DeviceContext1>();

        using var dxgiDevice = device.QueryInterface<IDXGIDevice1>();
        using var adapter = dxgiDevice.GetAdapter();
        using var factory = adapter.GetParent<IDXGIFactory2>();

        var desc = new SwapChainDescription1
        {
            Width = (uint)ClientSize.Width,
            Height = (uint)ClientSize.Height,
            Format = Format.R16G16B16A16_Float,
            Stereo = false,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipDiscard,
            AlphaMode = Vortice.DXGI.AlphaMode.Ignore,
            Flags = SwapChainFlags.None,
        };

        _swapChain = factory.CreateSwapChainForHwnd(device, Handle, desc);

        try
        {
            using var swapChain3 = _swapChain.QueryInterface<IDXGISwapChain3>();
            swapChain3.SetColorSpace1(ColorSpaceType.RgbFullG10NoneP709);
        }
        catch
        {
            // Non-fatal: DWM will assume a default color space.
        }

        using var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        _rtv = _device.CreateRenderTargetView(backBuffer);
    }

    public void Render()
    {
        if (_device is null || _context1 is null || _rtv is null || _swapChain is null) return;

        int w = ClientSize.Width, h = ClientSize.Height;
        _context1.ClearRenderTargetView(_rtv, new Color4(_bgR, _bgG, _bgB, 1));

        var analysisPatches = new List<Patch>();
        foreach (var p in _scene)
        {
            int x1 = (int)(p.X * w), y1 = (int)(p.Y * h);
            int x2 = (int)((p.X + p.W) * w), y2 = (int)((p.Y + p.H) * h);
            // RectI is (x, y, width, height) — NOT left/top/right/bottom
            var rect = new RectI(x1, y1, x2 - x1, y2 - y1);
            _context1.ClearView(_rtv, new Color4(p.R, p.G, p.B, 1), [rect]);
            analysisPatches.Add(new Patch(x1, y1, x2 - x1, y2 - y1, p.R * 80.0f));
        }
        Patches = analysisPatches.ToArray();

        _swapChain.Present(1, PresentFlags.None);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _rtv?.Dispose();
            _swapChain?.Dispose();
            _context1?.Dispose();
            _device?.Dispose();
        }
        base.Dispose(disposing);
    }
}
