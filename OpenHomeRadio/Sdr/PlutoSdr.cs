using System.Globalization;
using System.Runtime.CompilerServices;

namespace OpenHomeRadio.Sdr;

/// <summary>
/// An ADALM-PLUTO software-defined radio, reached over the network through its IIO daemon (pure managed TCP, so it runs
/// on Windows, Linux and Linux arm64 alike). Thread-safe; create one instance per radio and share it.
/// </summary>
/// <remarks>
/// <para>
/// The radio has independent receive and transmit chains, and this class uses one TCP connection for each, so it
/// receives and transmits at the same time. Transmissions are serialised: concurrent <see cref="TransmitAsync"/> calls
/// queue up, and each one tunes the transmitter to its signal's centre frequency.
/// </para>
/// <para>
/// Reception runs one background stream at the fixed wideband tuning of <see cref="PlutoSdrOptions.ReceiveCenterFrequencyHz"/>.
/// It starts when the first <see cref="ReceiveAsync"/> enumeration begins and stops when the last one ends; every
/// active enumeration receives the same buffers. If the connection fails, active enumerations throw and the next
/// enumeration reconnects. Connections are opened lazily, so constructing an instance never blocks.
/// </para>
/// </remarks>
public sealed class PlutoSdr : ISdrTransceiver
{
    private const string PhyDeviceName = "ad9361-phy";
    private const string ReceiveDeviceName = "cf-ad9361-lpc";
    private const string TransmitDeviceName = "cf-ad9361-dds-core-lpc";
    private const string ReceiveChannel = "INPUT voltage0";
    private const string TransmitChannel = "OUTPUT voltage0";
    private const string ReceiveLoChannel = "OUTPUT altvoltage0";
    private const string TransmitLoChannel = "OUTPUT altvoltage1";
    private const string SampleRateAttribute = "sampling_frequency";
    private const string BandwidthAttribute = "rf_bandwidth";
    private const string FrequencyAttribute = "frequency";
    private const string GainAttribute = "hardwaregain";
    private const string GainModeAttribute = "gain_control_mode";
    private const string ManualGainMode = "manual";
    private const string DdsToneAttribute = "raw";
    private const string DdsToneOff = "0";
    private const int DdsToneChannels = 4;
    private const double MaxTransmitAttenuationDb = 89.75;
    private static readonly TimeSpan TransmitDrainMargin = TimeSpan.FromMilliseconds(300);

    private readonly SemaphoreSlim _transmitLock = new(1, 1);
    private readonly ReceiveFanOut _receivers;
    private IiodClient? _transmitter;
    private bool _disposed;

    /// <summary>Creates a radio handle. No connection is made until the first transmit or receive.</summary>
    /// <param name="options">Connection and receive settings; defaults to <see cref="PlutoSdrOptions"/>'s defaults.</param>
    public PlutoSdr(PlutoSdrOptions? options = null)
    {
        Options = options ?? new PlutoSdrOptions();
        _receivers = new ReceiveFanOut(StreamAsync, Options.ReceiveQueueCapacity);
    }

    /// <summary>The settings this instance was created with.</summary>
    public PlutoSdrOptions Options { get; }

    /// <inheritdoc />
    public long SampleRateHz => Options.SampleRateHz;

    /// <inheritdoc />
    public async Task TransmitAsync(IqBuffer signal, TransmitOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(signal);
        if (signal.SampleRateHz != SampleRateHz)
            throw new ArgumentException($"The signal is sampled at {signal.SampleRateHz} Hz but the radio runs at {SampleRateHz} Hz.", nameof(signal));
        ArgumentOutOfRangeException.ThrowIfNegative(options.AttenuationDb, nameof(options));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.AttenuationDb, MaxTransmitAttenuationDb, nameof(options));

        await _transmitLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _transmitter ??= await IiodClient.ConnectAsync(Options.Host, Options.Port, Options.Timeout, cancellationToken).ConfigureAwait(false);
            var phy = _transmitter.GetDeviceId(PhyDeviceName);
            var dds = _transmitter.GetDeviceId(TransmitDeviceName);
            await _transmitter.WriteAttributeAsync(phy, TransmitChannel, SampleRateAttribute, SampleRateHz.ToString(CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
            await _transmitter.WriteAttributeAsync(phy, TransmitChannel, BandwidthAttribute, options.RfBandwidthHz.ToString(CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
            await _transmitter.WriteAttributeAsync(phy, TransmitLoChannel, FrequencyAttribute, signal.CenterFrequencyHz.ToString(CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
            // "0 - x" rather than "-x" so that 0 dB is written as "0", not "-0".
            await _transmitter.WriteAttributeAsync(phy, TransmitChannel, GainAttribute, (0 - options.AttenuationDb).ToString(CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
            for (var tone = 0; tone < DdsToneChannels; tone++) // silence the built-in test-tone generators
                await _transmitter.WriteAttributeAsync(dds, $"OUTPUT altvoltage{tone}", DdsToneAttribute, DdsToneOff, cancellationToken).ConfigureAwait(false);

            await _transmitter.OpenAsync(dds, signal.SampleCount, cancellationToken).ConfigureAwait(false);
            await _transmitter.WriteBufferAsync(dds, signal.Samples, cancellationToken).ConfigureAwait(false);
            await Task.Delay(signal.Duration + TransmitDrainMargin, cancellationToken).ConfigureAwait(false); // let the DMA push the samples out
            await _transmitter.CloseAsync(dds, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // The protocol state is unknown after a failure or cancellation: drop the connection (which also releases
            // the radio's buffer) and reconnect on the next call.
            if (_transmitter != null)
                await _transmitter.DisposeAsync().ConfigureAwait(false);
            _transmitter = null;
            throw;
        }
        finally
        {
            _transmitLock.Release();
        }
    }

    /// <inheritdoc />
    public IAsyncEnumerable<IqBuffer> ReceiveAsync(CancellationToken cancellationToken = default) =>
        _receivers.SubscribeAsync(cancellationToken);

    /// <summary>Stops reception (active receive enumerations end), waits for a running transmission, and closes the connections.</summary>
    public async ValueTask DisposeAsync()
    {
        await _receivers.DisposeAsync().ConfigureAwait(false);
        await _transmitLock.WaitAsync().ConfigureAwait(false);
        try
        {
            _disposed = true;
            if (_transmitter != null)
                await _transmitter.DisposeAsync().ConfigureAwait(false);
            _transmitter = null;
        }
        finally
        {
            _transmitLock.Release();
        }
    }

    private async IAsyncEnumerable<IqBuffer> StreamAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var receiver = await IiodClient.ConnectAsync(Options.Host, Options.Port, Options.Timeout, cancellationToken).ConfigureAwait(false);
        var phy = receiver.GetDeviceId(PhyDeviceName);
        var adc = receiver.GetDeviceId(ReceiveDeviceName);
        await receiver.WriteAttributeAsync(phy, ReceiveChannel, SampleRateAttribute, SampleRateHz.ToString(CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
        await receiver.WriteAttributeAsync(phy, ReceiveChannel, BandwidthAttribute, Options.ReceiveBandwidthHz.ToString(CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
        await receiver.WriteAttributeAsync(phy, ReceiveLoChannel, FrequencyAttribute, Options.ReceiveCenterFrequencyHz.ToString(CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
        await receiver.WriteAttributeAsync(phy, ReceiveChannel, GainModeAttribute, ManualGainMode, cancellationToken).ConfigureAwait(false);
        await receiver.WriteAttributeAsync(phy, ReceiveChannel, GainAttribute, Options.ReceiveGainDb.ToString(CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
        await receiver.OpenAsync(adc, Options.ReceiveBufferSamples, cancellationToken).ConfigureAwait(false);
        while (true)
            yield return new IqBuffer(
                await receiver.ReadBufferAsync(adc, Options.ReceiveBufferSamples, cancellationToken).ConfigureAwait(false),
                Options.ReceiveCenterFrequencyHz,
                SampleRateHz);
    }
}
