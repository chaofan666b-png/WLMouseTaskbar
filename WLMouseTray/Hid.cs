using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WLMouseTray;

/// <summary>
/// 极简 HID 访问层：枚举 HID 接口、打开设备、收发 Feature Report。
/// 只用系统自带的 hid.dll / setupapi.dll，无第三方依赖。
/// </summary>
internal static class Hid
{
    private static readonly Guid HidInterfaceGuid =
        new("4d1e55b2-f16f-11cf-88cb-001111000030"); // GUID_DEVINTERFACE_HID

    private const uint DigcfPresent = 0x02;

    internal static class Diagnostics
    {
        public static string? EnumerateError;
    }
    private const uint DigcfDeviceInterface = 0x10;

    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x01;
    private const uint FileShareWrite = 0x02;
    private const uint OpenExisting = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HidAttributes
    {
        public uint Size;
        public ushort VendorID;
        public ushort ProductID;
        public ushort VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HidpCaps
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, IntPtr enumerator,
        IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData,
        ref Guid interfaceClassGuid, uint memberIndex, ref SpDeviceInterfaceData deviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr deviceInfoSet,
        ref SpDeviceInterfaceData deviceInterfaceData, IntPtr deviceInterfaceDetailData,
        uint deviceInterfaceDetailDataSize, out uint requiredSize, IntPtr deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetAttributes(IntPtr device, ref HidAttributes attributes);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetPreparsedData(IntPtr device, out IntPtr preparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

    [DllImport("hid.dll")]
    private static extern int HidP_GetCaps(IntPtr preparsedData, ref HidpCaps capabilities);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_SetFeature(IntPtr device, byte[] buffer, int bufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetFeature(IntPtr device, byte[] buffer, int bufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    /// <summary>一个可用的 HID 接口。</summary>
    public sealed record InterfaceInfo(string Path, ushort Vid, ushort Pid, ushort UsagePage, ushort Usage, ushort FeatureReportLength);

    /// <summary>枚举当前系统上所有 HID 接口。</summary>
    public static List<InterfaceInfo> Enumerate()
    {
        var result = new List<InterfaceInfo>();
        Guid hidGuid = HidInterfaceGuid;
        IntPtr set = SetupDiGetClassDevsW(ref hidGuid, IntPtr.Zero, IntPtr.Zero,
            DigcfPresent | DigcfDeviceInterface);
        if (set == IntPtr.Zero || set == new IntPtr(-1))
        {
            Diagnostics.EnumerateError = $"SetupDiGetClassDevs -> {set} err={Marshal.GetLastWin32Error()}";
            return result;
        }

        try
        {
            for (uint index = 0; ; index++)
            {
                var did = new SpDeviceInterfaceData { cbSize = Marshal.SizeOf<SpDeviceInterfaceData>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, index, ref did))
                {
                    int err = Marshal.GetLastWin32Error();
                    if (index == 0) Diagnostics.EnumerateError = $"EnumDeviceInterfaces err={err} guid={hidGuid} cbSize={did.cbSize}";
                    break;
                }

                SetupDiGetDeviceInterfaceDetailW(set, ref did, IntPtr.Zero, 0, out uint needed, IntPtr.Zero);
                if (needed == 0) continue;

                IntPtr detail = Marshal.AllocHGlobal((int)needed);
                try
                {
                    // SP_DEVICE_INTERFACE_DETAIL_DATA_W.cbSize：x64 为 8，x86 为 6
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetailW(set, ref did, detail, needed, out _, IntPtr.Zero))
                        continue;

                    // SP_DEVICE_INTERFACE_DETAIL_DATA_W: DWORD cbSize，随后紧跟 WCHAR 路径
                    string path = Marshal.PtrToStringUni(IntPtr.Add(detail, 4)) ?? "";
                    if (path.Length == 0) continue;

                    var info = Describe(path);
                    if (info != null) result.Add(info);
                }
                finally
                {
                    Marshal.FreeHGlobal(detail);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }

        return result;
    }

    private static InterfaceInfo? Describe(string path)
    {
        using var handle = OpenReadOnly(path);
        if (handle.IsInvalid) return null;

        IntPtr h = handle.DangerousGetHandle();
        var attrs = new HidAttributes { Size = (uint)Marshal.SizeOf<HidAttributes>() };
        if (!HidD_GetAttributes(h, ref attrs)) return null;

        ushort usage = 0, usagePage = 0, featureLen = 0;
        if (HidD_GetPreparsedData(h, out IntPtr preparsed))
        {
            try
            {
                var caps = new HidpCaps { Reserved = new ushort[17] };
                if (HidP_GetCaps(preparsed, ref caps) == 0x00110000) // HIDP_STATUS_SUCCESS
                {
                    usage = caps.Usage;
                    usagePage = caps.UsagePage;
                    featureLen = caps.FeatureReportByteLength;
                }
            }
            finally
            {
                HidD_FreePreparsedData(preparsed);
            }
        }

        return new InterfaceInfo(path, attrs.VendorID, attrs.ProductID, usagePage, usage, featureLen);
    }

    private static SafeFileHandle OpenReadOnly(string path) =>
        CreateFileW(path, 0, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);

    /// <summary>以读写方式打开 HID 接口（发 Feature Report 需要）。</summary>
    public static SafeFileHandle Open(string path) =>
        CreateFileW(path, GenericRead | GenericWrite, FileShareRead | FileShareWrite,
            IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);

    public static bool SetFeature(SafeFileHandle device, byte[] buffer) =>
        HidD_SetFeature(device.DangerousGetHandle(), buffer, buffer.Length);

    public static bool GetFeature(SafeFileHandle device, byte[] buffer) =>
        HidD_GetFeature(device.DangerousGetHandle(), buffer, buffer.Length);
}
