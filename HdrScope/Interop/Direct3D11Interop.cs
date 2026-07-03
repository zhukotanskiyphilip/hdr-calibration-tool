using System;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace HdrScope.Interop;

internal static class Direct3D11Interop
{
    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice", SetLastError = true, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11SurfaceFromDXGISurface", SetLastError = true, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    private static extern int CreateDirect3D11SurfaceFromDXGISurface(IntPtr dxgiSurface, out IntPtr graphicsSurface);

    public static (ID3D11Device Device, ID3D11DeviceContext Context, IDirect3DDevice WinRtDevice) CreateSharedDevice()
    {
        FeatureLevel[] levels =
        [
            FeatureLevel.Level_11_1,
            FeatureLevel.Level_11_0,
        ];

        D3D11.D3D11CreateDevice(
            null,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            levels,
            out ID3D11Device device).CheckError();

        var context = device.ImmediateContext;

        using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
        int hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out IntPtr pUnknown);
        Marshal.ThrowExceptionForHR(hr);
        var winrtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(pUnknown);
        Marshal.Release(pUnknown);

        return (device, context, winrtDevice);
    }

    public static ID3D11Texture2D GetTexture2DFromSurface(IDirect3DSurface surface)
    {
        var access = surface.As<IDirect3DDxgiInterfaceAccess>();
        Guid iid = KnownGuids.ID3D11Texture2D;
        IntPtr texturePtr = access.GetInterface(ref iid);
        var texture = new ID3D11Texture2D(texturePtr);
        Marshal.Release(texturePtr);
        return texture;
    }
}
