using Microsoft.Extensions.Time.Testing;
using OpenHomeRadio.IoHome;
using OpenHomeRadio.Sdr;

namespace OpenHomeRadio.Tests;

[TestClass]
public sealed class IoHomeClientTests
{
    private const ushort StartSequence = 0x0599;
    private const double QuarterPositionPercent = 25;
    private const int CallCount = 4; // the calls made in Sequence_AdvancesByOnePerSendOrPairing
    private const byte KeyTransferControlByte0 = 0xFC;
    private const byte KeyTransferTag = 0x01;
    private const int MainParameterIndex = 2;
    private const double FrequencyToleranceHz = 10; // 16-bit quantisation; the sweep steps are kHz apart
    private const double EnvelopeFraction = 0.5;
    private const int DefaultSweepSteps = 17;
    private const double DefaultSweepSpanHz = 20_000;
    private static readonly TimeSpan BeyondDeduplicationWindow = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SweepPause = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan JustBeforePause = SweepPause - TimeSpan.FromMilliseconds(1);

    private static readonly IoHomeSettings SweepSettings = new()
    {
        TxFrequencyOffsetsHz = [-20_000, -5_000, 0, 12_500],
        FrameRepeats = 2,
        SweepPause = TimeSpan.Zero,
    };

    private static IoHomeRemote NewRemote() => new(IoHomeFrameTests.SyntheticKey, IoHomeFrameTests.SyntheticAddress) { Sequence = StartSequence };

    private static IoHomeFrame DecodeSingle(IqBuffer signal)
    {
        var frames = IoHomeModem.Demodulate(signal, new IoHomeSettings());
        Assert.AreEqual(1, frames.Count);
        return frames[0];
    }

    private static ushort MainParameter(IoHomeFrame frame) => (ushort)((frame.Data[MainParameterIndex] << 8) | frame.Data[MainParameterIndex + 1]);

    [TestMethod]
    public async Task SendAsync_TransmitsOneSequenceAcrossTheWholeSweep()
    {
        var sdr = new FakeSdr();
        var remote = NewRemote();
        var sent = await new IoHomeClient(sdr, SweepSettings).SendAsync(remote, IoHomeAction.Down);

        Assert.AreEqual(StartSequence, sent.Sequence);
        Assert.AreEqual((ushort)(StartSequence + 1), remote.Sequence);
        Assert.AreEqual((ushort)IoHomeAction.Down, MainParameter(sent));
        Assert.AreEqual(SweepSettings.TxFrequencyOffsetsHz.Count, sdr.Transmissions.Count, "one transmission per offset");
        foreach (var (signal, options) in sdr.Transmissions)
        {
            Assert.AreEqual(SweepSettings.CarrierHz, signal.CenterFrequencyHz);
            Assert.AreEqual(new TransmitOptions(0, SweepSettings.TxBandwidthHz), options, "full power by default");
            var decoded = DecodeSingle(signal);
            CollectionAssert.AreEqual(sent.ToBytes(), decoded.ToBytes());
            Assert.IsTrue(IoHomeKeys.Verify(IoHomeFrameTests.SyntheticKey, decoded));
        }
    }

    [TestMethod]
    public async Task SendAsync_ShiftsEachTransmissionByItsOffset()
    {
        var sdr = new FakeSdr();
        await new IoHomeClient(sdr, SweepSettings).SendAsync(NewRemote(), IoHomeAction.Up);

        // Identical symbols, so the discriminator outputs differ by exactly the offset wherever the signal is present.
        var reference = sdr.Transmissions[SweepSettings.TxFrequencyOffsetsHz.ToList().IndexOf(0)].Signal;
        var (referenceFrequency, magnitude) = FskDsp.Discriminate(reference.Samples.Span, 0, reference.SampleRateHz, FskDsp.NoSmoothing);
        var threshold = magnitude.Max() * EnvelopeFraction; // hoisted out of the filter below, which would otherwise be quadratic
        var present = Enumerable.Range(0, magnitude.Length).Where(n => magnitude[n] > threshold).ToArray();
        for (var i = 0; i < sdr.Transmissions.Count; i++)
        {
            var signal = sdr.Transmissions[i].Signal;
            var (frequency, _) = FskDsp.Discriminate(signal.Samples.Span, 0, signal.SampleRateHz, FskDsp.NoSmoothing);
            Assert.AreEqual(SweepSettings.TxFrequencyOffsetsHz[i], present.Average(n => frequency[n] - referenceFrequency[n]), FrequencyToleranceHz);
        }
    }

    [TestMethod]
    public async Task Sequence_AdvancesByOnePerSendOrPairing()
    {
        var sdr = new FakeSdr();
        var client = new IoHomeClient(sdr, SweepSettings);
        var remote = NewRemote();

        var up = await client.SendAsync(remote, IoHomeAction.Up);
        var stop = await client.SendAsync(remote, IoHomeAction.Stop);
        var pair = await client.PairAsync(remote);
        var position = await client.SendPositionAsync(remote, QuarterPositionPercent);

        CollectionAssert.AreEqual(new ushort[] { StartSequence, StartSequence + 1, StartSequence + 2, StartSequence + 3 },
            new[] { up.Sequence, stop.Sequence, pair.Sequence, position.Sequence });
        Assert.AreEqual((ushort)(StartSequence + CallCount), remote.Sequence);
        Assert.AreEqual(CallCount * SweepSettings.TxFrequencyOffsetsHz.Count, sdr.Transmissions.Count);
        CollectionAssert.AreEqual(
            Enumerable.Range(0, CallCount).SelectMany(call => Enumerable.Repeat((ushort)(StartSequence + call), SweepSettings.TxFrequencyOffsetsHz.Count)).ToArray(),
            sdr.Transmissions.Select(t => DecodeSingle(t.Signal).Sequence).ToArray());
    }

    [TestMethod]
    public async Task PairAsync_SendsADecodableKeyTransfer()
    {
        var sdr = new FakeSdr();
        var sent = await new IoHomeClient(sdr, SweepSettings).PairAsync(NewRemote());

        var bytes = sent.ToBytes();
        Assert.AreEqual(KeyTransferControlByte0, bytes[0]);
        Assert.IsTrue(IoHomeFrame.TryParse(bytes, out var parsed));
        CollectionAssert.AreEqual(bytes, parsed.ToBytes());

        var received = DecodeSingle(sdr.Transmissions[0].Signal);
        CollectionAssert.AreEqual(bytes, received.ToBytes());
        Assert.AreEqual(IoHomeCommand.KeyTransfer1W, received.Command);
        Assert.AreEqual(IoHomeFrame.BroadcastAddress, received.Destination);
        Assert.AreEqual(IoHomeFrameTests.SyntheticAddress, received.Source);
        CollectionAssert.AreEqual(new[] { IoHomeRemote.DefaultManufacturer, KeyTransferTag }, received.Trailer);
        CollectionAssert.AreEqual(IoHomeFrameTests.SyntheticKey, IoHomeKeys.RecoverFromKeyTransfer(received));
        Assert.IsTrue(IoHomeKeys.Verify(IoHomeFrameTests.SyntheticKey, received));
    }

    [TestMethod]
    [DataRow(0.0, (ushort)0x0000)]
    [DataRow(100.0, (ushort)0xC800)]
    [DataRow(50.0, (ushort)0x6400)]
    public async Task SendPositionAsync_MapsPercentToMainParameter(double percent, ushort expected)
    {
        var sent = await new IoHomeClient(new FakeSdr(), SweepSettings).SendPositionAsync(NewRemote(), percent);
        Assert.AreEqual(IoHomeCommand.Execute, sent.Command);
        Assert.AreEqual(expected, MainParameter(sent));
    }

    [TestMethod]
    [DataRow(-0.1)]
    [DataRow(100.1)]
    [DataRow(double.NaN)]
    [DataRow(double.PositiveInfinity)]
    public async Task SendPositionAsync_RejectsOutOfRangeWithoutSending(double percent)
    {
        var sdr = new FakeSdr();
        var remote = NewRemote();
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => new IoHomeClient(sdr, SweepSettings).SendPositionAsync(remote, percent));
        Assert.AreEqual(0, sdr.Transmissions.Count);
        Assert.AreEqual(StartSequence, remote.Sequence);
    }

    [TestMethod]
    public async Task SendAsync_NullRemote_Throws() =>
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(() => new IoHomeClient(new FakeSdr(), SweepSettings).SendAsync(null!, IoHomeAction.Up));

    [TestMethod]
    public async Task Sweep_PausesOnTheTimeProvider()
    {
        var sdr = new FakeSdr();
        var time = new FakeTimeProvider();
        var settings = new IoHomeSettings { TxFrequencyOffsetsHz = [0, 2_500], FrameRepeats = 1, SweepPause = SweepPause };

        // The fake radio completes synchronously, so the first transmission and the pause start before SendAsync returns.
        var sending = new IoHomeClient(sdr, settings, time).SendAsync(NewRemote(), IoHomeAction.Up);
        Assert.AreEqual(1, sdr.Transmissions.Count);
        time.Advance(JustBeforePause);
        Assert.IsFalse(sending.IsCompleted);
        Assert.AreEqual(1, sdr.Transmissions.Count);
        time.Advance(SweepPause - JustBeforePause);
        await sending;
        Assert.AreEqual(2, sdr.Transmissions.Count);
    }

    [TestMethod]
    public async Task ReceiveAsync_ReportsEachTransmissionOnce()
    {
        IqBuffer Transmission(ushort sequence) => TestSignals.Retune(
            IoHomeModem.Modulate(Enumerable.Repeat(IoHomeFrameTests.ExecuteDown(sequence), 2), SweepSettings, TestSignals.SampleRateHz), TestSignals.SharedCenterHz);

        var first = Transmission(StartSequence);
        var time = new FakeTimeProvider();
        IEnumerable<IqBuffer> Script()
        {
            yield return first;  // reported (two copies in the buffer)
            yield return first;  // repeat within the window: collapsed
            time.Advance(BeyondDeduplicationWindow);
            yield return first;  // after the window: reported again
            yield return Transmission(StartSequence + 1); // next sequence: reported
        }

        var frames = await new IoHomeClient(new FakeSdr(Script()), SweepSettings, time).ReceiveAsync().ToListAsync();

        CollectionAssert.AreEqual(new ushort[] { StartSequence, StartSequence, StartSequence + 1 }, frames.Select(f => f.Sequence).ToArray());
    }

    [TestMethod]
    public void DefaultSweep_IsCentredOnZero()
    {
        var offsets = new IoHomeSettings().TxFrequencyOffsetsHz;
        Assert.AreEqual(DefaultSweepSteps, offsets.Count);
        Assert.AreEqual(-DefaultSweepSpanHz, offsets[0]);
        Assert.AreEqual(DefaultSweepSpanHz, offsets[^1]);
        Assert.IsTrue(offsets.Contains(0));
    }

    [TestMethod]
    public void EmptySweep_IsRejected() =>
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new IoHomeSettings { TxFrequencyOffsetsHz = [] });
}
