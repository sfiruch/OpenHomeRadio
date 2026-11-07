using System.Globalization;
using OpenHomeRadio.Sdr;

namespace OpenHomeRadio.Tests;

/// <summary><see cref="PlutoSdr"/> against an in-process fake IIO daemon on the loopback interface.</summary>
[TestClass]
public sealed class PlutoSdrTests
{
    private const int ReceiveBufferSamples = 256;
    private const int BuffersToRead = 3;
    private const int TransmitSamples = 1000;
    private const long BandwidthHz = 300_000;
    private const string TransmitLoChannel = "OUTPUT altvoltage1";
    private const string ReceiveLoChannel = "OUTPUT altvoltage0";

    private static PlutoSdr Connect(FakeIiodServer server) =>
        new(new PlutoSdrOptions { Host = "127.0.0.1", Port = server.Port, ReceiveBufferSamples = ReceiveBufferSamples });

    private static async Task<IqBuffer[]> TakeAsync(IAsyncEnumerator<IqBuffer> subscriber, ValueTask<bool> firstMove)
    {
        var received = new List<IqBuffer>();
        for (var more = await firstMove; more; more = received.Count < BuffersToRead && await subscriber.MoveNextAsync())
            received.Add(subscriber.Current);
        return [.. received];
    }

    [TestMethod]
    public async Task ConcurrentReceivers_GetTheSameBuffersFromOneStream()
    {
        await using var server = new FakeIiodServer();
        await using (var sdr = Connect(server))
        {
            await using var first = sdr.ReceiveAsync().GetAsyncEnumerator();
            await using var second = sdr.ReceiveAsync().GetAsyncEnumerator();
            var firstMove = first.MoveNextAsync();
            var secondMove = second.MoveNextAsync();
            server.ReleaseReceiveBuffers(BuffersToRead);

            var received = await Task.WhenAll(TakeAsync(first, firstMove), TakeAsync(second, secondMove));

            CollectionAssert.AreEqual(received[0], received[1], "both subscribers get the very same buffer instances");
            CollectionAssert.AreEqual(Enumerable.Range(0, BuffersToRead).ToArray(), received[0].Select(b => (int)b.Samples.Span[0]).ToArray());
            foreach (var buffer in received[0])
            {
                Assert.AreEqual(ReceiveBufferSamples, buffer.SampleCount);
                Assert.AreEqual(TestSignals.SharedCenterHz, buffer.CenterFrequencyHz);
                Assert.AreEqual(TestSignals.SampleRateHz, buffer.SampleRateHz);
            }
        }

        var log = server.Log.ToArray();
        Assert.AreEqual(1, log.Where(e => e.Command.StartsWith("OPEN", StringComparison.Ordinal)).Select(e => e.Connection).Distinct().Count(), "one receive stream");
        Assert.IsTrue(log.Any(e => e.Command.StartsWith($"WRITE {FakeIiodServer.PhyId} {ReceiveLoChannel} frequency", StringComparison.Ordinal)
            && e.Payload == TestSignals.SharedCenterHz.ToString(CultureInfo.InvariantCulture)));
        Assert.IsTrue(log.Any(e => e.Command == $"OPEN {FakeIiodServer.ReceiveId} {ReceiveBufferSamples} 00000003"));
        Assert.IsTrue(log.Any(e => e.Command.Contains("gain_control_mode", StringComparison.Ordinal) && e.Payload == "manual"));
    }

    [TestMethod]
    public async Task ConcurrentTransmissions_AreSerialisedAndEachTunesItsCarrier()
    {
        const long CarrierA = 869_000_000;
        const long CarrierB = 868_950_000;
        const short FillA = 1;
        const short FillB = 2;
        const double AttenuationA = 10;
        await using var server = new FakeIiodServer();
        await using (var sdr = Connect(server))
            await Task.WhenAll(
                sdr.TransmitAsync(new IqBuffer(Enumerable.Repeat(FillA, 2 * TransmitSamples).ToArray(), CarrierA, TestSignals.SampleRateHz), new TransmitOptions(AttenuationA, BandwidthHz)),
                sdr.TransmitAsync(new IqBuffer(Enumerable.Repeat(FillB, 2 * TransmitSamples).ToArray(), CarrierB, TestSignals.SampleRateHz), new TransmitOptions(0, BandwidthHz)));

        Assert.AreEqual(0, server.DoubleOpens);
        var log = server.Log.ToArray();
        Assert.AreEqual(1, log.Select(e => e.Connection).Distinct().Count(), "one transmit connection");
        var bufferCommands = log.Select(e => e.Command.Split(' ')[0]).Where(c => c is "OPEN" or "WRITEBUF" or "CLOSE").ToArray();
        CollectionAssert.AreEqual(new[] { "OPEN", "WRITEBUF", "CLOSE", "OPEN", "WRITEBUF", "CLOSE" }, bufferCommands);

        // At each WRITEBUF, the most recently written LO frequency and gain belong to that signal.
        var written = server.WrittenBuffers.ToArray();
        Assert.AreEqual(2, written.Length);
        var writeIndex = 0;
        string? frequency = null, gain = null;
        foreach (var (_, command, payload) in log)
        {
            if (command.StartsWith($"WRITE {FakeIiodServer.PhyId} {TransmitLoChannel} frequency", StringComparison.Ordinal))
                frequency = payload;
            else if (command.Contains("hardwaregain", StringComparison.Ordinal))
                gain = payload;
            else if (command.StartsWith("WRITEBUF", StringComparison.Ordinal))
            {
                var isA = written[writeIndex++][0] == FillA;
                Assert.AreEqual((isA ? CarrierA : CarrierB).ToString(CultureInfo.InvariantCulture), frequency);
                Assert.AreEqual(isA ? (-AttenuationA).ToString(CultureInfo.InvariantCulture) : "0", gain, "0 dB is written as \"0\", not \"-0\"");
            }
        }
        CollectionAssert.AreEquivalent(new short[] { FillA, FillB }, written.Select(w => w[0]).ToArray());
    }

    [TestMethod]
    public async Task Transmit_WrongSampleRate_IsRejectedBeforeConnecting()
    {
        await using var server = new FakeIiodServer();
        await using (var sdr = Connect(server))
            await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
                sdr.TransmitAsync(new IqBuffer(new short[2], TestSignals.SharedCenterHz, TestSignals.SampleRateHz / 2), new TransmitOptions(0, BandwidthHz)));
        Assert.AreEqual(0, server.Log.Count);
    }
}
