using System.Runtime.InteropServices;

namespace KovaaksCompanion.Core.Video;

/// <summary>One DXGI output (monitor). <see cref="OutputIndex"/> is the per-adapter index ddagrab's output_idx expects.</summary>
public sealed record DxgiOutput(int AdapterIndex, string AdapterName, int OutputIndex, string DeviceName,
    int Left, int Top, int Right, int Bottom, bool AttachedToDesktop, int Rotation = 1)
{
    /// <summary>DXGI_MODE_ROTATION: 1 = identity. Rotated outputs are captured unrotated by ddagrab, so crop math does not apply.</summary>
    public bool IsRotated => Rotation != 1;
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    /// <summary>The primary monitor always contains the virtual-desktop origin.</summary>
    public bool IsPrimary => AttachedToDesktop && Left <= 0 && Top <= 0 && Right > 0 && Bottom > 0;
}

/// <summary>Enumerates DXGI outputs so the primary monitor can be picked instead of hard-coding output 0.</summary>
public static unsafe class MonitorResolver
{
    [DllImport("dxgi.dll")] private static extern int CreateDXGIFactory1(Guid* riid, void** factory);
    private static readonly Guid IidFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");

    public static IReadOnlyList<DxgiOutput> Enumerate()
    {
        var result = new List<DxgiOutput>();
        if (!OperatingSystem.IsWindows()) return result;
        var adapterBuf = stackalloc byte[320]; // DXGI_ADAPTER_DESC: first 256 bytes = WCHAR[128] name
        var outBuf = stackalloc byte[128];     // DXGI_OUTPUT_DESC: name[32]W, RECT, BOOL attached, rotation, HMONITOR
        void* factory = null;
        Guid iid = IidFactory1;
        if (CreateDXGIFactory1(&iid, &factory) < 0) return result;
        try
        {
            for (uint a = 0; ; a++)
            {
                void* adapter;
                if (((delegate* unmanaged[Stdcall]<void*, uint, void**, int>)VTable(factory, 12))(factory, a, &adapter) < 0) break;
                try
                {
                    var descBuf = adapterBuf;
                    string name = "";
                    if (((delegate* unmanaged[Stdcall]<void*, byte*, int>)VTable(adapter, 8))(adapter, descBuf) >= 0)
                        name = new string((char*)descBuf).TrimEnd('\0');
                    for (uint o = 0; ; o++)
                    {
                        void* output;
                        if (((delegate* unmanaged[Stdcall]<void*, uint, void**, int>)VTable(adapter, 7))(adapter, o, &output) < 0) break;
                        try
                        {
                            var d = outBuf;
                            if (((delegate* unmanaged[Stdcall]<void*, byte*, int>)VTable(output, 7))(output, d) < 0) continue;
                            var dev = new string((char*)d).TrimEnd('\0');
                            var r = (int*)(d + 64);
                            bool attached = *(int*)(d + 80) != 0;
                            result.Add(new DxgiOutput((int)a, name, (int)o, dev, r[0], r[1], r[2], r[3], attached, *(int*)(d + 84)));
                        }
                        finally { Release(output); }
                    }
                }
                finally { Release(adapter); }
            }
        }
        finally { Release(factory); }
        return result;
    }

    /// <summary>Primary monitor output, or null if none can be found.</summary>
    public static DxgiOutput? FindPrimary() => Pick(Enumerate());

    public static DxgiOutput? Pick(IEnumerable<DxgiOutput> outputs)
        => outputs.Where(o => o.IsPrimary).OrderBy(o => o.AdapterIndex).ThenBy(o => o.OutputIndex).FirstOrDefault();

    private static void* VTable(void* obj, int index) => (*(void***)obj)[index];
    private static void Release(void* obj) => ((delegate* unmanaged[Stdcall]<void*, uint>)VTable(obj, 2))(obj);
}
