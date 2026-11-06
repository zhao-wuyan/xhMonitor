using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

internal static class WindowsNativeDiskReader
{
    internal static readonly Guid DiskInterface = new("53F56307-B6BF-11D0-94F2-00A0C91EFB8B");
    internal const uint PresentDeviceInterfaces = 0x12;
    internal const uint GenericRead = 0x80000000;
    internal const uint ShareReadWrite = 3;
    internal const uint OpenExisting = 3;
    internal const uint StorageGetDeviceNumber = 0x002D1080;
    internal const uint StorageQueryProperty = 0x002D1400;
    internal const uint DiskGetDriveGeometryEx = 0x000700A0;
    internal const uint DiskGetDriveLayoutEx = 0x00070050;
    internal const uint VolumeGetVolumeDiskExtents = 0x00560000;
    internal const uint DiskPerformance = 0x00070020;
    internal const int InsufficientBuffer = 122;
    internal const int MoreData = 234;
    internal const int NoMoreItems = 259;
    internal const int NoMoreFiles = 18;
    internal const uint FriendlyName = 12;

    // x64 的 SP_DEVICE_INTERFACE_DATA 和 SP_DEVINFO_DATA 均为 32 字节。
    [StructLayout(LayoutKind.Sequential)]
    internal struct DeviceInterfaceData
    {
        internal uint Size;
        internal Guid InterfaceClassGuid;
        internal uint Flags;
        internal UIntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DeviceInfoData
    {
        internal uint Size;
        internal Guid ClassGuid;
        internal uint DeviceInstance;
        internal UIntPtr Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeDeviceInfoSetHandle SetupDiGetClassDevsW(
        in Guid classGuid, IntPtr enumerator, IntPtr parent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetupDiEnumDeviceInterfaces(SafeDeviceInfoSetHandle devices,
        IntPtr device, in Guid interfaceGuid, uint index, ref DeviceInterfaceData data);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetupDiGetDeviceInterfaceDetailW(SafeDeviceInfoSetHandle devices,
        ref DeviceInterfaceData data, [In, Out] byte[]? detail, uint detailSize,
        out uint requiredSize, ref DeviceInfoData deviceInfo);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetupDiGetDeviceRegistryPropertyW(SafeDeviceInfoSetHandle devices,
        ref DeviceInfoData deviceInfo, uint property, out uint propertyType,
        [Out] byte[] buffer, uint bufferSize, out uint requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr devices);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeFileHandle CreateFileW(string path, uint access, uint share,
        IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeviceIoControl(SafeFileHandle device, uint code,
        [In] byte[]? input, uint inputSize, [Out] byte[] output, uint outputSize,
        out uint returned, IntPtr overlapped);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeVolumeFindHandle FindFirstVolumeW(StringBuilder name, uint length);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool FindNextVolumeW(SafeVolumeFindHandle search, StringBuilder name, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindVolumeClose(IntPtr search);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetDiskFreeSpaceExW(string path, out ulong available,
        out ulong total, out ulong free);

    internal sealed class SafeDeviceInfoSetHandle : SafeHandleMinusOneIsInvalid
    {
        public SafeDeviceInfoSetHandle() : base(true) { }
        protected override bool ReleaseHandle() => SetupDiDestroyDeviceInfoList(handle);
    }

    internal sealed class SafeVolumeFindHandle : SafeHandleMinusOneIsInvalid
    {
        public SafeVolumeFindHandle() : base(true) { }
        protected override bool ReleaseHandle() => FindVolumeClose(handle);
    }
}
