using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using WinForms = System.Windows.Forms;

namespace ArcRecorder
{
    /// <summary>Описание монитора в терминах DXGI (тот же порядок, что видит ddagrab).</summary>
    public class MonitorInfo
    {
        public int AdapterIndex;   // индекс GPU (для -init_hw_device d3d11va=hw:N)
        public int OutputIndex;    // индекс выхода (для ddagrab output_idx)
        public string DeviceName;  // \\.\DISPLAY1
        public string AdapterName; // название видеокарты
        public int Left, Top, Width, Height;
        public bool Primary;
    }

    /// <summary>
    /// Энумерация мониторов через DXGI — в ТОМ ЖЕ порядке, в котором их нумерует ddagrab.
    /// Screen.AllScreens даёт другой порядок, из-за чего запись шла не с того монитора.
    /// </summary>
    public static class MonitorService
    {
        const uint DXGI_ADAPTER_FLAG_SOFTWARE = 2;

        [DllImport("dxgi.dll")]
        static extern int CreateDXGIFactory1([In] ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object ppFactory);

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DXGI_OUTPUT_DESC
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            public RECT DesktopCoordinates;
            public int AttachedToDesktop;
            public int Rotation;
            public IntPtr Monitor;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DXGI_ADAPTER_DESC1
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
            public uint VendorId, DeviceId, SubSysId, Revision;
            public IntPtr DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
            public long AdapterLuid;
            public uint Flags;
        }

        // Методы с "_" — заглушки для vtable-слотов, которые мы не вызываем.
        [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IDXGIFactory1
        {
            void _SetPrivateData(); void _SetPrivateDataInterface(); void _GetPrivateData(); void _GetParent();
            void _EnumAdapters(); void _MakeWindowAssociation(); void _GetWindowAssociation();
            void _CreateSwapChain(); void _CreateSoftwareAdapter();
            [PreserveSig] int EnumAdapters1(uint index, out IDXGIAdapter1 adapter);
        }

        [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IDXGIAdapter1
        {
            void _SetPrivateData(); void _SetPrivateDataInterface(); void _GetPrivateData(); void _GetParent();
            [PreserveSig] int EnumOutputs(uint index, out IDXGIOutput output);
            void _GetDesc(); void _CheckInterfaceSupport();
            [PreserveSig] int GetDesc1(out DXGI_ADAPTER_DESC1 desc);
        }

        [ComImport, Guid("ae02eedb-c735-4690-8d52-5a8dc20213aa"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IDXGIOutput
        {
            void _SetPrivateData(); void _SetPrivateDataInterface(); void _GetPrivateData(); void _GetParent();
            [PreserveSig] int GetDesc(out DXGI_OUTPUT_DESC desc);
        }

        /// <summary>Список мониторов в порядке DXGI. Если DXGI недоступен — фолбэк на Screen.AllScreens.</summary>
        public static List<MonitorInfo> GetMonitors()
        {
            var list = new List<MonitorInfo>();
            object factoryObj = null;
            try
            {
                var iid = typeof(IDXGIFactory1).GUID;
                if (CreateDXGIFactory1(ref iid, out factoryObj) == 0)
                {
                    var factory = (IDXGIFactory1)factoryObj;
                    for (uint a = 0; factory.EnumAdapters1(a, out var adapter) == 0; a++)
                    {
                        try
                        {
                            adapter.GetDesc1(out var ad);
                            if ((ad.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) != 0) continue; // WARP и прочий софт
                            for (uint o = 0; adapter.EnumOutputs(o, out var output) == 0; o++)
                            {
                                try
                                {
                                    if (output.GetDesc(out var od) != 0 || od.AttachedToDesktop == 0) continue;
                                    var r = od.DesktopCoordinates;
                                    list.Add(new MonitorInfo
                                    {
                                        AdapterIndex = (int)a,
                                        OutputIndex = (int)o,
                                        DeviceName = od.DeviceName,
                                        AdapterName = ad.Description?.Trim(),
                                        Left = r.Left, Top = r.Top,
                                        Width = r.Right - r.Left, Height = r.Bottom - r.Top,
                                        Primary = r.Left == 0 && r.Top == 0
                                    });
                                }
                                finally { Marshal.ReleaseComObject(output); }
                            }
                        }
                        finally { Marshal.ReleaseComObject(adapter); }
                    }
                }
            }
            catch { /* DXGI сломан — берём фолбэк ниже */ }
            finally { if (factoryObj != null) Marshal.ReleaseComObject(factoryObj); }

            if (list.Count == 0) list = Fallback();
            return list;
        }

        static List<MonitorInfo> Fallback()
        {
            var list = new List<MonitorInfo>();
            var screens = WinForms.Screen.AllScreens;
            for (int i = 0; i < screens.Length; i++)
            {
                var b = screens[i].Bounds;
                list.Add(new MonitorInfo
                {
                    AdapterIndex = 0, OutputIndex = i,
                    DeviceName = screens[i].DeviceName, AdapterName = "GPU",
                    Left = b.Left, Top = b.Top, Width = b.Width, Height = b.Height,
                    Primary = screens[i].Primary
                });
            }
            return list;
        }

        /// <summary>Найти монитор по сохранённым индексам (null, если конфигурация мониторов поменялась).</summary>
        public static MonitorInfo Find(AppSettings s)
        {
            foreach (var m in GetMonitors())
                if (m.AdapterIndex == s.AdapterIndex && m.OutputIndex == s.MonitorIndex)
                    return m;
            return null;
        }
    }
}
