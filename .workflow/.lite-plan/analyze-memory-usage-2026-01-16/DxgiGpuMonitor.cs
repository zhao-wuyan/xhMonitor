using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace XhMonitor.Core.Monitoring
{
    /// <summary>
    /// DXGI-based GPU monitoring for Windows (supports NVIDIA, AMD, Intel, etc.)
    /// Lightweight alternative to PerformanceCounter iteration
    /// </summary>
    public class DxgiGpuMonitor : IDisposable
    {
        private IntPtr _factory;
        private readonly List<GpuAdapter> _adapters = new();
        private bool _disposed;

        #region DXGI Structures and Enums

        [StructLayout(LayoutKind.Sequential)]
        private struct DXGI_ADAPTER_DESC1
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string Description;
            public uint VendorId;
            public uint DeviceId;
            public uint SubSysId;
            public uint Revision;
            public UIntPtr DedicatedVideoMemory;
            public UIntPtr DedicatedSystemMemory;
            public UIntPtr SharedSystemMemory;
            public LUID AdapterLuid;
            public uint Flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LUID
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DXGI_QUERY_VIDEO_MEMORY_INFO
        {
            public ulong Budget;
            public ulong CurrentUsage;
            public ulong AvailableForReservation;
            public ulong CurrentReservation;
        }

        private enum DXGI_MEMORY_SEGMENT_GROUP
        {
            DXGI_MEMORY_SEGMENT_GROUP_LOCAL = 0,
            DXGI_MEMORY_SEGMENT_GROUP_NON_LOCAL = 1
        }

        #endregion

        #region DXGI P/Invoke

        [DllImport("dxgi.dll", EntryPoint = "CreateDXGIFactory1")]
        private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr ppFactory);

        [DllImport("dxgi.dll", EntryPoint = "CreateDXGIFactory")]
        private static extern int CreateDXGIFactory(ref Guid riid, out IntPtr ppFactory);

        // IDXGIFactory1 methods
        private delegate int EnumAdapters1Delegate(IntPtr factory, uint adapter, out IntPtr ppAdapter);

        // IDXGIAdapter methods
        private delegate int GetDesc1Delegate(IntPtr adapter, out DXGI_ADAPTER_DESC1 pDesc);

        // IDXGIAdapter3 methods (Windows 10+)
        private delegate int QueryVideoMemoryInfoDelegate(
            IntPtr adapter,
            uint nodeIndex,
            DXGI_MEMORY_SEGMENT_GROUP memorySegmentGroup,
            out DXGI_QUERY_VIDEO_MEMORY_INFO pVideoMemoryInfo);

        private static readonly Guid IID_IDXGIFactory1 = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
        private static readonly Guid IID_IDXGIAdapter3 = new Guid("645967A4-1392-4310-A798-8053CE3E93FD");

        #endregion

        #region Public API

        /// <summary>
        /// GPU adapter information
        /// </summary>
        public class GpuAdapter
        {
            public string Name { get; set; }
            public uint VendorId { get; set; }
            public ulong DedicatedVideoMemory { get; set; }
            public ulong SharedSystemMemory { get; set; }
            internal IntPtr AdapterPtr { get; set; }
        }

        /// <summary>
        /// GPU memory usage information
        /// </summary>
        public class GpuMemoryInfo
        {
            public string AdapterName { get; set; }
            public ulong TotalMemory { get; set; }
            public ulong UsedMemory { get; set; }
            public ulong AvailableMemory { get; set; }
            public double UsagePercent { get; set; }
        }

        /// <summary>
        /// Initialize DXGI and enumerate GPU adapters
        /// </summary>
        public bool Initialize()
        {
            try
            {
                // Try CreateDXGIFactory1 first (Windows 7+)
                int hr = CreateDXGIFactory1(ref IID_IDXGIFactory1, out _factory);
                if (hr < 0)
                {
                    // Fallback to CreateDXGIFactory (older Windows)
                    hr = CreateDXGIFactory(ref IID_IDXGIFactory1, out _factory);
                    if (hr < 0)
                    {
                        return false;
                    }
                }

                // Enumerate adapters
                EnumerateAdapters();
                return _adapters.Count > 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Get all GPU adapters
        /// </summary>
        public IReadOnlyList<GpuAdapter> GetAdapters()
        {
            return _adapters.AsReadOnly();
        }

        /// <summary>
        /// Get memory usage for all GPUs
        /// </summary>
        public List<GpuMemoryInfo> GetMemoryUsage()
        {
            var results = new List<GpuMemoryInfo>();

            foreach (var adapter in _adapters)
            {
                try
                {
                    var memInfo = QueryAdapterMemory(adapter.AdapterPtr);
                    if (memInfo != null)
                    {
                        results.Add(new GpuMemoryInfo
                        {
                            AdapterName = adapter.Name,
                            TotalMemory = adapter.DedicatedVideoMemory,
                            UsedMemory = memInfo.Value.CurrentUsage,
                            AvailableMemory = memInfo.Value.Budget - memInfo.Value.CurrentUsage,
                            UsagePercent = adapter.DedicatedVideoMemory > 0
                                ? (double)memInfo.Value.CurrentUsage / adapter.DedicatedVideoMemory * 100.0
                                : 0.0
                        });
                    }
                }
                catch
                {
                    // Skip adapters that don't support QueryVideoMemoryInfo
                    continue;
                }
            }

            return results;
        }

        /// <summary>
        /// Get total system GPU memory usage (all adapters combined)
        /// </summary>
        public (ulong TotalMemory, ulong UsedMemory, double UsagePercent) GetTotalMemoryUsage()
        {
            ulong totalMemory = 0;
            ulong usedMemory = 0;

            foreach (var adapter in _adapters)
            {
                try
                {
                    var memInfo = QueryAdapterMemory(adapter.AdapterPtr);
                    if (memInfo != null)
                    {
                        totalMemory += adapter.DedicatedVideoMemory;
                        usedMemory += memInfo.Value.CurrentUsage;
                    }
                }
                catch
                {
                    continue;
                }
            }

            double usagePercent = totalMemory > 0 ? (double)usedMemory / totalMemory * 100.0 : 0.0;
            return (totalMemory, usedMemory, usagePercent);
        }

        #endregion

        #region Private Methods

        private void EnumerateAdapters()
        {
            if (_factory == IntPtr.Zero)
                return;

            // Get EnumAdapters1 function pointer from vtable
            var vtable = Marshal.ReadIntPtr(_factory);
            var enumAdapters1Ptr = Marshal.ReadIntPtr(vtable, IntPtr.Size * 7); // EnumAdapters1 is at index 7
            var enumAdapters1 = Marshal.GetDelegateForFunctionPointer<EnumAdapters1Delegate>(enumAdapters1Ptr);

            uint adapterIndex = 0;
            while (true)
            {
                int hr = enumAdapters1(_factory, adapterIndex, out IntPtr adapterPtr);
                if (hr < 0) // No more adapters
                    break;

                try
                {
                    var desc = GetAdapterDesc(adapterPtr);
                    if (desc != null)
                    {
                        _adapters.Add(new GpuAdapter
                        {
                            Name = desc.Value.Description,
                            VendorId = desc.Value.VendorId,
                            DedicatedVideoMemory = (ulong)desc.Value.DedicatedVideoMemory,
                            SharedSystemMemory = (ulong)desc.Value.SharedSystemMemory,
                            AdapterPtr = adapterPtr
                        });
                    }
                }
                catch
                {
                    // Release adapter on error
                    if (adapterPtr != IntPtr.Zero)
                        Marshal.Release(adapterPtr);
                }

                adapterIndex++;
            }
        }

        private DXGI_ADAPTER_DESC1? GetAdapterDesc(IntPtr adapterPtr)
        {
            try
            {
                var vtable = Marshal.ReadIntPtr(adapterPtr);
                var getDesc1Ptr = Marshal.ReadIntPtr(vtable, IntPtr.Size * 10); // GetDesc1 is at index 10
                var getDesc1 = Marshal.GetDelegateForFunctionPointer<GetDesc1Delegate>(getDesc1Ptr);

                int hr = getDesc1(adapterPtr, out DXGI_ADAPTER_DESC1 desc);
                return hr >= 0 ? desc : null;
            }
            catch
            {
                return null;
            }
        }

        private DXGI_QUERY_VIDEO_MEMORY_INFO? QueryAdapterMemory(IntPtr adapterPtr)
        {
            try
            {
                // QueryInterface to IDXGIAdapter3
                IntPtr adapter3Ptr = IntPtr.Zero;
                var iid = IID_IDXGIAdapter3;

                // Get QueryInterface from IUnknown vtable (index 0)
                var vtable = Marshal.ReadIntPtr(adapterPtr);
                var queryInterfacePtr = Marshal.ReadIntPtr(vtable, 0);
                var queryInterface = Marshal.GetDelegateForFunctionPointer<QueryInterfaceDelegate>(queryInterfacePtr);

                int hr = queryInterface(adapterPtr, ref iid, out adapter3Ptr);
                if (hr < 0 || adapter3Ptr == IntPtr.Zero)
                    return null;

                try
                {
                    // Get QueryVideoMemoryInfo from IDXGIAdapter3 vtable
                    var adapter3Vtable = Marshal.ReadIntPtr(adapter3Ptr);
                    var queryMemInfoPtr = Marshal.ReadIntPtr(adapter3Vtable, IntPtr.Size * 16); // QueryVideoMemoryInfo is at index 16
                    var queryMemInfo = Marshal.GetDelegateForFunctionPointer<QueryVideoMemoryInfoDelegate>(queryMemInfoPtr);

                    // Query local video memory (dedicated VRAM)
                    hr = queryMemInfo(
                        adapter3Ptr,
                        0, // NodeIndex 0 for single-GPU
                        DXGI_MEMORY_SEGMENT_GROUP.DXGI_MEMORY_SEGMENT_GROUP_LOCAL,
                        out DXGI_QUERY_VIDEO_MEMORY_INFO memInfo);

                    return hr >= 0 ? memInfo : null;
                }
                finally
                {
                    if (adapter3Ptr != IntPtr.Zero)
                        Marshal.Release(adapter3Ptr);
                }
            }
            catch
            {
                return null;
            }
        }

        private delegate int QueryInterfaceDelegate(IntPtr pUnk, ref Guid riid, out IntPtr ppvObject);

        #endregion

        #region IDisposable

        public void Dispose()
        {
            if (_disposed)
                return;

            // Release all adapter pointers
            foreach (var adapter in _adapters)
            {
                if (adapter.AdapterPtr != IntPtr.Zero)
                {
                    Marshal.Release(adapter.AdapterPtr);
                    adapter.AdapterPtr = IntPtr.Zero;
                }
            }
            _adapters.Clear();

            // Release factory
            if (_factory != IntPtr.Zero)
            {
                Marshal.Release(_factory);
                _factory = IntPtr.Zero;
            }

            _disposed = true;
        }

        #endregion
    }
}
