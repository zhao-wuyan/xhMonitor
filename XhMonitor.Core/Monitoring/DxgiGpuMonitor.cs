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

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
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
            public string Name { get; set; } = string.Empty;
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
            public string AdapterName { get; set; } = string.Empty;
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
                // Create local copy of GUID for ref parameter
                var factoryGuid = IID_IDXGIFactory1;

                // Try CreateDXGIFactory1 first (Windows 7+)
                int hr = CreateDXGIFactory1(ref factoryGuid, out _factory);
                if (hr < 0)
                {
                    // Fallback to CreateDXGIFactory (older Windows)
                    factoryGuid = IID_IDXGIFactory1;
                    hr = CreateDXGIFactory(ref factoryGuid, out _factory);
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
                    // IDXGIAdapter3 vtable layout:
                    // IUnknown (0-2): QueryInterface, AddRef, Release
                    // IDXGIObject (3-6): SetPrivateData, SetPrivateDataInterface, GetPrivateData, GetParent
                    // IDXGIAdapter (7-9): EnumOutputs, GetDesc, CheckInterfaceSupport
                    // IDXGIAdapter1 (10): GetDesc1
                    // IDXGIAdapter2 (11): GetDesc2
                    // IDXGIAdapter3 (12-17): RegisterHardwareContentProtectionTeardownStatusEvent, UnregisterHardwareContentProtectionTeardownStatus, QueryVideoMemoryInfo, SetVideoMemoryReservation, RegisterVideoMemoryBudgetChangeNotificationEvent, UnregisterVideoMemoryBudgetChangeNotification

                    // Get QueryVideoMemoryInfo from IDXGIAdapter3 vtable
                    var adapter3Vtable = Marshal.ReadIntPtr(adapter3Ptr);
                    var queryMemInfoPtr = Marshal.ReadIntPtr(adapter3Vtable, IntPtr.Size * 14); // QueryVideoMemoryInfo is at index 14
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

        #region D3DKMT GPU Usage Monitoring

        private const int D3DKMT_QUERYSTATISTICS_RESULT_SIZE = 1024;
        private const int D3DKMT_QUERYSTATISTICS_QUERY_SIZE = 64;
        private const int SegmentGroupShift = 6;
        private const ulong SegmentGroupMask = 0x3;
        private const int D3dkmtSuccess = 0;
        private const int SegmentBytesResidentOffset = 16;

        [StructLayout(LayoutKind.Sequential)]
        private struct LUID
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct D3DKMT_QUERYSTATISTICS
        {
            public D3DKMT_QUERYSTATISTICS_TYPE Type;
            public LUID AdapterLuid;
            public IntPtr hProcess;
            public D3DKMT_QUERYSTATISTICS_RESULT QueryResult;
            public D3DKMT_QUERYSTATISTICS_QUERY Query;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct D3DKMT_QUERYSTATISTICS_OVERLAY
        {
            public D3DKMT_QUERYSTATISTICS_TYPE Type;
            public LUID AdapterLuid;
            public IntPtr hProcess;
            public D3DKMT_QUERYSTATISTICS_UNION Union;
        }

        [StructLayout(LayoutKind.Explicit, Size = D3DKMT_QUERYSTATISTICS_RESULT_SIZE)]
        private struct D3DKMT_QUERYSTATISTICS_UNION
        {
            [FieldOffset(0)]
            public D3DKMT_QUERYSTATISTICS_RESULT QueryResult;

            [FieldOffset(0)]
            public D3DKMT_QUERYSTATISTICS_QUERY Query;
        }

        [StructLayout(LayoutKind.Explicit, Size = D3DKMT_QUERYSTATISTICS_RESULT_SIZE)]
        private struct D3DKMT_QUERYSTATISTICS_RESULT
        {
            [FieldOffset(0)]
            public D3DKMT_QUERYSTATISTICS_ADAPTER_INFORMATION AdapterInformation;

            [FieldOffset(0)]
            public D3DKMT_QUERYSTATISTICS_NODE_INFORMATION NodeInformation;

            [FieldOffset(0)]
            public D3DKMT_QUERYSTATISTICS_SEGMENT_INFORMATION SegmentInformation;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct D3DKMT_QUERYSTATISTICS_ADAPTER_INFORMATION
        {
            public uint NbSegments;
            public uint NodeCount;
            public uint VidPnSourceCount;
            public uint VSyncEnabled;
            public uint TdrDetectedCount;
            public long ZeroLengthDmaBuffers;
            public ulong RestartedPeriod;
        }

        [StructLayout(LayoutKind.Explicit, Size = 256)]
        private struct D3DKMT_QUERYSTATISTICS_NODE_INFORMATION
        {
            [FieldOffset(0)]
            public D3DKMT_QUERYSTATISTICS_PROCESS_NODE_INFORMATION GlobalInformation;
            [FieldOffset(128)]
            public D3DKMT_QUERYSTATISTICS_PROCESS_NODE_INFORMATION SystemInformation;
        }

        [StructLayout(LayoutKind.Explicit, Size = 152)]
        private struct D3DKMT_QUERYSTATISTICS_SEGMENT_INFORMATION
        {
            [FieldOffset(0)]
            public ulong CommitLimit;
            [FieldOffset(8)]
            public ulong BytesCommitted;
            [FieldOffset(16)]
            public ulong BytesResident;
            [FieldOffset(104)]
            public ulong SegmentProperties;
        }

        [StructLayout(LayoutKind.Explicit, Size = 128)]
        private struct D3DKMT_QUERYSTATISTICS_PROCESS_NODE_INFORMATION
        {
            [FieldOffset(0)]
            public long RunningTime;
            [FieldOffset(8)]
            public uint ContextSwitch;
        }

        [StructLayout(LayoutKind.Explicit, Size = D3DKMT_QUERYSTATISTICS_QUERY_SIZE)]
        private struct D3DKMT_QUERYSTATISTICS_QUERY
        {
            [FieldOffset(0)]
            public D3DKMT_QUERYSTATISTICS_QUERY_SEGMENT QuerySegment;
            [FieldOffset(0)]
            public D3DKMT_QUERYSTATISTICS_QUERY_NODE QueryNode;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct D3DKMT_QUERYSTATISTICS_QUERY_SEGMENT
        {
            public uint SegmentId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct D3DKMT_QUERYSTATISTICS_QUERY_NODE
        {
            public uint NodeId;
        }

        private enum D3DKMT_QUERYSTATISTICS_TYPE
        {
            D3DKMT_QUERYSTATISTICS_ADAPTER = 0,
            D3DKMT_QUERYSTATISTICS_PROCESS = 1,
            D3DKMT_QUERYSTATISTICS_PROCESS_ADAPTER = 2,
            D3DKMT_QUERYSTATISTICS_SEGMENT = 3,
            D3DKMT_QUERYSTATISTICS_PROCESS_SEGMENT = 4,
            D3DKMT_QUERYSTATISTICS_NODE = 5,
            D3DKMT_QUERYSTATISTICS_PROCESS_NODE = 6,
            D3DKMT_QUERYSTATISTICS_VIDPNSOURCE = 7,
            D3DKMT_QUERYSTATISTICS_PROCESS_VIDPNSOURCE = 8
        }

        private enum D3DKMT_MEMORY_SEGMENT_GROUP : uint
        {
            D3DKMT_MEMORY_SEGMENT_GROUP_LOCAL = 0,
            D3DKMT_MEMORY_SEGMENT_GROUP_NON_LOCAL = 1
        }

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern int D3DKMTQueryStatistics(ref D3DKMT_QUERYSTATISTICS pData);

        [DllImport("gdi32.dll", SetLastError = true, EntryPoint = "D3DKMTQueryStatistics")]
        private static extern int D3DKMTQueryStatistics(ref D3DKMT_QUERYSTATISTICS_OVERLAY pData);

        // GPU usage tracking
        private class GpuNodeUsage
        {
            public ulong LastRunningTime { get; set; }
            public DateTime LastQueryTime { get; set; }
        }

        public readonly struct AdapterVramInfo
        {
            public AdapterVramInfo(string name, int luidHigh, uint luidLow, ulong segment0Bytes, bool skipped, bool success)
            {
                Name = name;
                LuidHigh = luidHigh;
                LuidLow = luidLow;
                Segment0Bytes = segment0Bytes;
                Skipped = skipped;
                Success = success;
            }

            public string Name { get; }
            public int LuidHigh { get; }
            public uint LuidLow { get; }
            public ulong Segment0Bytes { get; }
            public bool Skipped { get; }
            public bool Success { get; }
        }

        private readonly Dictionary<string, GpuNodeUsage> _nodeUsageCache = new();
        private readonly object _usageLock = new object();

        /// <summary>
        /// Get GPU usage percentage for all adapters (0-100)
        /// </summary>
        public double GetGpuUsage()
        {
            if (_adapters.Count == 0)
                return 0.0;

            try
            {
                double maxUsage = 0.0;

                foreach (var adapter in _adapters)
                {
                    var usage = GetAdapterGpuUsage(adapter);
                    if (usage >= 0)
                    {
                        if (usage > maxUsage)
                            maxUsage = usage;
                    }
                }

                return maxUsage;
            }
            catch
            {
                return 0.0;
            }
        }

        /// <summary>
        /// Get total dedicated VRAM usage across all adapters in bytes.
        /// Returns -1 when D3DKMT query fails.
        /// </summary>
        public long GetTotalVramUsageBytes()
        {
            if (_adapters.Count == 0)
            {
                return 0;
            }

            try
            {
                var adapterInfos = GetAdapterSegment0VramBytes(true);
                ulong maxBytes = 0;
                int validAdapters = 0;

                foreach (var info in adapterInfos)
                {
                    if (!info.Success || info.Skipped)
                        continue;

                    if (info.Segment0Bytes > maxBytes)
                        maxBytes = info.Segment0Bytes;

                    validAdapters++;
                }

                if (validAdapters == 0)
                {
                    return -1;
                }

                return maxBytes > long.MaxValue ? long.MaxValue : (long)maxBytes;
            }
            catch
            {
                return -1;
            }
        }

        public IReadOnlyList<AdapterVramInfo> GetAdapterSegment0VramBytes(bool skipBasicRenderDriver)
        {
            var results = new List<AdapterVramInfo>();
            if (_adapters.Count == 0)
                return results;

            foreach (var adapter in _adapters)
            {
                var desc = GetAdapterDesc(adapter.AdapterPtr);
                if (desc == null)
                    continue;

                var luid = desc.Value.AdapterLuid;
                bool skip = skipBasicRenderDriver && IsBasicRenderAdapter(adapter.Name);
                bool ok = TryGetSegment0BytesResident(luid, out var bytesResident);
                results.Add(new AdapterVramInfo(adapter.Name, luid.HighPart, luid.LowPart, ok ? bytesResident : 0, skip, ok));
            }

            return results;
        }

        private double GetAdapterGpuUsage(GpuAdapter adapter)
        {
            try
            {
                // Get adapter LUID from descriptor
                var desc = GetAdapterDesc(adapter.AdapterPtr);
                if (desc == null)
                    return -1;

                var luid = desc.Value.AdapterLuid;

                // Query adapter information to get node count
                var queryAdapter = new D3DKMT_QUERYSTATISTICS
                {
                    Type = D3DKMT_QUERYSTATISTICS_TYPE.D3DKMT_QUERYSTATISTICS_ADAPTER,
                    AdapterLuid = luid
                };

                int hr = D3DKMTQueryStatistics(ref queryAdapter);
                if (hr != D3dkmtSuccess)
                    return -1;

                uint nodeCount = queryAdapter.QueryResult.AdapterInformation.NodeCount;
                if (nodeCount == 0)
                    return 0.0;

                // Query each node and calculate usage
                double maxUsage = 0.0;
                DateTime now = DateTime.UtcNow;

                for (uint nodeId = 0; nodeId < nodeCount; nodeId++)
                {
                    var queryNode = new D3DKMT_QUERYSTATISTICS
                    {
                        Type = D3DKMT_QUERYSTATISTICS_TYPE.D3DKMT_QUERYSTATISTICS_NODE,
                        AdapterLuid = luid
                    };

                    queryNode.Query.QueryNode.NodeId = nodeId;

                    hr = D3DKMTQueryStatistics(ref queryNode);
                    if (hr != D3dkmtSuccess)
                        continue;

                    long runningTimeTicks = queryNode.QueryResult.NodeInformation.GlobalInformation.RunningTime;
                    if (runningTimeTicks < 0)
                        continue;

                    ulong runningTime = (ulong)runningTimeTicks;

                    // Calculate usage based on time delta
                    string cacheKey = $"{luid.LowPart}_{luid.HighPart}_{nodeId}";

                    lock (_usageLock)
                    {
                        double? usage = null;
                        if (_nodeUsageCache.TryGetValue(cacheKey, out var cached))
                        {
                            var timeDelta = (now - cached.LastQueryTime).TotalMilliseconds;
                            if (timeDelta > 0 && runningTime >= cached.LastRunningTime)
                            {
                                var runningDelta = runningTime - cached.LastRunningTime;
                                // RunningTime is in 100-nanosecond units
                                var runningMs = runningDelta / 10000.0;
                                usage = (runningMs / timeDelta) * 100.0;
                            }
                        }

                        _nodeUsageCache[cacheKey] = new GpuNodeUsage
                        {
                            LastRunningTime = runningTime,
                            LastQueryTime = now
                        };

                        if (usage.HasValue)
                        {
                            var clamped = Math.Max(0, Math.Min(100, usage.Value));
                            if (clamped > maxUsage)
                                maxUsage = clamped;
                        }
                    }
                }

                return maxUsage;
            }
            catch
            {
                return -1;
            }
        }

        private static int GetSegmentGroup(D3DKMT_QUERYSTATISTICS_SEGMENT_INFORMATION segmentInfo)
        {
            return (int)((segmentInfo.SegmentProperties >> SegmentGroupShift) & SegmentGroupMask);
        }

        private static bool IsBasicRenderAdapter(string name)
        {
            return name.IndexOf("Microsoft Basic Render Driver", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool TryGetSegment0BytesResident(LUID luid, out ulong bytesResident)
        {
            bytesResident = 0;

            try
            {
                var querySegment = new D3DKMT_QUERYSTATISTICS_OVERLAY
                {
                    Type = D3DKMT_QUERYSTATISTICS_TYPE.D3DKMT_QUERYSTATISTICS_SEGMENT,
                    AdapterLuid = luid
                };
                querySegment.Union.Query.QuerySegment.SegmentId = 0;

                int hr = D3DKMTQueryStatistics(ref querySegment);
                if (hr != D3dkmtSuccess)
                    return false;

                var segmentInfo = querySegment.Union.QueryResult.SegmentInformation;
                bytesResident = ReadUlongAtOffset(ref segmentInfo, SegmentBytesResidentOffset);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static ulong ReadUlongAtOffset(ref D3DKMT_QUERYSTATISTICS_SEGMENT_INFORMATION segmentInfo, int offset)
        {
            var ptr = Marshal.AllocHGlobal(Marshal.SizeOf<D3DKMT_QUERYSTATISTICS_SEGMENT_INFORMATION>());
            try
            {
                Marshal.StructureToPtr(segmentInfo, ptr, false);
                return (ulong)Marshal.ReadInt64(ptr, offset);
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }

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
