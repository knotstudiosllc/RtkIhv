namespace RtkIhv;

// FBFeatureConfig: Meta's low-latency ("FB") mode. Mirrors RealtekWiFiFBFeatureConfig and its
// pre_ini_config / config_ini_setting / post_ini_config / pre_disable_ini_config helpers.
public sealed partial class RtkWifi
{
    // driver defaults read while enabling, used to restore on disable
    uint _ampduDefaultTime, _ampduDefaultNum, _popDefault = uint.MaxValue;
    bool _haveAmpduDefault;

    public uint FbFeatureConfig(bool enable)
    {
        if (_t == null) return RtkStatus.NOT_INITIALIZED;
        L($"FBFeatureConfig({(enable ? 1 : 0)})");
        if (enable)
        {
            uint d = _t.Disconnect(); // the DLL drops any station connection first
            if (d != 0) L($"  WlanDisconnect -> {d}");
        }
        var ch = GetCurrentChannel();
        if (ch != null) L($"  cur_ch({ch.Channel}) cur_bw({ch.Width})");

        if (!enable)
        {
            PreDisableIniConfig();
            uint off = FbSet(0);
            FbEnabled = false;
            return off;
        }

        uint st = FbSet(1);
        if (st != RtkStatus.OK) return st;
        st = PreIniConfig();
        if (st != RtkStatus.OK)
        {
            L($"  ini config fail ret({st}), disable fb feature");
            FbSet(0);
            return st;
        }
        FbEnabled = true;
        ConfigIniSetting();
        PostIniConfig();
        return RtkStatus.OK;
    }

    public uint FbFeatureQuery(out bool enabled)
    {
        var (st, rpt) = Send(Cmd.FBFeatureQuery, 1, Bytes.U32(0));
        enabled = st == RtkStatus.OK && Bytes.ReadU32(rpt) == 1;
        return st;
    }

    /// <summary>0x8001: the report echoes the requested state.</summary>
    uint FbSet(uint on) => SendExpect(Cmd.FBFeatureConfig, 0, Bytes.U32(on), expect: on);

    uint PreIniConfig()
    {
        Send(Cmd.SetTestMode, 0, Bytes.U32(Config.TestMode));

        // SetWifiRole: report 0 = ok, 3 = no soft AP up yet
        var (st, rpt) = Send(Cmd.SetWifiRole, 0, Array.Empty<byte>());
        if (st != RtkStatus.OK) return st;
        uint role = Bytes.ReadU32(rpt);
        if (role == 3) return RtkStatus.NULL_AP;
        if (role != 0) return RtkStatus.RPT_FAIL;

        if ((st = InitEdcaDefaults()) != RtkStatus.OK) return st;

        // 0x800B: {max_time, max_num}; the DLL sends an uninitialised 8-byte buffer here
        (st, rpt) = Send(Cmd.GetAmpduCfg, 0, new byte[8]);
        if (st != RtkStatus.OK) return st;
        _ampduDefaultTime = Bytes.ReadU32(rpt, 0);
        _ampduDefaultNum = Bytes.ReadU32(rpt, 4);
        _haveAmpduDefault = true;

        (st, rpt) = Send(Cmd.GetPdThr, 1, Bytes.U32(0));
        if (st != RtkStatus.OK) return st;
        L($"  PD threshold default {Bytes.ReadU32(rpt)}");

        (st, rpt) = Send(Cmd.GetPop, 1, Bytes.U32(0xFF));
        if (st != RtkStatus.OK) return st;
        _popDefault = Bytes.ReadU32(rpt);
        return RtkStatus.OK;
    }

    /// <summary>Failures here are only logged, as in the DLL (config_ini_setting always reports success).</summary>
    void ConfigIniSetting()
    {
        var c = Config;
        if (c.ApplyEdcaOnFb)
            foreach (var e in c.Edca)
                if (SetEdcaParam(e) != RtkStatus.OK) { L($"  set edca param fail: {e}"); return; }

        if (c.AmpduMaxTime != 0 || c.AmpduMaxNum != 0)
            if (SendExpect(Cmd.AmpduConfig, 0, Bytes.U32((uint)c.AmpduMaxTime, (uint)c.AmpduMaxNum)) != RtkStatus.OK)
                { L("  AMPDU config fail"); return; }

        // 0x802A: the driver reads the first byte; the DLL leaves the other 7 uninitialised
        var amsdu = new byte[8];
        amsdu[0] = (byte)c.AmsduMaxNum;
        if (SendExpect(Cmd.AmsduConfig, 0, amsdu) != RtkStatus.OK) { L("  AMSDU config fail"); return; }

        if (c.TsfPeriod != 0) L("  TSF period set is a no-op in the DLL (\"would not work\"); skipped");
        if (c.BeaconRptPeriod != 0) L("  beacon report period was never traced; skipped");

        // 0x8018: {u32 count, u8 channels[]}, 48 bytes
        var list = new byte[48];
        var chans = c.CustomChannels.Take(44).ToArray();
        list[0] = (byte)chans.Length;
        for (int i = 0; i < chans.Length; i++) list[4 + i] = (byte)chans[i];
        if (SendExpect(Cmd.SetCustomChnlList, 0, list) != RtkStatus.OK) L("  set cus ch fail");
    }

    void PostIniConfig()
    {
        var c = Config;
        if (SendExpect(Cmd.SetPop, 1, Bytes.U32(c.PopOnFb)) != RtkStatus.OK) { L("  IHVWlanSetPOP fail"); return; }
        if (SendExpect(Cmd.SetTxMcsMask, 1, Bytes.U32(c.TxMcsMask)) != RtkStatus.OK) { L("  tx_mask config fail"); return; }
        if (c.StatsRptMode != 0) L("  stats report was never traced; skipped");
        StartCalibration();
    }

    /// <summary>
    /// Undo what enabling changed. The DLL's version never ran in the trace (it returned busy
    /// while calibration was pending), so this restores only the settings we changed and whose
    /// defaults we read: AMPDU, POP and any EDCA we set. AMSDU (driver default 2 = Meta's 2),
    /// PD threshold and the custom channel list are left as they are.
    /// </summary>
    void PreDisableIniConfig()
    {
        StopCalibration();
        if (_haveAmpduDefault)
            if (SendExpect(Cmd.AmpduConfig, 0, Bytes.U32(_ampduDefaultTime, _ampduDefaultNum)) != RtkStatus.OK)
                L("  restore AMPDU fail");
        RestoreEdcaDefaults();
        if (_popDefault != uint.MaxValue)
            if (SendExpect(Cmd.SetPop, 1, Bytes.U32(_popDefault)) != RtkStatus.OK) L("  restore POP fail");
    }
}
