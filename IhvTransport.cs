using System.Buffers.Binary;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace RtkIhv;

/// <summary>
/// The one path to the Realtek driver: WlanIhvControl with Realtek's 0x28-byte envelope,
/// plus the IHV notification listener that carries custom-command reports back.
/// Layouts are in re/ihv_protocol.md and match the bytes traced from RealtekWiFi.dll.
/// </summary>
public sealed unsafe class IhvTransport : IDisposable
{
    public const uint OID_CUSTOM_CMD = 0xFF0106A4;

    const uint Signature = 0x814CE000;      // Realtek OUI 00:E0:4C + 0x81
    public const uint ReqQuery = 7, ReqSet = 8;
    const int HeaderLen = 0x28;
    const uint RptEvent = 0xFF008000;       // first dword of every Realtek IHV notification
    const uint RptCustomCmd = 0x00270000;   // third dword when the notification is a custom-command report

    IntPtr _handle;
    Guid _iface;
    GCHandle _self;
    readonly object _lock = new();
    readonly Dictionary<uint, TaskCompletionSource<byte[]>> _pending = new();

    public Guid InterfaceGuid => _iface;
    public string Description { get; private set; }

    public event Action<string>? Log;
    /// <summary>Realtek notifications that are not a report for a waiting command (raw bytes).</summary>
    public event Action<byte[]>? Notification;

    IhvTransport(Guid iface, string description)
    {
        _iface = iface;
        Description = description;
        uint rc = Native.WlanOpenHandle(2, IntPtr.Zero, out _, out _handle);
        if (rc != 0) throw new InvalidOperationException($"WlanOpenHandle failed ({rc})");
        _self = GCHandle.Alloc(this);
        uint prev;
        rc = Native.WlanRegisterNotification(_handle, Native.WLAN_NOTIFICATION_SOURCE_IHV, 0,
            (IntPtr)(delegate* unmanaged<byte*, IntPtr, void>)&OnNotification, GCHandle.ToIntPtr(_self), IntPtr.Zero, &prev);
        if (rc != 0)
        {
            Dispose();
            throw new InvalidOperationException($"WlanRegisterNotification failed ({rc})");
        }
    }

    /// <summary>Opens the Realtek WLAN interface (MAC OUI 00:E0:4C, as RealtekWiFi.dll picks it), or null.</summary>
    public static IhvTransport? Open()
    {
        var found = FindInterface();
        return found == null ? null : new IhvTransport(found.Value.guid, found.Value.description);
    }

    public static (Guid guid, string description)? FindInterface()
    {
        if (Native.WlanOpenHandle(2, IntPtr.Zero, out _, out var h) != 0) return null;
        try
        {
            var ifaces = Native.EnumInterfaces(h);
            var nics = NetworkInterface.GetAllNetworkInterfaces();
            foreach (var i in ifaces)
            {
                var nic = nics.FirstOrDefault(n => Guid.TryParse(n.Id, out var g) && g == i.InterfaceGuid);
                var mac = nic?.GetPhysicalAddress().GetAddressBytes();
                if (mac is { Length: 6 } && mac[0] == 0x00 && mac[1] == 0xE0 && mac[2] == 0x4C)
                    return (i.InterfaceGuid, i.Description);
            }
            // Some Realtek dongles ship with the vendor's own OUI (D-Link's here): fall back to the name
            foreach (var i in ifaces)
                if (i.Description.Contains("Realtek", StringComparison.OrdinalIgnoreCase) ||
                    i.Description.Contains("DWA-F18", StringComparison.OrdinalIgnoreCase))
                    return (i.InterfaceGuid, i.Description);
            return null;
        }
        finally { Native.WlanCloseHandle(h, IntPtr.Zero); }
    }

    /// <summary>The request WlanIhvControl receives (public so tests can compare it with traced bytes).</summary>
    public static byte[] Envelope(uint type, uint oid, ReadOnlySpan<byte> payload)
    {
        int total = HeaderLen + payload.Length;
        var buf = new byte[total];
        var s = buf.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(s[0x00..], (uint)total - 4);
        BinaryPrimitives.WriteUInt32LittleEndian(s[0x04..], Signature);
        BinaryPrimitives.WriteUInt32LittleEndian(s[0x08..], 4);
        BinaryPrimitives.WriteUInt32LittleEndian(s[0x0C..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(s[0x10..], type);
        BinaryPrimitives.WriteUInt32LittleEndian(s[0x14..], (uint)total);
        BinaryPrimitives.WriteUInt32LittleEndian(s[0x18..], oid);
        BinaryPrimitives.WriteUInt32LittleEndian(s[0x1C..], (uint)payload.Length + 0x18);
        BinaryPrimitives.WriteUInt32LittleEndian(s[0x20..], (uint)payload.Length + 0x18);
        BinaryPrimitives.WriteUInt32LittleEndian(s[0x24..], (uint)payload.Length);
        payload.CopyTo(s[HeaderLen..]);
        return buf;
    }

    uint Transmit(byte[] buf, bool query)
    {
        uint rc = TransmitOnce(buf, query);
        if (rc is Native.ERROR_NOT_FOUND or Native.ERROR_SERVICE_NOT_ACTIVE)
        {
            // The dongle re-enumerated (USB switch, replug): look the interface up again and retry once
            var found = FindInterface();
            if (found != null && found.Value.guid != _iface)
            {
                Log?.Invoke($"Realtek interface moved to {found.Value.guid}");
                _iface = found.Value.guid;
                Description = found.Value.description;
            }
            if (found != null) rc = TransmitOnce(buf, query);
        }
        return rc;
    }

    uint TransmitOnce(byte[] buf, bool query)
    {
        Guid g = _iface;
        uint ret = 0;
        fixed (byte* p = buf)
            return Native.WlanIhvControl(_handle, &g, Native.wlan_ihv_control_type_driver, (uint)buf.Length, p,
                query ? (uint)buf.Length : 0, query ? p : null, &ret);
    }

    /// <summary>Set OID. Returns the WlanIhvControl error (0 = sent).</summary>
    public uint Set(uint oid, ReadOnlySpan<byte> payload) => Transmit(Envelope(ReqSet, oid, payload), query: false);

    /// <summary>Query OID: the request buffer doubles as the reply buffer, as in RealtekWiFi.dll.</summary>
    public uint Query(uint oid, Span<byte> payload)
    {
        var buf = Envelope(ReqQuery, oid, payload);
        uint rc = Transmit(buf, query: true);
        if (rc == 0) buf.AsSpan(HeaderLen, payload.Length).CopyTo(payload);
        return rc;
    }

    /// <summary>
    /// Custom command (OID 0xFF0106A4): payload {u32 cmd, u32 a, u32 b, u32 in_len, in}. With a
    /// timeout, waits for the driver's report for the same cmd and returns its data.
    /// </summary>
    public (uint status, byte[] report) Custom(ushort cmd, uint b, ReadOnlySpan<byte> input, int timeoutMs = 10_000, uint a = 1)
    {
        var payload = CustomPayload(cmd, a, b, input);

        TaskCompletionSource<byte[]>? wait = null;
        if (timeoutMs >= 0)
        {
            lock (_lock)
            {
                if (_pending.ContainsKey(cmd)) return (RtkStatus.BUSY, Array.Empty<byte>());
                wait = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
                _pending[cmd] = wait; // registered before sending: the report can beat WlanIhvControl's return
            }
        }
        try
        {
            uint rc = Set(OID_CUSTOM_CMD, payload);
            if (rc != 0)
            {
                Log?.Invoke($"cmd 0x{cmd:x4}: WlanIhvControl error {rc}");
                return (RtkStatus.IHV_UNAVAILABLE, Array.Empty<byte>());
            }
            if (wait == null) return (RtkStatus.OK, Array.Empty<byte>());
            try
            {
                if (!wait.Task.Wait(timeoutMs))
                {
                    Log?.Invoke($"cmd 0x{cmd:x4}: no report within {timeoutMs} ms");
                    return (RtkStatus.TIMEOUT, Array.Empty<byte>());
                }
            }
            catch (AggregateException)
            {
                Log?.Invoke($"cmd 0x{cmd:x4}: wait abandoned");
                return (RtkStatus.TIMEOUT, Array.Empty<byte>());
            }
            return (RtkStatus.OK, wait.Task.Result);
        }
        finally
        {
            if (wait != null) lock (_lock) _pending.Remove(cmd);
        }
    }

    public static byte[] CustomPayload(ushort cmd, uint a, uint b, ReadOnlySpan<byte> input)
    {
        var payload = new byte[16 + input.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0), cmd);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), a);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8), b);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(12), (uint)input.Length);
        input.CopyTo(payload.AsSpan(16));
        return payload;
    }

    /// <summary>Drops a pending wait so that cmd can be sent again (the driver may still answer the old one).</summary>
    public void Abandon(ushort cmd)
    {
        lock (_lock)
            if (_pending.Remove(cmd, out var t)) t.TrySetCanceled();
    }

    // WLAN_NOTIFICATION_DATA: u32 source, u32 code, GUID interface, u32 size, void* data
    [UnmanagedCallersOnly]
    static void OnNotification(byte* nd, IntPtr ctx)
    {
        try
        {
            if (*(uint*)nd != Native.WLAN_NOTIFICATION_SOURCE_IHV) return;
            uint size = *(uint*)(nd + 24);
            byte* data = *(byte**)(nd + 32);
            if (data == null || size == 0) return;
            var self = (IhvTransport)GCHandle.FromIntPtr(ctx).Target!;
            self.Dispatch(new ReadOnlySpan<byte>(data, (int)size).ToArray());
        }
        catch { /* never let an exception unwind into wlanapi */ }
    }

    // Custom-command report: {0xFF008000, size-8, 0x00270000, size-16, cmd, ?, ?, data_len, data}
    void Dispatch(byte[] n)
    {
        if (n.Length >= 32 && BinaryPrimitives.ReadUInt32LittleEndian(n) == RptEvent &&
            BinaryPrimitives.ReadUInt32LittleEndian(n.AsSpan(8)) == RptCustomCmd)
        {
            uint cmd = BinaryPrimitives.ReadUInt32LittleEndian(n.AsSpan(16));
            int len = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(n.AsSpan(28)), (uint)(n.Length - 32));
            TaskCompletionSource<byte[]>? t;
            lock (_lock) _pending.TryGetValue(cmd, out t);
            if (t != null && t.TrySetResult(n.AsSpan(32, len).ToArray())) return;
        }
        Notification?.Invoke(n);
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            uint prev;
            Native.WlanRegisterNotification(_handle, Native.WLAN_NOTIFICATION_SOURCE_NONE, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, &prev);
            Native.WlanCloseHandle(_handle, IntPtr.Zero);
            _handle = IntPtr.Zero;
        }
        lock (_lock)
        {
            foreach (var t in _pending.Values) t.TrySetCanceled();
            _pending.Clear();
        }
        // freed after the handle is closed, so no callback can still be running with this context
        if (_self.IsAllocated) _self.Free();
    }

    /// <summary>Disconnects the station side, as RealtekWiFi.dll does before enabling FB mode.</summary>
    public uint Disconnect()
    {
        Guid g = _iface;
        return Native.WlanDisconnect(_handle, &g, IntPtr.Zero);
    }
}
