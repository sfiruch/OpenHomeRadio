namespace OpenHomeRadio.Domino;

/// <summary>
/// Radio, framing and behaviour parameters for the Domino protocol. The defaults match the system as measured:
/// 869.000 MHz, 2-FSK with ±48 kHz deviation at 100 kchip/s, Manchester coded (50 kbit/s).
/// </summary>
public sealed class DominoSettings
{
    /// <summary>
    /// The system identifier observed in frame bytes 0–3 (<c>C3 0F 22 77</c>). It was identical on every remote examined
    /// so far, but it is an observed value and may be installation-specific.
    /// </summary>
    public const uint DefaultSystemId = 0xC30F2277;

    /// <summary>Channel centre frequency in Hz.</summary>
    public long CarrierHz { get; init; } = 869_000_000;

    /// <summary>FSK frequency deviation of each tone from the carrier, in Hz.</summary>
    public double DeviationHz { get; init; } = 48_000;

    /// <summary>Chip rate in chips per second. Manchester coding halves this to the data rate.</summary>
    public double ChipRateHz { get; init; } = 100_000;

    /// <summary>
    /// The system identifier (frame bytes 0–3, most significant byte first) used for sending; received frames with a
    /// different value are ignored by <see cref="DominoClient"/>. Defaults to <see cref="DefaultSystemId"/>.
    /// </summary>
    public uint SystemId { get; init; } = DefaultSystemId;

    /// <summary>Peak transmit I/Q amplitude, of the radio's 2047 full-scale range.</summary>
    public double TxAmplitude { get; init; } = 2000;

    /// <summary>Transmit power back-off in dB (0 = maximum output). 10 dB was sufficient indoors.</summary>
    public double TxAttenuationDb { get; init; } = 10;

    /// <summary>Bandwidth of the transmitter's analog filter, in Hz.</summary>
    public long TxBandwidthHz { get; init; } = 1_500_000;

    /// <summary>Number of alternating "10" chip pairs sent before the sync chips, for the receiver to lock onto.</summary>
    public int PreamblePairs { get; init; } = 32;

    /// <summary>Fixed chips between the preamble and the payload, as emitted by real remotes.</summary>
    public string SyncChips { get; init; } = "110011001";

    /// <summary>Silent gap between the repeated frames of one command.</summary>
    public TimeSpan InterFrameGap { get; init; } = TimeSpan.FromMilliseconds(10);

    /// <summary>Silent samples before and after a whole transmission, so the transmitter ramps cleanly.</summary>
    public int LeadTrailSamples { get; init; } = 512;

    /// <summary>A command is sent as this many frames with the primary <see cref="DominoFrame.Flag"/> value...</summary>
    public int PrimaryFrameRepeats { get; init; } = 3;

    /// <summary>...followed by this many frames with the flag decremented by one, as real remotes do.</summary>
    public int SecondaryFrameRepeats { get; init; } = 5;

    /// <summary>
    /// Received frames with the same address, command and counter within this window are reported once, collapsing the
    /// repeated frames of one button press.
    /// </summary>
    public TimeSpan DeduplicationWindow { get; init; } = TimeSpan.FromSeconds(1.5);
}
