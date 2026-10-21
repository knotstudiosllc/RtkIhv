using System.Text.Json;

namespace RtkIhv;

public enum EdcaAc { BE = 0, BK = 1, VI = 2, VO = 3 }

/// <summary>0 = aggressive slot, 1 = moderate slot, 2 = write both (RealtekWiFiSetEdcaParam's modes).</summary>
public enum EdcaMode { Aggressive = 0, Moderate = 1, Both = 2 }

public record EdcaSetting(EdcaAc Ac, EdcaMode Mode, ushort CwMax, ushort CwMin, byte Aifsn, ushort TxopUs);

/// <summary>
/// The tuning RealtekWiFi.dll reads from its SDKPreConfig JSON. Defaults are the values Meta
/// ships, so no Meta file is needed; <see cref="FromMetaJson"/> reads the same JSON format.
/// </summary>
public sealed class RtkConfig
{
    public int AmpduMaxNum { get; set; } = 64;
    public int AmpduMaxTime { get; set; } = 0;
    public int AmsduMaxNum { get; set; } = 2;

    /// <summary>
    /// Parsed from EDCA_PARAM. Meta's FB(1) was never seen sending these (cmd 0x8005), so they are
    /// only applied when <see cref="ApplyEdcaOnFb"/> is set, or through RtkWifi.SetEdcaParam.
    /// </summary>
    public List<EdcaSetting> Edca { get; set; } = new()
    {
        new(EdcaAc.BE, EdcaMode.Moderate, 7, 3, 1, 2000),
        new(EdcaAc.BE, EdcaMode.Aggressive, 7, 3, 1, 2000),
    };
    public bool ApplyEdcaOnFb { get; set; }

    public int TsfPeriod { get; set; } = 0;

    public int[] CaliCenterChannels { get; set; } = { 42, 155 };
    public int CaliTimeoutMs { get; set; } = 180_000;
    public int CaliThermalLevels { get; set; } = 2;

    public int StatsRptMode { get; set; } = 0;
    public int StatsRptPeriod { get; set; } = 0;

    // BCN_OPT_IE, one bit each in cmd 0x801E's byte (Meta's set gives 0x78)
    public bool IeCountryInfo { get; set; }
    public bool IePowerConstraint { get; set; }
    public bool IeChannelSwitch { get; set; }
    public bool IeQosCapability { get; set; } = true;
    public bool IeVhtCapability { get; set; } = true;
    public bool IeVhtOperation { get; set; } = true;
    public bool IeVhtTxPowerEnvelope { get; set; } = true;

    // BCN_RPT
    public int OpChannel { get; set; } = 36;
    public int BandWidth { get; set; } = 2;      // CHANNEL_WIDTH_20/40/80/160 = 0/1/2/3
    public int BandType { get; set; } = 1;       // BAND_ON_24G/5G/6G = 0/1/2
    public int BeaconRptPeriod { get; set; } = 0;
    public int[] CustomChannels { get; set; } = { 36, 149 };

    // Fixed in RealtekWiFi.dll rather than read from the JSON
    public uint TestMode { get; set; } = 0;
    public uint PopOnFb { get; set; } = 1;
    public uint TxMcsMask { get; set; } = 0x003FF3FF;

    public byte BeaconIeBits =>
        (byte)((IeCountryInfo ? 1 : 0) | (IePowerConstraint ? 2 : 0) | (IeChannelSwitch ? 4 : 0) |
               (IeQosCapability ? 8 : 0) | (IeVhtCapability ? 0x10 : 0) | (IeVhtOperation ? 0x20 : 0) |
               (IeVhtTxPowerEnvelope ? 0x40 : 0));

    /// <summary>Reads Meta's JSON (string values, as embedded in OculusAppFramework.dll). Missing keys keep defaults.</summary>
    public static RtkConfig FromMetaJson(string json)
    {
        var c = new RtkConfig();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        string? S(string section, string key) =>
            root.TryGetProperty(section, out var s) && s.ValueKind == JsonValueKind.Object &&
            s.TryGetProperty(key, out var v) ? v.ToString() : null;
        int I(string section, string key, int dflt) => int.TryParse(S(section, key), out var v) ? v : dflt;
        bool B(string section, string key, bool dflt) => S(section, key) is { } v ? v != "0" : dflt;
        int[] L(string section, string key, int[] dflt) => S(section, key) is { } v
            ? v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(int.Parse).ToArray()
            : dflt;

        c.AmpduMaxNum = I("AMPDU", "max_num", c.AmpduMaxNum);
        c.AmpduMaxTime = I("AMPDU", "max_time", c.AmpduMaxTime);
        c.AmsduMaxNum = I("AMSDU", "max_num", c.AmsduMaxNum);
        c.TsfPeriod = I("TSF", "period", c.TsfPeriod);
        c.CaliCenterChannels = L("COLD_BOOT_CALI", "chnl", c.CaliCenterChannels);
        c.CaliTimeoutMs = I("COLD_BOOT_CALI", "timeout", c.CaliTimeoutMs);
        c.CaliThermalLevels = I("COLD_BOOT_CALI", "thermal_level", c.CaliThermalLevels);
        c.StatsRptMode = I("STATS_RPT", "mode", c.StatsRptMode);
        c.StatsRptPeriod = I("STATS_RPT", "period", c.StatsRptPeriod);
        c.IeCountryInfo = B("BCN_OPT_IE", "country_info", c.IeCountryInfo);
        c.IePowerConstraint = B("BCN_OPT_IE", "power_constraint", c.IePowerConstraint);
        c.IeChannelSwitch = B("BCN_OPT_IE", "channel_switch", c.IeChannelSwitch);
        c.IeQosCapability = B("BCN_OPT_IE", "qos_capability", c.IeQosCapability);
        c.IeVhtCapability = B("BCN_OPT_IE", "vht_capability", c.IeVhtCapability);
        c.IeVhtOperation = B("BCN_OPT_IE", "vht_operation", c.IeVhtOperation);
        c.IeVhtTxPowerEnvelope = B("BCN_OPT_IE", "vht_tx_power_envelope", c.IeVhtTxPowerEnvelope);
        c.OpChannel = I("BCN_RPT", "op_ch", c.OpChannel);
        c.BeaconRptPeriod = I("BCN_RPT", "bcn_per", c.BeaconRptPeriod);
        c.CustomChannels = L("BCN_RPT", "chlist", c.CustomChannels);
        c.BandWidth = S("BCN_RPT", "band_width") switch
        {
            "CHANNEL_WIDTH_20" => 0, "CHANNEL_WIDTH_40" => 1, "CHANNEL_WIDTH_80" => 2, "CHANNEL_WIDTH_160" => 3, _ => c.BandWidth,
        };
        c.BandType = S("BCN_RPT", "band_type") switch
        {
            "BAND_ON_24G" => 0, "BAND_ON_5G" => 1, "BAND_ON_6G" => 2, _ => c.BandType,
        };

        // EDCA_PARAM: {"be": "be_edca_param"} -> {"moderate": "be_moderate_edca", ...} -> {cwmax, cwmin, aifsn, txop}
        if (root.TryGetProperty("EDCA_PARAM", out var edca) && edca.ValueKind == JsonValueKind.Object)
        {
            c.Edca = new();
            foreach (var ac in edca.EnumerateObject())
            {
                if (!Enum.TryParse<EdcaAc>(ac.Name, ignoreCase: true, out var acId)) continue;
                if (!root.TryGetProperty(ac.Value.ToString(), out var modes) || modes.ValueKind != JsonValueKind.Object) continue;
                foreach (var m in modes.EnumerateObject())
                {
                    var mode = m.Name == "aggressive" ? EdcaMode.Aggressive : m.Name == "moderate" ? EdcaMode.Moderate : (EdcaMode?)null;
                    var name = m.Value.ToString();
                    if (mode == null || !root.TryGetProperty(name, out _)) continue;
                    c.Edca.Add(new(acId, mode.Value, (ushort)I(name, "cwmax", 0), (ushort)I(name, "cwmin", 0),
                        (byte)I(name, "aifsn", 0), (ushort)I(name, "txop", 0)));
                }
            }
        }
        return c;
    }
}
