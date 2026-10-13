using System.Runtime.InteropServices;
using KovaaksCompanion.Core.Diagnostics;

namespace KovaaksCompanion.Core.Video.Obs;

/// <summary>
/// Picks the DXGI adapter libobs composites on. Index 0 is not always the real GPU (virtual display drivers such as Sunshine/IDD
/// can enumerate first), and a window capture from another GPU than the game's renders black.
/// </summary>
internal static unsafe class DxgiAdapters
{
    [DllImport("dxgi.dll")] static extern int CreateDXGIFactory1(in Guid riid, out nint factory);

    static readonly Guid IidFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    const uint VendorMicrosoft = 0x1414;

    /// <summary>Index (as libobs counts adapters) of the hardware adapter with the most dedicated video memory; 0 when DXGI cannot be queried.</summary>
    public static int PickBest()
    {
        var best = 0; ulong bestMem = 0;
        var desc = stackalloc byte[304];
        try
        {
            if (CreateDXGIFactory1(IidFactory1, out var factory) < 0 || factory == 0) return 0;
            try
            {
                for (uint i = 0; ; i++)
                {
                    nint adapter;
                    // IDXGIFactory1::EnumAdapters1 is vtable slot 12
                    if (((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)(*(nint**)factory)[12])(factory, i, &adapter) < 0) break;
                    try
                    {
                        // IDXGIAdapter::GetDesc is vtable slot 8
                        if (((delegate* unmanaged[Stdcall]<nint, byte*, int>)(*(nint**)adapter)[8])(adapter, desc) < 0) continue;
                        var name = new string((char*)desc).TrimEnd('\0');
                        var vendor = *(uint*)(desc + 256);
                        var mem = *(ulong*)(desc + 272);
                        AppLog.Write("obs", $"adapter {i}: {name} vendor=0x{vendor:X} vram={mem >> 20} MB");
                        if (vendor != VendorMicrosoft && mem > bestMem) { best = (int)i; bestMem = mem; }
                    }
                    finally { ((delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)adapter)[2])(adapter); }
                }
            }
            finally { ((delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)factory)[2])(factory); }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return 0; }
        AppLog.Write("obs", "using adapter " + best);
        return best;
    }
}
