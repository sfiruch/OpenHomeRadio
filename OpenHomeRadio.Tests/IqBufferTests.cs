using OpenHomeRadio.Sdr;

namespace OpenHomeRadio.Tests;

[TestClass]
public sealed class IqBufferTests
{
    private static readonly short[] First = [1, -1, short.MaxValue, short.MinValue];
    private static readonly short[] Second = [0x1234, -0x1234];

    [TestMethod]
    public async Task Cs16_WriteThenRead_RoundTripsAndIgnoresAPartialTrailingSample()
    {
        var path = Path.Combine(Path.GetTempPath(), $"OpenHomeRadio.Tests.{Guid.NewGuid():N}.cs16");
        try
        {
            await using (var file = File.Create(path))
            {
                await new IqBuffer(First, TestSignals.SharedCenterHz, TestSignals.SampleRateHz).WriteCs16Async(file);
                await new IqBuffer(Second, TestSignals.SharedCenterHz, TestSignals.SampleRateHz).WriteCs16Async(file);
                file.Write([0xAA, 0xBB, 0xCC]); // an incomplete I/Q pair
            }
            CollectionAssert.AreEqual(new byte[] { 0x01, 0x00, 0xFF, 0xFF, 0xFF, 0x7F, 0x00, 0x80 }, (await File.ReadAllBytesAsync(path))[..8], "little-endian");

            var read = await IqBuffer.ReadCs16Async(path, TestSignals.SharedCenterHz, TestSignals.SampleRateHz);
            CollectionAssert.AreEqual(First.Concat(Second).ToArray(), read.Samples.ToArray());
            Assert.AreEqual(TestSignals.SharedCenterHz, read.CenterFrequencyHz);
            Assert.AreEqual(TestSignals.SampleRateHz, read.SampleRateHz);
            Assert.AreEqual((First.Length + Second.Length) / 2, read.SampleCount);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void Slice_SelectsComplexSamples()
    {
        var slice = new IqBuffer(First.Concat(Second).ToArray(), TestSignals.SharedCenterHz, TestSignals.SampleRateHz).Slice(1, 2);
        CollectionAssert.AreEqual(new short[] { short.MaxValue, short.MinValue, 0x1234, -0x1234 }, slice.Samples.ToArray());
        Assert.AreEqual(TestSignals.SharedCenterHz, slice.CenterFrequencyHz);
    }

    [TestMethod]
    public void Constructor_RejectsOddLengthAndBadRate()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new IqBuffer(new short[3], TestSignals.SharedCenterHz, TestSignals.SampleRateHz));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new IqBuffer(new short[2], TestSignals.SharedCenterHz, 0));
    }
}
