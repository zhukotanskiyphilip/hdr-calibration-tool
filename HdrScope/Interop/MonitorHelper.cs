using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace HdrScope.Interop;

public static class MonitorHelper
{
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref RECT lprc, uint dwFlags);

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    public static IntPtr GetHMonitor(Rectangle bounds)
    {
        var rect = new RECT { Left = bounds.Left, Top = bounds.Top, Right = bounds.Right, Bottom = bounds.Bottom };
        return MonitorFromRect(ref rect, MONITOR_DEFAULTTONEAREST);
    }
}
