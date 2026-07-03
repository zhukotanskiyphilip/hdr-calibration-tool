using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace HdrScope.Rendering;

/// <summary>
/// Plain GDI (SDR-composited) fullscreen pattern window. Used for tests that must go
/// through the normal SDR path: checkerboard gamma matching, grayscale tint fields,
/// near-white contrast clipping bars.
/// </summary>
public sealed class SdrPatternForm : Form
{
    public enum Mode { CheckerboardMatch, SolidGray, ContrastBars }

    public Mode CurrentMode { get; private set; } = Mode.SolidGray;

    /// <summary>Checker density: fraction of white pixels (0.25 / 0.5 / 0.75).</summary>
    public double CheckerDensity { get; private set; } = 0.5;

    /// <summary>Adjustable solid gray code 0..255 (the value the user tunes to match).</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int MatchCode { get; set; } = 128;

    /// <summary>Gray code for SolidGray mode.</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int SolidCode { get; set; } = 128;

    public SdrPatternForm(Rectangle screenBounds)
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = screenBounds;
        TopMost = true;
        BackColor = System.Drawing.Color.Black;
        ShowInTaskbar = false;
        DoubleBuffered = true;
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };
    }

    public void SetCheckerboard(double density, int initialMatchCode)
    {
        CurrentMode = Mode.CheckerboardMatch;
        CheckerDensity = density;
        MatchCode = initialMatchCode;
        Invalidate();
    }

    public void SetSolidGray(int code)
    {
        CurrentMode = Mode.SolidGray;
        SolidCode = code;
        Invalidate();
    }

    public void SetContrastBars()
    {
        CurrentMode = Mode.ContrastBars;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        int w = ClientSize.Width, h = ClientSize.Height;

        switch (CurrentMode)
        {
            case Mode.SolidGray:
            {
                using var b = new SolidBrush(System.Drawing.Color.FromArgb(SolidCode, SolidCode, SolidCode));
                g.FillRectangle(b, 0, 0, w, h);
                break;
            }
            case Mode.CheckerboardMatch:
            {
                g.FillRectangle(Brushes.Black, 0, 0, w, h);
                // Left half: checkerboard tile (2x2 px cells); right half: solid MatchCode.
                using var tile = BuildCheckerTile(CheckerDensity);
                using var brush = new TextureBrush(tile, WrapMode.Tile);
                int fieldW = w * 3 / 10, fieldH = h * 4 / 10;
                int cy = (h - fieldH) / 2;
                g.FillRectangle(brush, w / 2 - fieldW, cy, fieldW, fieldH);
                using var solid = new SolidBrush(System.Drawing.Color.FromArgb(MatchCode, MatchCode, MatchCode));
                g.FillRectangle(solid, w / 2, cy, fieldW, fieldH);
                break;
            }
            case Mode.ContrastBars:
            {
                g.FillRectangle(Brushes.White, 0, 0, w, h);
                // Vertical bars 250..254 on a 255 background
                int n = 5;
                int barW = w / 12;
                int gap = barW / 2;
                int total = n * barW + (n - 1) * gap;
                int x = (w - total) / 2;
                for (int i = 0; i < n; i++)
                {
                    int code = 250 + i;
                    using var b = new SolidBrush(System.Drawing.Color.FromArgb(code, code, code));
                    g.FillRectangle(b, x, h / 4, barW, h / 2);
                    x += barW + gap;
                }
                break;
            }
        }
    }

    private static Bitmap BuildCheckerTile(double density)
    {
        // 4x4 px tile of 2x2 blocks: 1..3 of 4 blocks white depending on density
        var bmp = new Bitmap(4, 4, PixelFormat.Format24bppRgb);
        int whiteBlocks = density switch { <= 0.3 => 1, >= 0.7 => 3, _ => 2 };
        bool[] blockWhite = whiteBlocks switch
        {
            1 => [true, false, false, false],
            3 => [true, true, true, false],
            _ => [true, false, false, true],
        };
        for (int by = 0; by < 2; by++)
            for (int bx = 0; bx < 2; bx++)
            {
                var c = blockWhite[by * 2 + bx] ? System.Drawing.Color.White : System.Drawing.Color.Black;
                for (int dy = 0; dy < 2; dy++)
                    for (int dx = 0; dx < 2; dx++)
                        bmp.SetPixel(bx * 2 + dx, by * 2 + dy, c);
            }
        return bmp;
    }
}
