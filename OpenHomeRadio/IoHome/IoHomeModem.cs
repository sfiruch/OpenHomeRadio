using System.Numerics;
using System.Text;
using OpenHomeRadio.Sdr;

namespace OpenHomeRadio.IoHome;

/// <summary>
/// Converts between serialised <see cref="IoHomeFrame"/>s and baseband I/Q. On air each frame is a run of 0x55
/// preamble bytes, the sync word <c>FF 33</c> and the frame bytes, every byte sent UART-style (a start bit 0, eight data
/// bits least significant first, a stop bit 1) as NRZ 2-FSK.
/// </summary>
public static class IoHomeModem
{
    private const byte PreambleByte = 0x55;
    private const byte SyncByte0 = 0xFF;
    private const byte SyncByte1 = 0x33;
    private const char StartSymbol = '0';
    private const char StopSymbol = '1';
    private const int BitsPerByte = 8;
    private const int SymbolsPerByte = BitsPerByte + 2; // start + 8 data + stop
    private const int RawSymbolPreview = 200;
    private const int HexPreviewChars = 64;
    private const int ToneLowPercentile = 10;
    private const int ToneHighPercentile = 90;
    private const int PercentScale = 100;
    private const double HzPerKHz = 1000;

    private static readonly string SyncSymbols = ToSymbols([SyncByte0, SyncByte1]);

    /// <summary>
    /// Builds the transmit signal for a sequence of signed frames, each with its own preamble and sync word and followed by
    /// the inter-frame gap. The returned buffer is centred on <see cref="IoHomeSettings.CarrierHz"/>.
    /// </summary>
    /// <param name="frames">Signed frames (see <see cref="IoHomeFrame.Sign"/>), in order.</param>
    /// <param name="settings">Radio and framing parameters.</param>
    /// <param name="sampleRateHz">The transmitter's sample rate (see <see cref="ISdrTransceiver.SampleRateHz"/>).</param>
    /// <param name="frequencyOffsetHz">
    /// A correction added to the whole signal's frequency, to compensate the radio's reference-oscillator error.
    /// </param>
    public static IqBuffer Modulate(IEnumerable<IoHomeFrame> frames, IoHomeSettings settings, long sampleRateHz, double frequencyOffsetHz = 0)
    {
        var samples = new List<Complex>(FskDsp.Silence(settings.LeadTrailSamples));
        var gap = FskDsp.Silence((int)(settings.InterFrameGap.TotalSeconds * sampleRateHz)).ToArray();
        var preamble = ToSymbols(Enumerable.Repeat(PreambleByte, settings.PreambleBytes));
        foreach (var frame in frames)
        {
            var symbols = preamble + SyncSymbols + ToSymbols(frame.ToBytes());
            samples.AddRange(FskDsp.Modulate(settings.Invert ? Flip(symbols) : symbols, sampleRateHz / settings.BitRateHz, settings.DeviationHz, sampleRateHz, settings.TxAmplitude));
            samples.AddRange(gap);
        }
        samples.AddRange(FskDsp.Silence(settings.LeadTrailSamples));
        return new IqBuffer(FskDsp.ToInterleaved(samples, frequencyOffsetHz, sampleRateHz), settings.CarrierHz, sampleRateHz);
    }

    /// <summary>
    /// Finds every distinct CRC-valid single-way frame in a buffer, demodulating once per
    /// <see cref="IoHomeSettings.DiscriminatorSmoothingSamples"/> value. The buffer may be tuned anywhere as long as the
    /// carrier lies within its bandwidth: the offset is computed from <see cref="IqBuffer.CenterFrequencyHz"/>.
    /// </summary>
    /// <param name="buffer">Received (or synthesised) samples.</param>
    /// <param name="settings">Radio parameters.</param>
    /// <returns>The frames in order of discovery, without duplicates. The MACs are not verified.</returns>
    public static IReadOnlyList<IoHomeFrame> Demodulate(IqBuffer buffer, IoHomeSettings settings)
    {
        double offsetHz = settings.CarrierHz - buffer.CenterFrequencyHz;
        if (Math.Abs(offsetHz) + settings.DeviationHz > buffer.SampleRateHz / 2.0)
            return [];

        var frames = new List<IoHomeFrame>();
        var seen = new HashSet<string>();
        foreach (var smoothing in settings.DiscriminatorSmoothingSamples)
        {
            var (frequency, magnitude) = FskDsp.Discriminate(buffer.Samples.Span, offsetHz, buffer.SampleRateHz, smoothing);
            foreach (var (start, end) in FskDsp.FindBursts(magnitude, buffer.SampleRateHz))
            {
                var symbols = FskDsp.SliceSymbols(frequency, start, end, buffer.SampleRateHz / settings.BitRateHz);
                if (settings.Invert)
                    symbols = Flip(symbols);
                for (var at = symbols.IndexOf(SyncSymbols, StringComparison.Ordinal); at >= 0; at = symbols.IndexOf(SyncSymbols, at + SyncSymbols.Length, StringComparison.Ordinal))
                    if (IoHomeFrame.TryParse(ReadUartBytes(symbols, at + SyncSymbols.Length), out var frame)
                        && seen.Add($"{frame.Source:X6}:{frame.Sequence:X4}:{Convert.ToHexString(frame.Mac)}"))
                        frames.Add(frame);
            }
        }
        return frames;
    }

    /// <summary>
    /// Describes what a buffer contains, for bringing up a radio or decoding an unfamiliar remote: the envelope peak and,
    /// per detected burst, its length, frequency offset, tones, the leading sliced symbols, and — at both polarities —
    /// where the sync word was found and the raw bytes after it. Tells "no signal" apart from "signal but not decodable".
    /// </summary>
    /// <param name="buffer">Received samples.</param>
    /// <param name="settings">Radio parameters; the first <see cref="IoHomeSettings.DiscriminatorSmoothingSamples"/> value is used.</param>
    /// <returns>Human-readable report lines.</returns>
    public static IReadOnlyList<string> Diagnose(IqBuffer buffer, IoHomeSettings settings)
    {
        var samplesPerSymbol = buffer.SampleRateHz / settings.BitRateHz;
        var (frequency, magnitude) = FskDsp.Discriminate(buffer.Samples.Span, settings.CarrierHz - buffer.CenterFrequencyHz, buffer.SampleRateHz, settings.DiscriminatorSmoothingSamples[0]);
        var bursts = FskDsp.FindBursts(magnitude, buffer.SampleRateHz).ToList();
        var report = new List<string> { $"samples={magnitude.Length} peakEnvelope={(magnitude.Length == 0 ? 0 : magnitude.Max()):F0} bursts={bursts.Count}" };
        foreach (var (start, end) in bursts)
        {
            var sorted = frequency[start..end].Order().ToArray();
            var lowTone = sorted[sorted.Length * ToneLowPercentile / PercentScale] / HzPerKHz;
            var highTone = sorted[sorted.Length * ToneHighPercentile / PercentScale] / HzPerKHz;
            report.Add($"  burst {start}-{end} (~{(end - start) / samplesPerSymbol:F0} symbols) offset={sorted.Average() / HzPerKHz:F1} kHz tones=[{lowTone:F1}, {highTone:F1}] kHz deviation={(highTone - lowTone) / 2:F1} kHz");
            var symbols = FskDsp.SliceSymbols(frequency, start, end, samplesPerSymbol);
            report.Add($"    symbols={symbols[..Math.Min(symbols.Length, RawSymbolPreview)]}");
            foreach (var inverted in (bool[])[false, true])
            {
                var stream = inverted ? Flip(symbols) : symbols;
                var at = stream.IndexOf(SyncSymbols, StringComparison.Ordinal);
                var bytes = at >= 0 ? Convert.ToHexString(ReadUartBytes(stream, at + SyncSymbols.Length)) : "(no sync word)";
                report.Add($"    invert={inverted} sync@{at} bytes={bytes[..Math.Min(bytes.Length, HexPreviewChars)]}");
            }
        }
        return report;
    }

    private static string ToSymbols(IEnumerable<byte> bytes)
    {
        var symbols = new StringBuilder();
        foreach (var value in bytes)
        {
            symbols.Append(StartSymbol);
            for (var bit = 0; bit < BitsPerByte; bit++) // least significant bit first
                symbols.Append(((value >> bit) & 1) == 1 ? '1' : '0');
            symbols.Append(StopSymbol);
        }
        return symbols.ToString();
    }

    private static string Flip(string symbols) => string.Create(symbols.Length, symbols, (span, source) =>
    {
        for (var i = 0; i < span.Length; i++)
            span[i] = source[i] == '1' ? '0' : '1';
    });

    private static byte[] ReadUartBytes(string symbols, int start)
    {
        var bytes = new List<byte>();
        for (var pos = start; pos + SymbolsPerByte <= symbols.Length && symbols[pos] == StartSymbol; pos += SymbolsPerByte)
        {
            byte value = 0;
            for (var bit = 0; bit < BitsPerByte; bit++)
                if (symbols[pos + 1 + bit] == '1')
                    value |= (byte)(1 << bit);
            bytes.Add(value);
        }
        return [.. bytes];
    }
}
