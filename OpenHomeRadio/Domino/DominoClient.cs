using System.Runtime.CompilerServices;
using OpenHomeRadio.Sdr;

namespace OpenHomeRadio.Domino;

/// <summary>
/// Sends commands to, and receives button presses from, Domino devices through a shared <see cref="ISdrTransceiver"/>.
/// Thread-safe; typically one instance per process. Device names and labels are deliberately not part of this library.
/// </summary>
/// <remarks>
/// The protocol has no pairing, encryption or rolling code: an actuator obeys any frame with a valid CRC whose
/// <see cref="DominoFrame.Counter"/> differs from the last one it saw for that address. Sending therefore works with
/// any button address an actuator has learned, read from a received frame of the real remote.
/// </remarks>
public sealed class DominoClient
{
    // Flag byte of the first frames of a command: Up/Down use one value, every other command another.
    private const byte StepFlag = 0x05;
    private const byte DefaultFlag = 0x18;

    private readonly ISdrTransceiver _sdr;
    private readonly TimeProvider _timeProvider;
    private int _counter;

    /// <summary>Creates a client.</summary>
    /// <param name="sdr">The radio, typically shared with other protocol clients.</param>
    /// <param name="settings">Protocol settings; defaults to <see cref="DominoSettings"/>'s defaults.</param>
    /// <param name="timeProvider">Clock for receive de-duplication; defaults to <see cref="TimeProvider.System"/>.</param>
    public DominoClient(ISdrTransceiver sdr, DominoSettings? settings = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(sdr);
        _sdr = sdr;
        Settings = settings ?? new DominoSettings();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>The protocol settings in use.</summary>
    public DominoSettings Settings { get; }

    /// <summary>
    /// The counter byte the next command will carry; every send uses it and then increments it (wrapping at 255).
    /// </summary>
    /// <remarks>
    /// An actuator ignores a command whose counter equals the last one it saw for that address, so persist this value
    /// after each send and restore it on start-up; otherwise the first command after a restart may be ignored. Any
    /// value different from the last one works, so an exact restore is not critical. The counter is shared by all
    /// addresses; if a real remote uses the same address, its own counter may coincide with ours and one command is
    /// then ignored — sending again fixes it.
    /// </remarks>
    public byte Counter
    {
        get => (byte)Volatile.Read(ref _counter);
        set => Volatile.Write(ref _counter, value);
    }

    /// <summary>Sends one command the way a remote does: a few frames with the primary flag, then more with the flag minus one.</summary>
    /// <param name="device">The target address and group.</param>
    /// <param name="command">The command.</param>
    /// <param name="cancellationToken">Cancels the transmission.</param>
    public Task SendAsync(DominoDevice device, DominoCommand command, CancellationToken cancellationToken = default) =>
        SendAsync(device, command, 0, cancellationToken);

    /// <summary>Sends one command with an argument (for example a lock level for <see cref="DominoCommand.LockLevelSet"/>).</summary>
    /// <param name="device">The target address and group.</param>
    /// <param name="command">The command.</param>
    /// <param name="value">The command's argument (frame byte 11).</param>
    /// <param name="cancellationToken">Cancels the transmission.</param>
    public Task SendAsync(DominoDevice device, DominoCommand command, byte value, CancellationToken cancellationToken = default)
    {
        var counter = (byte)(Interlocked.Increment(ref _counter) - 1);
        var primaryFlag = command is DominoCommand.Up or DominoCommand.Down ? StepFlag : DefaultFlag;
        var frames = new[] { (Flag: primaryFlag, Repeats: Settings.PrimaryFrameRepeats), (Flag: (byte)(primaryFlag - 1), Repeats: Settings.SecondaryFrameRepeats) }
            .SelectMany(variant => Enumerable.Repeat(new DominoFrame
            {
                SystemId = Settings.SystemId,
                Command = command,
                Address = device.Address,
                Group = device.Group,
                Counter = counter,
                Flag = variant.Flag,
                Value = value,
            }, variant.Repeats));
        return _sdr.TransmitAsync(
            DominoModem.Modulate(frames, Settings, _sdr.SampleRateHz),
            new TransmitOptions(Settings.TxAttenuationDb, Settings.TxBandwidthHz),
            cancellationToken);
    }

    /// <summary>
    /// Streams received button presses of the configured system until cancelled. Each physical press is reported once:
    /// the repeated frames of a press (same address, command and counter) are collapsed; a new press, or a held button
    /// that sends a new counter, is reported again.
    /// </summary>
    /// <param name="cancellationToken">Ends the enumeration.</param>
    public async IAsyncEnumerable<DominoFrame> ReceiveAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var recent = new RecentKeys<(uint, DominoCommand, byte)>(Settings.DeduplicationWindow, _timeProvider);
        await foreach (var buffer in _sdr.ReceiveAsync(cancellationToken).ConfigureAwait(false))
            foreach (var frame in DominoModem.Demodulate(buffer, Settings))
                if (frame.SystemId == Settings.SystemId && recent.TryAdd((frame.Address, frame.Command, frame.Counter)))
                    yield return frame;
    }
}
