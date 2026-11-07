using System.Runtime.CompilerServices;
using OpenHomeRadio.Sdr;

namespace OpenHomeRadio.IoHome;

/// <summary>
/// Sends single-way (1W) IoHome commands as an <see cref="IoHomeRemote"/>, pairs new controllers, and receives frames,
/// through a shared <see cref="ISdrTransceiver"/>. Thread-safe; typically one instance per process.
/// </summary>
/// <remarks>
/// Every transmission is sent across <see cref="IoHomeSettings.TxFrequencyOffsetsHz"/> with a single sequence number,
/// to cope with the radio's frequency error (see there). With the default sweep one command takes several seconds.
/// </remarks>
public sealed class IoHomeClient
{
    private const byte OriginatorUser = 0x01;
    private const byte NoFunctionalParameter = 0x00;
    private const byte KeyTransferTag = 0x01;
    private const int BitsPerByte = 8;
    private const double FullPositionPercent = 100;

    private readonly ISdrTransceiver _sdr;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a client.</summary>
    /// <param name="sdr">The radio, typically shared with other protocol clients.</param>
    /// <param name="settings">Protocol settings; defaults to <see cref="IoHomeSettings"/>'s defaults.</param>
    /// <param name="timeProvider">Clock for the sweep pauses and receive de-duplication; defaults to <see cref="TimeProvider.System"/>.</param>
    public IoHomeClient(ISdrTransceiver sdr, IoHomeSettings? settings = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(sdr);
        _sdr = sdr;
        Settings = settings ?? new IoHomeSettings();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>The protocol settings in use.</summary>
    public IoHomeSettings Settings { get; }

    /// <summary>Moves the actuators paired to <paramref name="remote"/> up/open, down/closed, or stops them.</summary>
    /// <param name="remote">The controller identity to send as; its <see cref="IoHomeRemote.Sequence"/> advances by one.</param>
    /// <param name="action">The movement.</param>
    /// <param name="cancellationToken">Cancels the (remaining) transmission.</param>
    /// <returns>The frame that was sent.</returns>
    public Task<IoHomeFrame> SendAsync(IoHomeRemote remote, IoHomeAction action, CancellationToken cancellationToken = default) =>
        SendMainParameterAsync(remote, (ushort)action, cancellationToken);

    /// <summary>
    /// Moves the actuators paired to <paramref name="remote"/> to a position, 0 % (fully up/open) to 100 % (fully
    /// down/closed). Positions follow the published protocol description; not every actuator supports them.
    /// </summary>
    /// <param name="remote">The controller identity to send as; its <see cref="IoHomeRemote.Sequence"/> advances by one.</param>
    /// <param name="percent">Target position, 0 to 100.</param>
    /// <param name="cancellationToken">Cancels the (remaining) transmission.</param>
    /// <returns>The frame that was sent.</returns>
    public Task<IoHomeFrame> SendPositionAsync(IoHomeRemote remote, double percent, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(percent, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(percent, FullPositionPercent);
        return SendMainParameterAsync(remote, (ushort)Math.Round(percent / FullPositionPercent * (ushort)IoHomeAction.Down), cancellationToken);
    }

    /// <summary>
    /// Pairs <paramref name="remote"/> into every actuator currently in learn mode, by broadcasting its system key in a
    /// single-way key transfer. Put the actuator into learn (pairing) mode first, as described in its manual.
    /// </summary>
    /// <param name="remote">The controller identity to pair; its <see cref="IoHomeRemote.Sequence"/> advances by one.</param>
    /// <param name="cancellationToken">Cancels the (remaining) transmission.</param>
    /// <returns>The frame that was sent.</returns>
    public Task<IoHomeFrame> PairAsync(IoHomeRemote remote, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(remote);
        return TransmitAsync(new IoHomeFrame
        {
            Command = IoHomeCommand.KeyTransfer1W,
            Destination = IoHomeFrame.BroadcastAddress,
            Source = remote.Address,
            ControlByte1 = remote.ControlByte1,
            Sequence = remote.TakeSequence(),
            Data = IoHomeCrypto.Encrypt1WKey(remote.SystemKey.Span, remote.Address),
            Trailer = [remote.Manufacturer, KeyTransferTag],
        }.Sign(remote.SystemKey.Span), cancellationToken);
    }

    /// <summary>
    /// Transmits an already signed frame across the configured frequency sweep. Use this for frames the other methods
    /// do not build; the caller is responsible for the sequence number.
    /// </summary>
    /// <param name="frame">A signed frame (see <see cref="IoHomeFrame.Sign"/>).</param>
    /// <param name="cancellationToken">Cancels the (remaining) transmission.</param>
    /// <returns><paramref name="frame"/>.</returns>
    public async Task<IoHomeFrame> TransmitAsync(IoHomeFrame frame, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var copies = Enumerable.Repeat(frame, Settings.FrameRepeats).ToArray();
        for (var i = 0; i < Settings.TxFrequencyOffsetsHz.Count; i++)
        {
            if (i > 0)
                await Task.Delay(Settings.SweepPause, _timeProvider, cancellationToken).ConfigureAwait(false);
            await _sdr.TransmitAsync(
                IoHomeModem.Modulate(copies, Settings, _sdr.SampleRateHz, Settings.TxFrequencyOffsetsHz[i]),
                new TransmitOptions(Settings.TxAttenuationDb, Settings.TxBandwidthHz),
                cancellationToken).ConfigureAwait(false);
        }
        return frame;
    }

    /// <summary>
    /// Streams received single-way frames until cancelled. The repeated copies of one transmission are reported once.
    /// Frames are CRC-checked but not authenticated; use <see cref="IoHomeKeys.Verify"/> to check a frame's MAC.
    /// </summary>
    /// <param name="cancellationToken">Ends the enumeration.</param>
    public async IAsyncEnumerable<IoHomeFrame> ReceiveAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var recent = new RecentKeys<string>(Settings.DeduplicationWindow, _timeProvider);
        await foreach (var buffer in _sdr.ReceiveAsync(cancellationToken).ConfigureAwait(false))
            foreach (var frame in IoHomeModem.Demodulate(buffer, Settings))
                if (recent.TryAdd($"{frame.Source:X6}:{frame.Sequence:X4}:{Convert.ToHexString(frame.Mac)}"))
                    yield return frame;
    }

    private Task<IoHomeFrame> SendMainParameterAsync(IoHomeRemote remote, ushort mainParameter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(remote);
        return TransmitAsync(new IoHomeFrame
        {
            Command = IoHomeCommand.Execute,
            Destination = remote.Destination,
            Source = remote.Address,
            ControlByte1 = remote.ControlByte1,
            Sequence = remote.TakeSequence(),
            Data = [OriginatorUser, remote.Acei, (byte)(mainParameter >> BitsPerByte), (byte)mainParameter, NoFunctionalParameter, NoFunctionalParameter],
        }.Sign(remote.SystemKey.Span), cancellationToken);
    }
}
