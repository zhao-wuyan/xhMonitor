using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Xunit;
using Xunit.Abstractions;
using XhMonitor.Core.Monitoring;

namespace XhMonitor.Tests.Monitoring;

public class DxgiQueryVideoMemoryInfoTests
{
    private readonly ITestOutputHelper _output;

    public DxgiQueryVideoMemoryInfoTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void QueryVideoMemoryInfo_VTableIndexSweep_PrintsCandidates()
    {
        if (!OperatingSystem.IsWindows())
        {
            _output.WriteLine("Windows only.");
            return;
        }

        using var monitor = new DxgiGpuMonitor();
        if (!monitor.Initialize())
        {
            _output.WriteLine("DXGI initialize failed.");
            return;
        }

        var adapters = monitor.GetAdapters();
        if (adapters.Count == 0)
        {
            _output.WriteLine("No GPU adapters found.");
            return;
        }

        var adapterPtrs = GetAdapterPointers(monitor);
        if (adapterPtrs.Count == 0)
        {
            _output.WriteLine("No adapter pointers available.");
            return;
        }

        var indices = new[] { 12, 13, 14, 15, 16, 17 };
        const double targetGb = 31.5;
        const double toleranceGb = 1.0;

        _output.WriteLine($"Target VRAM used: {targetGb:F1} GB, tolerance: {toleranceGb:F1} GB");
        _output.WriteLine($"Index sweep: {string.Join(", ", indices)}");

        Candidate? best = null;

        foreach (var index in indices)
        {
            ulong totalUsedBytes = 0;
            ulong totalBudgetBytes = 0;
            int successAdapters = 0;

            foreach (var adapter in adapterPtrs)
            {
                var localResult = TryQueryVideoMemoryInfo(adapter.AdapterPtr, index, DXGI_MEMORY_SEGMENT_GROUP.DXGI_MEMORY_SEGMENT_GROUP_LOCAL, out var localInfo);
                var nonLocalResult = TryQueryVideoMemoryInfo(adapter.AdapterPtr, index, DXGI_MEMORY_SEGMENT_GROUP.DXGI_MEMORY_SEGMENT_GROUP_NON_LOCAL, out var nonLocalInfo);

                if (!localResult && !nonLocalResult)
                {
                    _output.WriteLine($"Index {index} | {adapter.Name}: failed");
                    continue;
                }

                successAdapters++;
                if (localResult)
                {
                    totalUsedBytes += localInfo.CurrentUsage;
                    totalBudgetBytes += localInfo.Budget;
                    var usedGb = BytesToGb(localInfo.CurrentUsage);
                    var budgetGb = BytesToGb(localInfo.Budget);
                    _output.WriteLine($"Index {index} | {adapter.Name}: Local Used {usedGb:F2} GB, Budget {budgetGb:F2} GB");
                }
                if (nonLocalResult)
                {
                    var usedGb = BytesToGb(nonLocalInfo.CurrentUsage);
                    var budgetGb = BytesToGb(nonLocalInfo.Budget);
                    _output.WriteLine($"Index {index} | {adapter.Name}: NonLocal Used {usedGb:F2} GB, Budget {budgetGb:F2} GB");
                }
            }

            if (successAdapters == 0)
                continue;

            var totalUsedGb = BytesToGb(totalUsedBytes);
            var totalBudgetGb = BytesToGb(totalBudgetBytes);
            var delta = Math.Abs(totalUsedGb - targetGb);

            _output.WriteLine($"Index {index} | Total Used {totalUsedGb:F2} GB, Total Budget {totalBudgetGb:F2} GB, Delta {delta:F2} GB");

            if (best == null || delta < best.Value.DeltaGb)
            {
                best = new Candidate(index, totalUsedGb, totalBudgetGb, delta);
            }
        }

        if (best.HasValue)
        {
            var hit = best.Value.DeltaGb <= toleranceGb ? "within tolerance" : "out of tolerance";
            _output.WriteLine($"Best index: {best.Value.Index}, Used {best.Value.UsedGb:F2} GB, Budget {best.Value.BudgetGb:F2} GB, Delta {best.Value.DeltaGb:F2} GB ({hit})");
        }

        _output.WriteLine("COM path:");
        var comCandidates = TryQueryVideoMemoryInfoCom(adapterPtrs);
        foreach (var candidate in comCandidates)
        {
            var hit = candidate.DeltaGb <= toleranceGb ? "within tolerance" : "out of tolerance";
            _output.WriteLine($"COM | {candidate.Name}: Used {candidate.UsedGb:F2} GB, Budget {candidate.BudgetGb:F2} GB, Delta {candidate.DeltaGb:F2} GB ({hit})");
        }
    }

    private static List<(IntPtr AdapterPtr, string Name)> GetAdapterPointers(DxgiGpuMonitor monitor)
    {
        var results = new List<(IntPtr AdapterPtr, string Name)>();
        var adaptersField = typeof(DxgiGpuMonitor).GetField("_adapters", BindingFlags.NonPublic | BindingFlags.Instance);
        if (adaptersField == null)
            return results;

        if (adaptersField.GetValue(monitor) is not System.Collections.IEnumerable adapters)
            return results;

        foreach (var adapterObj in adapters)
        {
            if (adapterObj is not DxgiGpuMonitor.GpuAdapter adapter)
                continue;

            var ptrProperty = adapterObj.GetType().GetProperty("AdapterPtr", BindingFlags.NonPublic | BindingFlags.Instance);
            if (ptrProperty?.GetValue(adapterObj) is not IntPtr ptr || ptr == IntPtr.Zero)
                continue;

            results.Add((ptr, adapter.Name));
        }

        return results;
    }

    private static bool TryQueryVideoMemoryInfo(IntPtr adapterPtr, int vtableIndex, DXGI_MEMORY_SEGMENT_GROUP group, out DXGI_QUERY_VIDEO_MEMORY_INFO info)
    {
        info = default;
        IntPtr adapter3Ptr = IntPtr.Zero;

        try
        {
            var iid = IID_IDXGIAdapter3;
            var vtable = Marshal.ReadIntPtr(adapterPtr);
            var queryInterfacePtr = Marshal.ReadIntPtr(vtable, 0);
            var queryInterface = Marshal.GetDelegateForFunctionPointer<QueryInterfaceDelegate>(queryInterfacePtr);
            var hr = queryInterface(adapterPtr, ref iid, out adapter3Ptr);
            if (hr < 0 || adapter3Ptr == IntPtr.Zero)
                return false;

            var adapter3Vtable = Marshal.ReadIntPtr(adapter3Ptr);
            var queryMemInfoPtr = Marshal.ReadIntPtr(adapter3Vtable, IntPtr.Size * vtableIndex);
            if (queryMemInfoPtr == IntPtr.Zero)
                return false;

            var queryMemInfo = Marshal.GetDelegateForFunctionPointer<QueryVideoMemoryInfoDelegate>(queryMemInfoPtr);
            hr = queryMemInfo(
                adapter3Ptr,
                0,
                group,
                out info);

            return hr >= 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (adapter3Ptr != IntPtr.Zero)
                Marshal.Release(adapter3Ptr);
        }
    }

    private static double BytesToGb(ulong bytes)
    {
        return bytes / 1024.0 / 1024.0 / 1024.0;
    }

    private static List<ComCandidate> TryQueryVideoMemoryInfoCom(List<(IntPtr AdapterPtr, string Name)> adapterPtrs)
    {
        var results = new List<ComCandidate>();
        foreach (var adapter in adapterPtrs)
        {
            try
            {
                var obj = Marshal.GetObjectForIUnknown(adapter.AdapterPtr);
                if (obj is not IDXGIAdapter3Com adapter3)
                    continue;

                if (adapter3.QueryVideoMemoryInfo(0, DXGI_MEMORY_SEGMENT_GROUP.DXGI_MEMORY_SEGMENT_GROUP_LOCAL, out var info) < 0)
                    continue;

                var usedGb = BytesToGb(info.CurrentUsage);
                var budgetGb = BytesToGb(info.Budget);
                var delta = Math.Abs(usedGb - 31.5);
                results.Add(new ComCandidate(adapter.Name, usedGb, budgetGb, delta));
            }
            catch
            {
                continue;
            }
        }

        return results;
    }

    private readonly struct Candidate
    {
        public Candidate(int index, double usedGb, double budgetGb, double deltaGb)
        {
            Index = index;
            UsedGb = usedGb;
            BudgetGb = budgetGb;
            DeltaGb = deltaGb;
        }

        public int Index { get; }
        public double UsedGb { get; }
        public double BudgetGb { get; }
        public double DeltaGb { get; }
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

    private static readonly Guid IID_IDXGIAdapter3 = new("645967A4-1392-4310-A798-8053CE3E93FD");

    private delegate int QueryInterfaceDelegate(IntPtr pUnk, ref Guid riid, out IntPtr ppvObject);

    private delegate int QueryVideoMemoryInfoDelegate(
        IntPtr adapter,
        uint nodeIndex,
        DXGI_MEMORY_SEGMENT_GROUP memorySegmentGroup,
        out DXGI_QUERY_VIDEO_MEMORY_INFO pVideoMemoryInfo);

    private readonly struct ComCandidate
    {
        public ComCandidate(string name, double usedGb, double budgetGb, double deltaGb)
        {
            Name = name;
            UsedGb = usedGb;
            BudgetGb = budgetGb;
            DeltaGb = deltaGb;
        }

        public string Name { get; }
        public double UsedGb { get; }
        public double BudgetGb { get; }
        public double DeltaGb { get; }
    }

    [ComImport]
    [Guid("645967A4-1392-4310-A798-8053CE3E93FD")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter3Com
    {
        int QueryInterface(ref Guid riid, out IntPtr ppvObject);
        uint AddRef();
        uint Release();
        int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
        int SetPrivateDataInterface(ref Guid name, IntPtr unknown);
        int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
        int GetParent(ref Guid riid, out IntPtr parent);
        int EnumOutputs(uint output, out IntPtr outputPtr);
        int GetDesc(out IntPtr desc);
        int CheckInterfaceSupport(ref Guid interfaceName, out long umdVersion);
        int GetDesc1(out IntPtr desc);
        int GetDesc2(out IntPtr desc);
        int RegisterHardwareContentProtectionTeardownStatusEvent(IntPtr eventHandle, out uint cookie);
        int UnregisterHardwareContentProtectionTeardownStatus(uint cookie);
        int QueryVideoMemoryInfo(uint nodeIndex, DXGI_MEMORY_SEGMENT_GROUP memorySegmentGroup, out DXGI_QUERY_VIDEO_MEMORY_INFO pVideoMemoryInfo);
        int SetVideoMemoryReservation(uint nodeIndex, DXGI_MEMORY_SEGMENT_GROUP memorySegmentGroup, ulong reservation);
        int RegisterVideoMemoryBudgetChangeNotificationEvent(IntPtr eventHandle, out uint cookie);
        int UnregisterVideoMemoryBudgetChangeNotification(uint cookie);
    }
}
