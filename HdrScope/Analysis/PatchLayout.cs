namespace HdrScope.Analysis;

public readonly record struct Patch(int X, int Y, int Width, int Height, float TargetNits);

public static class PatchLayout
{
    /// <summary>Evenly divides the given area into vertical columns, one per target nits value.</summary>
    public static Patch[] BuildColumns(int areaWidth, int areaHeight, float[] targetNits)
    {
        var patches = new Patch[targetNits.Length];
        int colWidth = areaWidth / targetNits.Length;
        for (int i = 0; i < targetNits.Length; i++)
        {
            int x = i * colWidth;
            int w = (i == targetNits.Length - 1) ? areaWidth - x : colWidth;
            patches[i] = new Patch(x, 0, w, areaHeight, targetNits[i]);
        }
        return patches;
    }

    /// <summary>Shrinks a patch rect inward to avoid edge/antialiasing/border contamination when sampling.</summary>
    public static Patch Inset(Patch p, double fractionX, double fractionY)
    {
        int insetX = (int)(p.Width * fractionX);
        int insetY = (int)(p.Height * fractionY);
        return p with
        {
            X = p.X + insetX,
            Y = p.Y + insetY,
            Width = p.Width - 2 * insetX,
            Height = p.Height - 2 * insetY,
        };
    }
}
