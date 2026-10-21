namespace RtkIhv;

// EDCA (cmd 0x8005). Meta's FB(1) never sent it in the trace; the record layout comes from
// IHVWlanInitEdcaParam / RealtekWiFiSetEdcaParam / IHVWlanSetEdcaParam in re/rtk_decomp.c.
public sealed partial class RtkWifi
{
    // Per AC, 13 u32 (0x34 bytes): idx, enable, band, wmm, ac,
    // aggressive {txop_32us, ecw_max, ecw_min, aifs_us}, moderate {same}
    readonly uint[][] _edca = new uint[4][];
    readonly uint[][] _edcaDefault = new uint[4][];
    readonly bool[] _edcaChanged = new bool[4];

    /// <summary>0x8006 per AC: report {ac, reg}, reg = aifs_us | ecw_min&lt;&lt;8 | ecw_max&lt;&lt;12 | txop_32us&lt;&lt;16.</summary>
    uint InitEdcaDefaults()
    {
        for (uint ac = 0; ac < 4; ac++)
        {
            var (st, rpt) = Send(Cmd.GetDefaultEdcaParam, 1, Bytes.U32(ac));
            if (st != RtkStatus.OK) return st;
            if (Bytes.ReadU32(rpt, 0) != ac) return RtkStatus.RPT_FAIL;
            uint reg = Bytes.ReadU32(rpt, 4);
            uint txop = reg >> 16, ecwMax = (reg >> 12) & 0xF, ecwMin = (reg >> 8) & 0xF, aifs = reg & 0xFF;
            _edcaDefault[ac] = new uint[] { ac, 0, 0, 0, ac, txop, ecwMax, ecwMin, aifs, txop, ecwMax, ecwMin, aifs };
            _edca[ac] = (uint[])_edcaDefault[ac].Clone();
            _edcaChanged[ac] = false;
        }
        return RtkStatus.OK;
    }

    static uint Ecw(uint cw)
    {
        uint n = 0;
        while ((1u << (int)n) < cw + 1) n++;
        return n;
    }

    public uint SetEdcaParam(EdcaSetting e)
    {
        if (_t == null) return RtkStatus.NOT_INITIALIZED;
        if ((uint)e.Ac > 3 || (uint)e.Mode > 2 || e.Aifsn >= 16 || e.TxopUs > 4000 ||
            e.CwMin > e.CwMax || e.CwMax >= 1024)
            return RtkStatus.INVALID_PARAM;
        int ac = (int)e.Ac;
        if (_edca[ac] == null)
        {
            uint st = InitEdcaDefaults();
            if (st != RtkStatus.OK) return st;
        }
        var r = _edca[ac];
        var slot = new[] { (e.TxopUs + 31u) / 32, Ecw(e.CwMax), Ecw(e.CwMin), e.Aifsn * 9u + 16 };
        if (e.Mode != EdcaMode.Moderate) slot.CopyTo(r, 5);
        if (e.Mode != EdcaMode.Aggressive) slot.CopyTo(r, 9);
        r[1] = 1;
        uint rc = SendEdca(r);
        if (rc == RtkStatus.OK) _edcaChanged[ac] = true;
        else r[1] = 0;
        return rc;
    }

    uint SendEdca(uint[] record) => SendExpect(Cmd.SetEdcaParam, 1, Bytes.U32(record));

    void RestoreEdcaDefaults()
    {
        for (int ac = 0; ac < 4; ac++)
        {
            if (!_edcaChanged[ac]) continue;
            if (SendEdca(_edcaDefault[ac]) != RtkStatus.OK) L($"  restore edca ac({ac}) fail");
            _edca[ac] = (uint[])_edcaDefault[ac].Clone();
            _edcaChanged[ac] = false;
        }
    }
}
