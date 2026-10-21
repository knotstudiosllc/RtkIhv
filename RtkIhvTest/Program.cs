// RtkIhvTest: checks RtkIhv against the bytes traced from Meta's RealtekWiFi.dll.
//   RtkIhvTest selftest [meta_rtk_config.json]        offline: envelopes and config parsing
//   RtkIhvTest run [log] [--with-hotspot] [--no-usb-switch] [--fb-seconds N]
//                                                      drives the dongle through Air Bridge's sequence
using RtkIhv;

static class Program
{
    static StreamWriter? _log;

    static void Log(string s)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {s}";
        Console.WriteLine(line);
        lock (typeof(Program)) { _log?.WriteLine(line); _log?.Flush(); }
    }

    static int Main(string[] args)
    {
        if (args.FirstOrDefault() == "selftest") return SelfTest(args.Skip(1).FirstOrDefault());
        if (args.FirstOrDefault() == "run") return Run(args.Skip(1).ToArray());
        Console.WriteLine("usage: RtkIhvTest selftest [json] | run [log] [--with-hotspot] [--no-usb-switch] [--fb-seconds N]");
        return 2;
    }

    static int _fails;

    static void Check(string name, bool ok, string detail = "")
    {
        if (!ok) _fails++;
        Log($"{(ok ? "PASS" : "FAIL")} {name} {detail}");
    }

    static byte[] H(string hex) => Convert.FromHexString(hex.Replace(" ", ""));

    static int SelfTest(string? jsonPath)
    {
        // #1 in re/ihv_trace_fb.log: GetDriverVersion
        var req = IhvTransport.Envelope(IhvTransport.ReqSet, IhvTransport.OID_CUSTOM_CMD,
            IhvTransport.CustomPayload(Cmd.GetDriverVersion, 1, 0, Bytes32(0)));
        Check("8032 envelope", req.AsSpan().SequenceEqual(H(
            "38000000 00e04c81 04000000 01000000 08000000 3c000000 a40601ff 2c000000 2c000000 14000000 32800000 01000000 00000000 04000000 00000000")));

        // GetSupportedChannels query: 300-byte payload, header only
        req = IhvTransport.Envelope(IhvTransport.ReqQuery, Oid.SupportedChannels, new byte[300]);
        Check("ff0101bc query header", req.AsSpan(0, 0x28).SequenceEqual(H(
            "50010000 00e04c81 04000000 01000000 07000000 54010000 bc0101ff 44010000 44010000 2c010000")), $"len={req.Length}");

        var c = jsonPath != null ? RtkConfig.FromMetaJson(File.ReadAllText(jsonPath)) : new RtkConfig();
        Check("BCN_OPT_IE bits", c.BeaconIeBits == 0x78, $"0x{c.BeaconIeBits:x2}");
        Check("op channel", c.OpChannel == 36 && c.BandWidth == 2 && c.BandType == 1, $"{c.OpChannel}/{c.BandWidth}/{c.BandType}");
        Check("custom channels", c.CustomChannels.SequenceEqual(new[] { 36, 149 }));
        Check("AMPDU/AMSDU", c.AmpduMaxNum == 64 && c.AmpduMaxTime == 0 && c.AmsduMaxNum == 2);
        Check("cali", c.CaliCenterChannels.SequenceEqual(new[] { 42, 155 }) && c.CaliThermalLevels == 2 && c.CaliTimeoutMs == 180000);
        Check("EDCA parsed", c.Edca.Count == 2 && c.Edca.All(e => e.Ac == EdcaAc.BE && e.CwMax == 7 && e.CwMin == 3 && e.Aifsn == 1 && e.TxopUs == 2000),
            string.Join("; ", c.Edca));
        Log(_fails == 0 ? "selftest OK" : $"selftest: {_fails} failure(s)");
        return _fails == 0 ? 0 : 1;
    }

    static byte[] Bytes32(uint v) => BitConverter.GetBytes(v);

    static int Run(string[] args)
    {
        string logPath = args.FirstOrDefault(a => !a.StartsWith("--") && !int.TryParse(a, out _))
                         ?? Path.Combine(AppContext.BaseDirectory, "rtkihv_run.log");
        _log = new StreamWriter(logPath, append: false);
        bool hotspot = args.Contains("--with-hotspot"), skipUsb = args.Contains("--no-usb-switch");
        int fbSeconds = 5;
        int i = Array.IndexOf(args, "--fb-seconds");
        if (i >= 0 && i + 1 < args.Length) fbSeconds = int.Parse(args[i + 1]);

        using var rtk = new RtkWifi();
        rtk.Log += Log;
        rtk.UsbSwitchState += s => Log($"EVENT usb switch {s}");
        rtk.CalibrationStep += (ch, lvl, o) => Log($"EVENT calibration ch={ch} thermal={lvl} {o}");

        uint rc = rtk.Initialize();
        Log($"<== Initialize {RtkStatus.Name(rc)}");
        if (rc != RtkStatus.OK) return 1;
        if (!skipUsb) Log($"<== UsbSwitch {rtk.UsbSwitchAsync().GetAwaiter().GetResult()}");
        Log($"<== GetCurrentChannel {rtk.GetCurrentChannel()}");
        Log($"<== ConfigSoftAp {RtkStatus.Name(rtk.ConfigSoftAp())}");

        System.Diagnostics.Process? ap = null;
        if (hotspot)
        {
            string script = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\airbridge\hotspot_ap.ps1"));
            string pass = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8));
            ap = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -Ssid RtkTrace_tmp -Passphrase {pass}")
            { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false })!;
            string? line = ap.StandardOutput.ReadLine();
            Log($"hotspot: {line}");
            if (line == null || !line.Contains("started")) { ap = null; Log("hotspot did not start"); }
            else Thread.Sleep(3000);
        }

        Log($"<== FBFeatureConfig(1) {RtkStatus.Name(rtk.FbFeatureConfig(true))}");
        Thread.Sleep(fbSeconds * 1000);
        Log($"<== GetCurrentChannel {rtk.GetCurrentChannel()}");
        Log($"<== FBFeatureConfig(0) {RtkStatus.Name(rtk.FbFeatureConfig(false))}");
        if (ap != null)
        {
            ap.StandardInput.WriteLine();
            Log($"hotspot: {ap.StandardOutput.ReadLine()}");
            ap.WaitForExit(15000);
        }
        Log($"<== Deinitialize {RtkStatus.Name(rtk.Deinitialize())}");
        return 0;
    }
}
