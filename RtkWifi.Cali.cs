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
        _cali = Task.Run(() =>
        {
            try { RunCalibration(channels, levels, budget, cts.Token); }
            catch (OperationCanceledException) { L("calibration stopped"); }
        });
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
                // holds the gate for the whole step: the driver takes no other set until it reports
                if (!_gate.Wait(GateWait, ct)) return;
                (uint st, byte[] rpt) r;
                try { r = SendUngated(Cmd.TriggerChnlRfk, 1, Bytes.U32((uint)ch, (uint)level), timeoutMs: 300_000); }
                finally { _gate.Release(); }
                var (st, rpt) = r;
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
    /// Stops issuing calibration steps and lets the one in flight finish (the driver rejects other
    /// sets until it reports; steps took under 10 s on the DWA-F18). After <paramref name="wait"/>
    /// the step is abandoned, and commands may then fail until the driver finishes it.
    /// </summary>
    public void StopCalibration(TimeSpan? wait = null)
    {
        if (_caliCts == null) return;
        _caliCts.Cancel();
        if (_cali != null && !_cali.Wait(wait ?? TimeSpan.FromSeconds(60)))
        {
            L("calibration step still running; abandoning it");
            _t?.Abandon(Cmd.TriggerChnlRfk);
            _cali.Wait(TimeSpan.FromSeconds(5));
        }
        _caliCts = null;
        _cali = null;
    }
}
