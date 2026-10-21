namespace RtkIhv;

public record ChannelInfo(int Band, int Channel, int Width, byte[] Raw)
{
    public string BandLabel => Band switch { 0 => "2.4 GHz", 1 => "5 GHz", 2 => "6 GHz", _ => $"band {Band}" };
    public int WidthMhz => Width switch { 0 => 20, 1 => 40, 2 => 80, 3 => 160, _ => Width };
}

/// <summary>
/// Drop-in replacement for the parts of Meta's RealtekWiFi.dll that Air Bridge uses:
/// Initialize, UsbSwitch, ConfigSoftAp, FBFeatureConfig, GetCurrentChannelSetting and
/// Deinitialize. Each sends the same driver commands, in the same order, as the DLL did
/// in re/ihv_trace_fb_summary.txt. Not thread-safe: call it from one thread at a time.
/// </summary>
public sealed partial class RtkWifi : IDisposable
{
    public const uint USB_SWITCH_SWITCHING = 0, USB_SWITCH_NOT_NEEDED = 1, USB_SWITCH_DONE = 2,
                      USB_SWITCH_TIMEOUT = 3, USB_SWITCH_ERROR = 4;

    IhvTransport? _t;

    public RtkConfig Config { get; set; }
    public uint DriverVersion { get; private set; }
    public bool FbEnabled { get; private set; }

    public event Action<string>? Log;
    /// <summary>USB 2→3 switch progress (USB_SWITCH_*), the DLL's 0x60001 callback.</summary>
    public event Action<uint>? UsbSwitchState;

    public RtkWifi(RtkConfig? config = null) => Config = config ?? new RtkConfig();

    void L(string s) => Log?.Invoke(s);

    (uint status, byte[] rpt) Send(ushort cmd, uint b, byte[] input, int timeoutMs = 10_000)
    {
        if (_t == null) return (RtkStatus.NOT_INITIALIZED, Array.Empty<byte>());
        var (st, rpt) = _t.Custom(cmd, b, input, timeoutMs);
        L($"  cmd 0x{cmd:x4} in={Bytes.Hex(input)} -> {RtkStatus.Name(st)}{(rpt.Length > 0 ? " rpt=" + Bytes.Hex(rpt) : "")}");
        return (st, rpt);
    }

    /// <summary>Send, then require the report's first value to equal <paramref name="expect"/>.</summary>
    uint SendExpect(ushort cmd, uint b, byte[] input, uint expect = 0)
    {
        var (st, rpt) = Send(cmd, b, input);
        if (st != RtkStatus.OK) return st;
        return Bytes.ReadU32(rpt) == expect ? RtkStatus.OK : RtkStatus.RPT_FAIL;
    }

    public uint Initialize()
    {
        if (_t != null) return RtkStatus.OK;
        try { _t = IhvTransport.Open(); }
        catch (Exception e) { L($"Initialize: {e.Message}"); }
        if (_t == null)
        {
            L("Initialize: no Realtek WLAN interface found");
            return RtkStatus.IHV_UNAVAILABLE;
        }
        _t.Log += L;
        L($"Initialize: {_t.Description} {_t.InterfaceGuid}");

        var (st, rpt) = Send(Cmd.GetDriverVersion, 0, Bytes.U32(0));
        if (st != RtkStatus.OK)
        {
            _t.Dispose();
            _t = null;
            return RtkStatus.IHV_UNAVAILABLE;
        }
        DriverVersion = Bytes.ReadU32(rpt);
        L($"Initialize: driver custom-cmd version 0x{DriverVersion:x8}");
        var (_, fb) = Send(Cmd.FBFeatureQuery, 1, Bytes.U32(0));
        FbEnabled = Bytes.ReadU32(fb) == 1;
        return RtkStatus.OK;
    }

    /// <summary>
    /// USB 2→3 switch (CWlanUsbSwitch::set_usb_switch): read 0x8027; if the dongle is not on
    /// USB 3 yet (0-2), request 3 via 0x8026 and poll once a second, up to 15 times.
    /// Returns a USB_SWITCH_* value; the dongle re-enumerates in between.
    /// </summary>
    public async Task<uint> UsbSwitchAsync(CancellationToken ct = default)
    {
        if (_t == null) return USB_SWITCH_ERROR;
        uint Report(uint s) { UsbSwitchState?.Invoke(s); return s; }

        var (st, rpt) = await Task.Run(() => Send(Cmd.GetUsbSwitch, 0, Bytes.U32(6)), ct);
        uint mode = Bytes.ReadU32(rpt);
        if (st != RtkStatus.OK || mode > 2) return Report(USB_SWITCH_NOT_NEEDED);

        Report(USB_SWITCH_SWITCHING);
        await Task.Run(() => Send(Cmd.SetUsbSwitch, 0, Bytes.U32(3), timeoutMs: -1), ct);
        for (int i = 0; i < 15; i++)
        {
            (st, rpt) = await Task.Run(() => Send(Cmd.GetUsbSwitch, 0, Bytes.U32(6)), ct);
            if (st == RtkStatus.OK && Bytes.ReadU32(rpt) == 3) return Report(USB_SWITCH_DONE);
            await Task.Delay(1000, ct);
        }
        return Report(USB_SWITCH_TIMEOUT);
    }

    /// <summary>Beacon IEs (0x801E), operating channel (0x8017) and soft-AP mode on (OID 0xFF0101C6).</summary>
    public uint ConfigSoftAp()
    {
        if (_t == null) return RtkStatus.NOT_INITIALIZED;
        var c = Config;
        if (SendExpect(Cmd.BeaconOptIe, 0, new[] { c.BeaconIeBits }) != RtkStatus.OK) L("ConfigSoftAp: beacon IE config failed");
        if (SendExpect(Cmd.BeaconOpChannel, 0, Bytes.U32((uint)c.OpChannel, (uint)c.BandWidth, (uint)c.BandType)) != RtkStatus.OK)
            L("ConfigSoftAp: op channel config failed");
        uint rc = _t.Set(Oid.SoftApAutoChannel, Bytes.U32(1));
        L($"  OID 0x{Oid.SoftApAutoChannel:x8} = 1 -> {rc}");
        return rc == 0 ? RtkStatus.OK : RtkStatus.IHV_UNAVAILABLE;
    }

    /// <summary>OID 0xFF010314. Layout: u32 band, u32 ?, u8 channel at +8, u32 width at +12.</summary>
    public ChannelInfo? GetCurrentChannel()
    {
        if (_t == null) return null;
        var buf = new byte[64];
        uint rc = _t.Query(Oid.CurrentChannel, buf);
        if (rc != 0) { L($"GetCurrentChannel: {rc}"); return null; }
        return new ChannelInfo((int)Bytes.ReadU32(buf, 0), buf[8], (int)Bytes.ReadU32(buf, 12), buf);
    }

    public uint Deinitialize()
    {
        StopCalibration();
        if (_t == null) return RtkStatus.OK;
        uint rc = _t.Set(Oid.SoftApAutoChannel, Bytes.U32(0));
        L($"  OID 0x{Oid.SoftApAutoChannel:x8} = 0 -> {rc}");
        _t.Dispose();
        _t = null;
        FbEnabled = false;
        return RtkStatus.OK;
    }

    public void Dispose() => Deinitialize();
}
