using System;
using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using WinRT;

namespace HdrScope.Interop;

internal static class MonitorCaptureItem
{
    [DllImport("combase.dll", ExactSpelling = true, PreserveSig = false)]
    private static extern void RoGetActivationFactory(IntPtr activatableClassId, [In] ref Guid iid, out IntPtr factory);

    [DllImport("combase.dll", ExactSpelling = true, PreserveSig = false)]
    private static extern void WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string sourceString, int length, out IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsDeleteString(IntPtr hstring);

    public static GraphicsCaptureItem CreateForMonitor(IntPtr hmonitor)
    {
        const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";
        WindowsCreateString(className, className.Length, out IntPtr classNameHString);

        Guid interopIid = typeof(IGraphicsCaptureItemInterop).GUID;
        IntPtr factoryPtr;
        try
        {
            RoGetActivationFactory(classNameHString, ref interopIid, out factoryPtr);
        }
        finally
        {
            WindowsDeleteString(classNameHString);
        }

        var interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factoryPtr);
        Marshal.Release(factoryPtr);

        Guid itemIid = KnownGuids.GraphicsCaptureItem;
        IntPtr itemPtr = interop.CreateForMonitor(hmonitor, ref itemIid);
        var item = MarshalInterface<GraphicsCaptureItem>.FromAbi(itemPtr);
        Marshal.Release(itemPtr);
        return item;
    }
}
