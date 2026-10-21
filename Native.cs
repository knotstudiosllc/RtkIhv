using System.Runtime.InteropServices;

namespace RtkIhv;

/// <summary>The few wlanapi.dll entry points RealtekWiFi.dll uses to reach the driver.</summary>
static unsafe class Native
{
    public const uint WLAN_NOTIFICATION_SOURCE_NONE = 0;
    public const uint WLAN_NOTIFICATION_SOURCE_IHV = 0x40;
    public const uint wlan_ihv_control_type_driver = 1;
    public const uint ERROR_NOT_FOUND = 0x490, ERROR_SERVICE_NOT_ACTIVE = 0x426;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WLAN_INTERFACE_INFO
    {
        public Guid InterfaceGuid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Description;
        public uint State;
    }

    [DllImport("wlanapi.dll")]
    public static extern uint WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiated, out IntPtr handle);

    [DllImport("wlanapi.dll")]
    public static extern uint WlanCloseHandle(IntPtr handle, IntPtr reserved);

    [DllImport("wlanapi.dll")]
    public static extern uint WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);

    [DllImport("wlanapi.dll")]
    public static extern void WlanFreeMemory(IntPtr mem);

    [DllImport("wlanapi.dll")]
    public static extern uint WlanIhvControl(IntPtr handle, Guid* iface, uint type, uint inSize, byte* inBuf,
        uint outSize, byte* outBuf, uint* bytesReturned);

    [DllImport("wlanapi.dll")]
    public static extern uint WlanRegisterNotification(IntPtr handle, uint source, int ignoreDuplicate,
        IntPtr callback, IntPtr context, IntPtr reserved, uint* prevSource);

    [DllImport("wlanapi.dll")]
    public static extern uint WlanDisconnect(IntPtr handle, Guid* iface, IntPtr reserved);

    public static List<WLAN_INTERFACE_INFO> EnumInterfaces(IntPtr handle)
    {
        var result = new List<WLAN_INTERFACE_INFO>();
        if (WlanEnumInterfaces(handle, IntPtr.Zero, out var list) != 0) return result;
        try
        {
            // WLAN_INTERFACE_INFO_LIST: u32 count, u32 index, then the entries
            int count = Marshal.ReadInt32(list);
            int size = Marshal.SizeOf<WLAN_INTERFACE_INFO>();
            for (int i = 0; i < count; i++)
                result.Add(Marshal.PtrToStructure<WLAN_INTERFACE_INFO>(list + 8 + i * size));
        }
        finally { WlanFreeMemory(list); }
        return result;
    }
}
