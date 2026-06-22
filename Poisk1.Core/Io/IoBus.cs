namespace Poisk1.Core.Io;

/// <summary>
/// I/O port dispatcher. Devices register themselves for specific ports.
/// Accesses to unregistered ports are logged (handy while bringing up POST).
/// </summary>
public sealed class IoBus
{
    private readonly Dictionary<ushort, IIoDevice> _handlers = new();
    private readonly HashSet<ushort> _loggedPorts = new(); // so we don't spam the log

    /// <summary>Tracing hook (optional). Invoked when an unknown port is accessed.</summary>
    public Action<string>? Trace { get; set; }

    private void LogUnknown(string msg, ushort port)
    {
        if (_loggedPorts.Add(port)) Trace?.Invoke(msg); // only once per port
    }

    public void Register(IIoDevice device, params ushort[] ports)
    {
        foreach (var port in ports)
            _handlers[port] = device;
    }

    /// <summary>Register a range of ports [from..to] inclusive.</summary>
    public void RegisterRange(IIoDevice device, ushort from, ushort to)
    {
        for (int p = from; p <= to; p++)
            _handlers[(ushort)p] = device;
    }

    /// <summary>Unregister a range of ports [from..to] (for expansion cards).</summary>
    public void UnregisterRange(ushort from, ushort to)
    {
        for (int p = from; p <= to; p++)
            _handlers.Remove((ushort)p);
    }

    /// <summary>A device (e.g. the FDC) requested a wait-state: the CPU must retry the current IN
    /// instruction (stall until the device is ready). Cleared at the start of every read.</summary>
    public bool PendingStall;

    public byte ReadByte(ushort port)
    {
        PendingStall = false;
        if (_handlers.TryGetValue(port, out var dev))
            return dev.ReadByte(port);
        LogUnknown($"IO RD  unknown port 0x{port:X4} -> 0xFF", port);
        return 0xFF;
    }

    public void WriteByte(ushort port, byte value)
    {
        if (_handlers.TryGetValue(port, out var dev))
        {
            dev.WriteByte(port, value);
            return;
        }
        LogUnknown($"IO WR  unknown port 0x{port:X4} <- 0x{value:X2}", port);
    }

    public ushort ReadWord(ushort port)
        => (ushort)(ReadByte(port) | (ReadByte((ushort)(port + 1)) << 8));

    public void WriteWord(ushort port, ushort value)
    {
        WriteByte(port, (byte)(value & 0xFF));
        WriteByte((ushort)(port + 1), (byte)(value >> 8));
    }
}
