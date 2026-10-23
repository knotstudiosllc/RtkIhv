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

    // While a calibration step (0x800C) is in flight the driver refuses every other set with
    // ERROR_NOT_READY, so commands take turns, as with the DLL's single outstanding command.
    readonly SemaphoreSlim _gate = new(1, 1);
    static readonly TimeSpan GateWait = TimeSpan.FromSeconds(310); // one calibration step can take 300 s

    (uint status, byte[] rpt) Send(ushort cmd, uint b, byte[] input, int timeoutMs = 10_000)
    {
        if (!_gate.Wait(GateWait)) return (RtkStatus.BUSY, Array.Empty<byte>());
        try { return SendUngated(cmd, b, input, timeoutMs); }
        finally { _gate.Release(); }
    }

    (uint status, byte[] rpt) SendUngated(ushort cmd, uint b, byte[] input, int timeoutMs)
    {
        if (_t == null) return (RtkStatus.NOT_INITIALIZED, Array.Empty<byte>());
        var (st, rpt) = _t.Custom(cmd, b, input, timeoutMs);
        L($"  cmd 0x{cmd:x4} in={Bytes.Hex(input)} -> {RtkStatus.Name(st)}{(rpt.Length > 0 ? " rpt=" + Bytes.Hex(rpt) : "")}");
        return (st, rpt);
    }

    uint SetOid(uint oid, uint value)
    {
        if (_t == null) return RtkStatus.NOT_INITIALIZED;
        if (!_gate.Wait(GateWait)) return RtkStatus.BUSY;
        try
        {
            uint rc = _t.Set(oid, Bytes.U32(value));
            L($"  OID 0x{oid:x8} = {value} -> {rc}");
            return IhvTransport.ToStatus(rc);
        }
        finally { _gate.Release(); }
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
        if (FbEnabled)
        {
            // FB left on by an earlier session (a kill, a crash, a replug mid-run). The DLL logs
            // "PREV_CFG_EXIST. Trying to recover.", turns FB off, waits a second and queries again.
            // It fails Initialize if FB is still on; we report it through FbEnabled instead.
            L("Initialize: FB mode left on by a previous session; turning it off");
            FbFeatureConfig(false);
            Thread.Sleep(1000);
            (_, fb) = Send(Cmd.FBFeatureQuery, 1, Bytes.U32(0));
            FbEnabled = Bytes.ReadU32(fb) == 1;
            if (FbEnabled) { L("Initialize: FB mode is still on"); return RtkStatus.OK; }
        }
        DisconnectStation("Initialize");
        return RtkStatus.OK;
    }

    /// <summary>
    /// WlanDisconnect on the dongle (the DLL's sub_2f470, also exported as RealtekWiFiWlanDisconnect).
    /// The DLL calls it at the end of Initialize and ConfigSoftAp and before FB(1): a station
    /// connection on the same radio (Windows reconnecting the dongle to a home network) holds it
    /// on that network's channel, and the AP pinned to another channel then never beacons.
    /// </summary>
    void DisconnectStation(string step)
    {
        if (_t == null) return;
        uint d = _t.Disconnect();
        L($"{step}: WlanDisconnect -> {d}");
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
        uint rc = SetOid(Oid.SoftApAutoChannel, 1);
        DisconnectStation("ConfigSoftAp");
        return rc;
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

    /// <summary>Read-only dump of the FB-related driver state (queries only; changes nothing).</summary>
    public string Snapshot()
    {
        if (_t == null) return "not initialized";
        uint U(ushort cmd, uint b, byte[] input, int off = 0) => Send(cmd, b, input) is (RtkStatus.OK, var r) ? Bytes.ReadU32(r, off) : uint.MaxValue;
        var ch = GetCurrentChannel();
        return $"fb={U(Cmd.FBFeatureQuery, 1, Bytes.U32(0))} ch={ch?.Channel} bw={ch?.WidthMhz} " +
               $"pd_thr={U(Cmd.GetPdThr, 1, Bytes.U32(0))} pop={U(Cmd.GetPop, 1, Bytes.U32(0xFF))} " +
               $"ampdu=({U(Cmd.GetAmpduCfg, 0, new byte[8])},{U(Cmd.GetAmpduCfg, 0, new byte[8], 4)}) " +
               $"amsdu={U(Cmd.GetAmsduCfg, 0, new byte[8])} txmask=0x{U(Cmd.GetTxMcsMask, 1, Bytes.U32(0)):x} " +
               $"cus_chlist={U(Cmd.IsUseCustomChnlList, 1, Bytes.U32(0))}";
    }

    public uint Deinitialize()
    {
        StopCalibration();
        if (_t == null) return RtkStatus.OK;
        SetOid(Oid.SoftApAutoChannel, 0);
        _t.Dispose();
        _t = null;
        FbEnabled = false;
        return RtkStatus.OK;
    }

    public void Dispose() => Deinitialize();
}
