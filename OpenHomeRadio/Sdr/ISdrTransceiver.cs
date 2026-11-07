namespace OpenHomeRadio.Sdr;

/// <summary>
/// A software-defined radio that can transmit baseband signals and stream received samples. One instance is meant to
/// be shared by every protocol client in a process (for example as a dependency-injection singleton).
/// </summary>
/// <remarks>
/// Implementations must be thread-safe: concurrent <see cref="TransmitAsync"/> calls are serialised, and any number of
/// <see cref="ReceiveAsync"/> enumerations may run at the same time, each seeing the same receive buffers. Receive
/// buffers carry their own centre frequency, so protocol demodulators work on a shared wideband stream.
/// </remarks>
public interface ISdrTransceiver : IAsyncDisposable
{
    /// <summary>
    /// The sample rate, in complex samples per second, of received buffers. Signals passed to <see cref="TransmitAsync"/>
    /// must be generated at this rate.
    /// </summary>
    long SampleRateHz { get; }

    /// <summary>
    /// Transmits <paramref name="signal"/> once, with the transmitter tuned to its <see cref="IqBuffer.CenterFrequencyHz"/>.
    /// Completes when the samples have been sent. Concurrent calls are queued and transmitted one after another.
    /// </summary>
    /// <param name="signal">The baseband signal; its sample rate must equal <see cref="SampleRateHz"/>.</param>
    /// <param name="options">Output power and analog filter settings for this transmission.</param>
    /// <param name="cancellationToken">Cancels waiting for, or performing, the transmission.</param>
    Task TransmitAsync(IqBuffer signal, TransmitOptions options, CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams received sample buffers until <paramref name="cancellationToken"/> is cancelled or the enumeration is
    /// disposed. The receiver runs at a fixed wideband tuning while at least one enumeration is active; every active
    /// enumeration sees the same buffers.
    /// </summary>
    /// <param name="cancellationToken">Ends the enumeration.</param>
    IAsyncEnumerable<IqBuffer> ReceiveAsync(CancellationToken cancellationToken = default);
}

/// <summary>Per-transmission radio settings.</summary>
/// <param name="AttenuationDb">Transmit power back-off in dB; 0 is the radio's maximum output power.</param>
/// <param name="RfBandwidthHz">Bandwidth of the transmitter's analog low-pass filter, in Hz.</param>
public readonly record struct TransmitOptions(double AttenuationDb, long RfBandwidthHz);
