namespace RtkIhv;

/// <summary>Return codes, numbered the same as RealtekWiFi.dll's so callers can treat both alike.</summary>
public static class RtkStatus
{
    public const uint OK = 0x00;
    public const uint NOT_INITIALIZED = 0x02;
    public const uint INI_CONFIG_FAIL = 0x04;
    public const uint INVALID_PARAM = 0x06;
    public const uint IHV_UNAVAILABLE = 0x07;   // WlanIhvControl failed (e.g. WLAN AutoConfig off on the dongle)
    public const uint BUSY = 0x08;              // a report for the same command is still outstanding
    public const uint TIMEOUT = 0x09;           // the driver never sent the report
    public const uint RPT_FAIL = 0x0A;          // the report came back with a failure value
    public const uint CALI_RUNNING = 0x0B;
    public const uint FB_NOT_ENABLED = 0x0D;
    public const uint NULL_AP = 0x12;           // SetWifiRole: no soft AP is up yet
    public const uint PENDING = 0x13;

    static readonly Dictionary<uint, string> Names = new()
    {
        [OK] = "OK", [NOT_INITIALIZED] = "NOT_INITIALIZED", [INI_CONFIG_FAIL] = "INI_CONFIG_FAIL",
        [INVALID_PARAM] = "INVALID_PARAM", [IHV_UNAVAILABLE] = "IHV_UNAVAILABLE", [BUSY] = "BUSY",
        [TIMEOUT] = "TIMEOUT", [RPT_FAIL] = "RPT_FAIL", [CALI_RUNNING] = "CALI_RUNNING",
        [FB_NOT_ENABLED] = "FB_NOT_ENABLED", [NULL_AP] = "NULL_AP", [PENDING] = "PENDING",
    };

    public static string Name(uint status) => Names.GetValueOrDefault(status, $"0x{status:x}");
}
