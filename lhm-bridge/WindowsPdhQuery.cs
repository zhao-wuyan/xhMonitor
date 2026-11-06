using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

[StructLayout(LayoutKind.Explicit, Size = 16)]
internal struct PdhFormattedValue
{
    [FieldOffset(0)] internal uint Status;
    [FieldOffset(8)] internal double Value;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PdhCounterValueItem
{
    internal IntPtr Name;
    internal PdhFormattedValue FormattedValue;
}

// 返回的 span 借用缓冲区，仅在下一次读取、扩容或 Dispose 前有效；调用方串行使用。
internal sealed class WindowsPdhQuery : IDisposable
{
    private const uint MoreData = 0x800007D2;
    private const uint Format = 0x00000200 | 0x00008000;
    private SafePdhQueryHandle? _query;
    private IntPtr _counter;
    private SafeHGlobalBuffer? _buffer;
    private uint _validBytes;

    internal WindowsPdhQuery(string counterPath)
    {
        try
        {
            if (PdhOpenQueryW(null, UIntPtr.Zero, out var query) != 0)
            {
                query?.Dispose();
                return;
            }
            _query = query;
            if (PdhAddEnglishCounterW(query, counterPath, UIntPtr.Zero, out _counter) != 0)
            {
                Dispose();
            }
        }
        catch (DllNotFoundException)
        {
            Dispose();
        }
        catch (EntryPointNotFoundException)
        {
            Dispose();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal bool IsAvailable => _query is { IsClosed: false, IsInvalid: false } && _counter != IntPtr.Zero;

    internal bool Collect() => IsAvailable && PdhCollectQueryData(_query!) == 0;

    internal unsafe bool TryReadValues(out ReadOnlySpan<PdhCounterValueItem> items)
    {
        items = default;
        _validBytes = 0;
        if (!IsAvailable) return false;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (_buffer is not null)
            {
                var size = _buffer.Capacity;
                var status = PdhGetFormattedCounterArrayW(_counter, Format, ref size, out var count,
                    _buffer.DangerousGetHandle());
                if (status == 0)
                {
                    if (size > _buffer.Capacity || (ulong)count * (uint)sizeof(PdhCounterValueItem) > size || count > int.MaxValue)
                        return false;
                    _validBytes = size;
                    items = new ReadOnlySpan<PdhCounterValueItem>((void*)_buffer.DangerousGetHandle(), (int)count);
                    return true;
                }
                if (status != MoreData) return false;
            }

            // 非零缓冲区不足时返回的 size 不可靠，必须重新以零长度询问。
            uint needed = 0;
            var sizingStatus = PdhGetFormattedCounterArrayW(_counter, Format, ref needed, out _, IntPtr.Zero);
            if (sizingStatus != MoreData && sizingStatus != 0) return false;
            if (needed == 0) return true;
            if (_buffer is null || needed > _buffer.Capacity)
            {
                var replacement = new SafeHGlobalBuffer(needed);
                _buffer?.Dispose();
                _buffer = replacement;
            }
        }
        return false;
    }

    internal unsafe ReadOnlySpan<char> GetInstanceName(in PdhCounterValueItem item)
    {
        if (_buffer is null || _buffer.IsClosed || _validBytes < sizeof(char)) return default;
        var start = (nuint)_buffer.DangerousGetHandle();
        var name = (nuint)item.Name;
        if (name < start || name - start > _validBytes - sizeof(char) || ((name - start) & 1) != 0)
            return default;
        var remaining = (_validBytes - (uint)(name - start)) / sizeof(char);
        if (remaining > int.MaxValue) return default;
        var text = new ReadOnlySpan<char>((void*)name, (int)remaining);
        var end = text.IndexOf('\0');
        return end < 0 ? default : text[..end];
    }

    public void Dispose()
    {
        _validBytes = 0;
        _counter = IntPtr.Zero;
        _buffer?.Dispose();
        _buffer = null;
        _query?.Dispose();
        _query = null;
    }

    private sealed class SafePdhQueryHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafePdhQueryHandle() : base(true) { }
        protected override bool ReleaseHandle() => PdhCloseQuery(handle) == 0;
    }

    private sealed class SafeHGlobalBuffer : SafeBuffer
    {
        internal uint Capacity { get; }
        internal SafeHGlobalBuffer(uint capacity) : base(true)
        {
            SetHandle(Marshal.AllocHGlobal(checked((nint)capacity)));
            Capacity = capacity;
            Initialize(capacity);
        }
        protected override bool ReleaseHandle()
        {
            Marshal.FreeHGlobal(handle);
            return true;
        }
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhOpenQueryW(string? dataSource, UIntPtr userData, out SafePdhQueryHandle query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhAddEnglishCounterW(SafePdhQueryHandle query, string path, UIntPtr userData, out IntPtr counter);
    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhCollectQueryData(SafePdhQueryHandle query);
    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint size, out uint count, IntPtr items);
    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhCloseQuery(IntPtr query);
}
