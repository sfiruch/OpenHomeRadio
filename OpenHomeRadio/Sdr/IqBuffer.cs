using System.Buffers.Binary;

namespace OpenHomeRadio.Sdr;

/// <summary>
/// A block of complex baseband samples together with the RF frequency they are centred on and their sample rate.
/// Received buffers come from the SDR's receive stream; transmit signals are produced by the protocol modems with the
/// centre frequency set to the protocol's carrier, so the transmitter knows where to tune.
/// </summary>
/// <remarks>
/// Samples are interleaved 16-bit I and Q values (I0, Q0, I1, Q1, ...), the native format of the ADALM-PLUTO (whose
/// converters use the low 12 bits). A buffer is immutable by convention: the same instance is handed to every receive
/// subscriber, so consumers must not modify the underlying array.
/// </remarks>
public sealed class IqBuffer
{
    private const int ValuesPerSample = 2; // I and Q

    /// <summary>Creates a buffer.</summary>
    /// <param name="samples">Interleaved I/Q samples; the length must be even.</param>
    /// <param name="centerFrequencyHz">The RF frequency that corresponds to 0 Hz in baseband.</param>
    /// <param name="sampleRateHz">Complex samples per second.</param>
    public IqBuffer(ReadOnlyMemory<short> samples, long centerFrequencyHz, long sampleRateHz)
    {
        if (samples.Length % ValuesPerSample != 0)
            throw new ArgumentException("Interleaved I/Q samples must have an even length.", nameof(samples));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRateHz);
        Samples = samples;
        CenterFrequencyHz = centerFrequencyHz;
        SampleRateHz = sampleRateHz;
    }

    /// <summary>Interleaved 16-bit I/Q samples (I0, Q0, I1, Q1, ...).</summary>
    public ReadOnlyMemory<short> Samples { get; }

    /// <summary>The RF frequency, in Hz, that corresponds to 0 Hz in baseband.</summary>
    public long CenterFrequencyHz { get; }

    /// <summary>Complex samples per second.</summary>
    public long SampleRateHz { get; }

    /// <summary>The number of complex (I/Q) samples.</summary>
    public int SampleCount => Samples.Length / ValuesPerSample;

    /// <summary>The time span the samples cover.</summary>
    public TimeSpan Duration => TimeSpan.FromSeconds((double)SampleCount / SampleRateHz);

    /// <summary>A part of this buffer, with the same centre frequency and sample rate, sharing its memory (no copy).</summary>
    /// <param name="startSample">Index of the first complex sample.</param>
    /// <param name="sampleCount">Number of complex samples.</param>
    public IqBuffer Slice(int startSample, int sampleCount) =>
        new(Samples.Slice(startSample * ValuesPerSample, sampleCount * ValuesPerSample), CenterFrequencyHz, SampleRateHz);

    /// <summary>
    /// Reads a capture file of raw interleaved little-endian 16-bit I/Q samples (the common "cs16" format, as written by
    /// <see cref="WriteCs16Async"/>). The file holds no metadata, so the caller states its tuning and sample rate. A
    /// trailing partial sample is ignored. The whole file is loaded into memory (4 bytes per complex sample).
    /// </summary>
    /// <param name="path">The capture file.</param>
    /// <param name="centerFrequencyHz">The RF frequency the capture was tuned to.</param>
    /// <param name="sampleRateHz">The capture's sample rate.</param>
    /// <param name="cancellationToken">Cancels reading.</param>
    public static async Task<IqBuffer> ReadCs16Async(string path, long centerFrequencyHz, long sampleRateHz, CancellationToken cancellationToken = default)
    {
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var samples = new short[bytes.Length / (ValuesPerSample * sizeof(short)) * ValuesPerSample];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(i * sizeof(short)));
        return new IqBuffer(samples, centerFrequencyHz, sampleRateHz);
    }

    /// <summary>
    /// Writes the samples to <paramref name="stream"/> as raw interleaved little-endian 16-bit I/Q ("cs16"). Writing
    /// successive receive buffers to one stream records a capture that <see cref="ReadCs16Async"/> reads back.
    /// </summary>
    /// <param name="stream">The destination, for example a file.</param>
    /// <param name="cancellationToken">Cancels writing.</param>
    public async Task WriteCs16Async(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var bytes = new byte[Samples.Length * sizeof(short)];
        var samples = Samples.Span;
        for (var i = 0; i < samples.Length; i++)
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * sizeof(short)), samples[i]);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }
}
