namespace OpenHomeRadio.IoHome;

/// <summary>
/// The identity of one single-way IoHome controller ("remote") that <see cref="IoHomeClient"/> transmits as: a system
/// key, a 24-bit address, and the rolling sequence number. Actuators obey commands only from addresses they have
/// learned, signed with the matching key. Thread-safe.
/// </summary>
/// <remarks>
/// <para>
/// Either pair a new identity (your own key from <see cref="IoHomeKeys.Generate"/> and an address of your choice, then
/// <see cref="IoHomeClient.PairAsync"/> with the actuator in learn mode), or act as an existing remote whose key you
/// recovered and whose address you read from its frames — only on installations you own or are authorised to control.
/// </para>
/// <para>
/// <b>Persist <see cref="Sequence"/> after every send.</b> An actuator tracks the highest sequence it has accepted per
/// controller address and rejects anything not above it, so restarting from an old value makes commands silently
/// ignored until the counter climbs past the stored one. When acting as an existing remote, every command you send
/// also advances the actuator's counter past the physical remote's, which is then ignored until its own counter
/// catches up: keep your sequence only slightly above the remote's (read it from a capture). The sequence is 16 bits;
/// avoid running it into the wrap-around.
/// </para>
/// </remarks>
public sealed class IoHomeRemote
{
    /// <summary>The default priority (ACEI) byte of an Execute command, used by roof-window and blind actuators.</summary>
    public const byte DefaultAcei = 0x61;

    /// <summary>The default manufacturer byte of a key transfer.</summary>
    public const byte DefaultManufacturer = 0x01;

    private readonly byte[] _systemKey;
    private int _sequence;

    /// <summary>Creates a controller identity.</summary>
    /// <param name="systemKey">The 16-byte system key. It is copied.</param>
    /// <param name="address">This controller's 24-bit address.</param>
    public IoHomeRemote(ReadOnlySpan<byte> systemKey, uint address)
    {
        if (systemKey.Length != IoHomeCrypto.KeySize)
            throw new ArgumentException($"The system key must be {IoHomeCrypto.KeySize} bytes long.", nameof(systemKey));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(address, IoHomeFrame.MaxAddress);
        _systemKey = systemKey.ToArray();
        Address = address;
    }

    /// <summary>The 16-byte system key the frames are signed with.</summary>
    public ReadOnlyMemory<byte> SystemKey => _systemKey;

    /// <summary>This controller's 24-bit address (the frames' source).</summary>
    public uint Address { get; }

    /// <summary>Destination of commands; the default broadcast reaches every actuator paired to this controller.</summary>
    public uint Destination { get; init; } = IoHomeFrame.BroadcastAddress;

    /// <summary>
    /// The priority (ACEI) byte of Execute commands. <see cref="DefaultAcei"/> (0x61) suits roof-window and blind
    /// actuators; some other actuator families expect 0x43 (see the documentation).
    /// </summary>
    public byte Acei { get; init; } = DefaultAcei;

    /// <summary>The manufacturer byte sent in a key transfer: <see cref="DefaultManufacturer"/> (1) or 2 depending on the actuator family (see the documentation).</summary>
    public byte Manufacturer { get; init; } = DefaultManufacturer;

    /// <summary>Control byte 1 of the frames; see <see cref="IoHomeFrame.ControlByte1"/>.</summary>
    public byte ControlByte1 { get; init; }

    /// <summary>
    /// The sequence number (rolling code) the next transmission will use; each send or pairing uses it once — even
    /// across a whole frequency sweep — and then increments it. Persist it after every send (see the remarks).
    /// </summary>
    public ushort Sequence
    {
        get => (ushort)Volatile.Read(ref _sequence);
        set => Volatile.Write(ref _sequence, value);
    }

    /// <summary>Atomically returns the current sequence and advances it.</summary>
    internal ushort TakeSequence() => (ushort)(Interlocked.Increment(ref _sequence) - 1);
}
