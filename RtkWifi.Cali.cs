namespace RtkIhv;

public enum CaliOutcome { Done, TempUnreached, Skipped }

// Cold-boot calibration (CWlanColdbootCaliMgnt::coldboot_cali_thread_body): for each thermal
// level, trigger 0x800C {center_chnl, thermal_idx} on every configured channel. A channel whose
// report fails ("temp unreached") is skipped at the higher levels. Runs in the background
// because each step can take minutes (300 s per-command timeout).
public sealed partial class RtkWifi
{
    CancellationTokenSource? _caliCts;
    Task? _cali;

    /// <summary>One calibration step finished (the DLL's 0x40001 callback).</summary>
    public event Action<int, int, CaliOutcome>? CalibrationStep;

    public bool CalibrationRunning => _cali is { IsCompleted: false };

    public uint StartCalibration()
    {
        if (_t == null) return RtkStatus.NOT_INITIALIZED;
        if (CalibrationRunning) return RtkStatus.CALI_RUNNING;
        var cts = _caliCts = new CancellationTokenSource();
        var channels = Config.CaliCenterChannels.ToArray();
        int levels = Math.Min(Config.CaliThermalLevels, 4);
        var budget = TimeSpan.FromMilliseconds(Math.Max(0, Config.CaliTimeoutMs - 350));
        _cali = Task.Run(() => RunCalibration(channels, levels, budget, cts.Token));
        return RtkStatus.PENDING;
    }

    void RunCalibration(int[] channels, int levels, TimeSpan budget, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        var skip = new HashSet<int>();
        for (int level = 0; level < levels; level++)
        {
            foreach (int ch in channels)
            {
                if (ct.IsCancellationRequested || DateTime.UtcNow - started > budget)
                {
                    L(ct.IsCancellationRequested ? "calibration stopped" : "calibration out of time");
                    return;
                }
                if (skip.Contains(ch))
                {
                    L($"calibration: skip (chnl, thermal_i) = ({ch}, {level})");
                    CalibrationStep?.Invoke(ch, level, CaliOutcome.Skipped);
                    continue;
                }
                var (st, rpt) = Send(Cmd.TriggerChnlRfk, 1, Bytes.U32((uint)ch, (uint)level), timeoutMs: 300_000);
                if (ct.IsCancellationRequested) return;
                bool ok = st == RtkStatus.OK && rpt.Length > 0 && rpt[0] == 0;
                if (!ok) skip.Add(ch);
                L($"calibration: (chnl, thermal_i) = ({ch}, {level}) {(ok ? "done" : "temp unreached")}");
                CalibrationStep?.Invoke(ch, level, ok ? CaliOutcome.Done : CaliOutcome.TempUnreached);
            }
        }
        L("calibration done");
    }

    /// <summary>
    /// Stops issuing calibration steps and stops waiting for the one in flight. Unlike the DLL
    /// (which answers busy until that report arrives), later commands can be sent right away.
    /// </summary>
    public void StopCalibration()
    {
        if (_caliCts == null) return;
        _caliCts.Cancel();
        _t?.Abandon(Cmd.TriggerChnlRfk);
        _cali?.Wait(TimeSpan.FromSeconds(5));
        _caliCts = null;
        _cali = null;
    }
}
