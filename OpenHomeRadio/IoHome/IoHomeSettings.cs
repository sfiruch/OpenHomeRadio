using OpenHomeRadio.Sdr;

namespace OpenHomeRadio.IoHome;

/// <summary>
/// Radio, framing and behaviour parameters for the IoHome protocol. The defaults are the documented single-way (1W)
/// physical layer: channel 2 at 868.95 MHz, 2-FSK, 38.4 kbit/s, ±19.2 kHz deviation, NRZ with UART-style byte framing,
/// no whitening and no error correction.
/// </summary>
public sealed class IoHomeSettings
{
    private const double DefaultSweepSpanHz = 20_000;
    private const double DefaultSweepStepHz = 2_500;
    private const int ExtraSmoothingSamples = 16;

    /// <summary>
    /// The default transmit sweep: −20 kHz to +20 kHz in 2.5 kHz steps, centred on the nominal carrier.
    /// </summary>
    public static IReadOnlyList<double> DefaultTxFrequencyOffsetsHz { get; } =
        [.. Enumerable.Range(0, (int)(2 * DefaultSweepSpanHz / DefaultSweepStepHz) + 1).Select(step => -DefaultSweepSpanHz + step * DefaultSweepStepHz)];

    /// <summary>Channel centre frequency in Hz. Channel 2 (868.95 MHz) carries all 1W traffic.</summary>
    public long CarrierHz { get; init; } = 868_950_000;

    /// <summary>FSK frequency deviation of each tone from the carrier, in Hz.</summary>
    public double DeviationHz { get; init; } = 19_200;

    /// <summary>Bit rate in bits per second; with NRZ one FSK symbol carries one bit.</summary>
    public double BitRateHz { get; init; } = 38_400;

    /// <summary>Peak transmit I/Q amplitude, of the radio's 2047 full-scale range.</summary>
    public double TxAmplitude { get; init; } = 2000;

    /// <summary>Transmit power back-off in dB. Defaults to 0 (full power): distant actuators need it.</summary>
    public double TxAttenuationDb { get; init; }

    /// <summary>Bandwidth of the transmitter's analog filter, in Hz.</summary>
    public long TxBandwidthHz { get; init; } = 300_000;

    /// <summary>Number of 0x55 preamble bytes sent before the sync word (52 bytes, as real remotes send).</summary>
    public int PreambleBytes { get; init; } = 52;

    /// <summary>How many copies of a frame are sent back to back at each transmit frequency offset.</summary>
    public int FrameRepeats { get; init; } = 6;

    /// <summary>Silent gap between the repeated copies of a frame.</summary>
    public TimeSpan InterFrameGap { get; init; } = TimeSpan.FromMilliseconds(5);

    /// <summary>Silent samples before and after a whole transmission, so the transmitter ramps cleanly.</summary>
    public int LeadTrailSamples { get; init; } = 512;

    /// <summary>
    /// Transmit frequency corrections, in Hz, to send every frame at. An SDR's reference oscillator is typically only
    /// accurate to about ±25 ppm (±22 kHz here), while an actuator's receive window is only a few kHz wide and the
    /// error was not stable between transmissions in testing. So by default the same frame — with one sequence number —
    /// is sent across a sweep of offsets: the actuator acts on the first copy that lands in its window and rejects the
    /// rest as replays, so the whole sweep counts as one command. A single value disables the sweep (use it once you
    /// know your radio's correction). Must not be empty.
    /// </summary>
    public IReadOnlyList<double> TxFrequencyOffsetsHz
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfZero(value.Count);
            field = value;
        }
    } = DefaultTxFrequencyOffsetsHz;

    /// <summary>Pause between the transmissions of a sweep. Other transmissions on the same radio may run in the pauses.</summary>
    public TimeSpan SweepPause { get; init; } = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// Moving-average lengths, in samples, applied to the FM discriminator output while receiving. Each buffer is
    /// demodulated once per value and the results are merged. 1 means no extra smoothing (right for standard remotes);
    /// 16 decodes remotes that transmit with roughly twice the nominal deviation, whose discriminator output otherwise
    /// chatters. Must not be empty.
    /// </summary>
    public IReadOnlyList<int> DiscriminatorSmoothingSamples
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfZero(value.Count);
            field = value;
        }
    } = [FskDsp.NoSmoothing, ExtraSmoothingSamples];

    /// <summary>
    /// Flips which FSK tone means a logical 1, for both transmit and receive. The protocol uses 1 = higher tone; this
    /// is only a diagnostic aid.
    /// </summary>
    public bool Invert { get; init; }

    /// <summary>
    /// Received frames with the same source, sequence and MAC within this window are reported once, collapsing the
    /// repeated copies of one transmission.
    /// </summary>
    public TimeSpan DeduplicationWindow { get; init; } = TimeSpan.FromSeconds(1.5);
}
