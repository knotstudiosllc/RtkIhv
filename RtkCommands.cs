using System.Buffers.Binary;

namespace RtkIhv;

/// <summary>Custom-command ids (OID 0xFF0106A4) and the other OIDs RealtekWiFi.dll uses.</summary>
public static class Cmd
{
    public const ushort FBFeatureConfig = 0x8001;
    public const ushort FBFeatureQuery = 0x8002;
    public const ushort SetTestMode = 0x8003;
    public const ushort SetWifiRole = 0x8004;
    public const ushort SetEdcaParam = 0x8005;
    public const ushort GetDefaultEdcaParam = 0x8006;
    public const ushort SetNavPadding = 0x8007;
    public const ushort AmpduConfig = 0x800A;
    public const ushort GetAmpduCfg = 0x800B;
    public const ushort TriggerChnlRfk = 0x800C;
    public const ushort SetPdThr = 0x800D;
    public const ushort GetPdThr = 0x800E;
    public const ushort SetPop = 0x800F;
    public const ushort GetPop = 0x8010;
    public const ushort BeaconOpChannel = 0x8017;
    public const ushort SetCustomChnlList = 0x8018;
    public const ushort IsUseCustomChnlList = 0x8019;
    public const ushort GetPreferredChannel = 0x801A;
    public const ushort GetTsfPer = 0x801C;
    public const ushort BeaconOptIe = 0x801E;
    public const ushort GetBeaconRptPeriod = 0x8023;
    public const ushort SetUsbSwitch = 0x8026;
    public const ushort GetUsbSwitch = 0x8027;
    public const ushort AmsduConfig = 0x802A;
    public const ushort GetAmsduCfg = 0x802B;
    public const ushort SetTxMcsMask = 0x8030;
    public const ushort GetTxMcsMask = 0x8031;
    public const ushort GetDriverVersion = 0x8032;
}

public static class Oid
{
    public const uint SupportedChannels = 0xFF0101BC;
    public const uint SoftApAutoChannel = 0xFF0101C6;
    public const uint CurrentChannel = 0xFF010314;
    public const uint CountryCode = 0xFF81521F;
}

static class Bytes
{
    public static byte[] U32(params uint[] values)
    {
        var b = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(i * 4), values[i]);
        return b;
    }

    public static uint ReadU32(byte[] b, int offset = 0) =>
        b.Length >= offset + 4 ? BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(offset)) :
        b.Length > offset ? b[offset] : uint.MaxValue;

    public static string Hex(ReadOnlySpan<byte> b) => Convert.ToHexString(b).ToLowerInvariant();
}
