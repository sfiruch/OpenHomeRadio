namespace OpenHomeRadio.Sdr;

/// <summary>
/// Connection and receive settings for <see cref="PlutoSdr"/>. The defaults suit the 868 MHz short-range-device band:
/// one wideband receive tuning at 868.75 MHz covers both the 868.95 MHz and the 869.0 MHz channels, so a single radio
/// can listen to several protocols at once. Can be bound from configuration (e.g. the options pattern).
/// </summary>
public sealed class PlutoSdrOptions
{
    /// <summary>The ADALM-PLUTO's factory-default USB network address.</summary>
    public const string DefaultHost = "192.168.2.1";

    /// <summary>The TCP port of the IIO daemon (iiod).</summary>
    public const int DefaultPort = 30431;

    /// <summary>Host name or IP address of the radio.</summary>
    public string Host { get; init; } = DefaultHost;

    /// <summary>TCP port of the radio's IIO daemon.</summary>
    public int Port { get; init; } = DefaultPort;

    /// <summary>
    /// Baseband sample rate for both receive and transmit, in complex samples per second. The radio uses one rate for
    /// both directions, so all transmit signals are generated at this rate. Must be at least about 2.1 MS/s, the
    /// lowest rate the radio supports without extra decimation filters.
    /// </summary>
    public long SampleRateHz { get; init; } = 2_500_000;

    /// <summary>
    /// The fixed receive tuning, in Hz. Keep every protocol's channel inside ±(sample rate / 2), and away from the exact
    /// centre where the radio has a DC spur. The default sits 200 kHz and 250 kHz below the two supported channels.
    /// </summary>
    public long ReceiveCenterFrequencyHz { get; init; } = 868_750_000;

    /// <summary>Bandwidth of the receiver's analog filter, in Hz.</summary>
    public long ReceiveBandwidthHz { get; init; } = 1_000_000;

    /// <summary>Fixed receiver gain in dB (automatic gain control is disabled so bursts are not clipped while it settles).</summary>
    public int ReceiveGainDb { get; init; } = 40;

    /// <summary>
    /// Complex samples per receive buffer. The default (262 144) is about 0.1 s at 2.5 MS/s. Demodulation works per
    /// buffer, so a burst cut by a buffer boundary is lost; the protocols repeat every frame several times for this reason.
    /// </summary>
    public int ReceiveBufferSamples { get; init; } = 1 << 18;

    /// <summary>
    /// How many buffers each receive subscriber may fall behind before its oldest unread buffer is dropped. Dropping
    /// keeps one slow consumer from stalling the radio or the other subscribers.
    /// </summary>
    public int ReceiveQueueCapacity { get; init; } = 16;

    /// <summary>How long to wait for the radio to answer any single request before failing with a <see cref="TimeoutException"/>.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);
}
