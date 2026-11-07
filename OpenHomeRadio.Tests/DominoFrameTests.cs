using OpenHomeRadio.Domino;

namespace OpenHomeRadio.Tests;

[TestClass]
public sealed class DominoFrameTests
{
    private const int BitsPerByte = 8;

    // Synthetic frames; the expected bytes were computed once with the library and checked field by field.
    private static readonly DominoFrame DefaultSystemFrame = new()
    {
        Command = DominoCommand.ContinuousDown,
        Address = 0x123456,
        Counter = 0x2A,
        Group = 0x20,
        Flag = 0x18,
    };

    private const string DefaultSystemFrameHex = "C30F2277" + "04" + "563412" + "2A" + "20" + "18" + "00" + "FE91";

    private static readonly DominoFrame CustomSystemFrame = new()
    {
        SystemId = 0x01020304,
        Command = DominoCommand.Toggle,
        Address = 0xABCDEF,
        Counter = 0xFF,
        Group = 0x23,
        Flag = 0x17,
        Value = 0x07,
    };

    private const string CustomSystemFrameHex = "01020304" + "06" + "EFCDAB" + "FF" + "23" + "17" + "07" + "5000";

    [TestMethod]
    public void Crc_MatchesCcittFalseCheckValue() =>
        Assert.AreEqual((ushort)0x29B1, DominoFrame.ComputeCrc("123456789"u8));

    [TestMethod]
    public void ToBytes_ProducesKnownVectors()
    {
        Assert.AreEqual(DefaultSystemFrameHex, Convert.ToHexString(DefaultSystemFrame.ToBytes()));
        Assert.AreEqual(CustomSystemFrameHex, Convert.ToHexString(CustomSystemFrame.ToBytes()));
    }

    [TestMethod]
    public void Crc_OfPayload_IsTheTrailingBigEndianWord()
    {
        var bytes = Convert.FromHexString(DefaultSystemFrameHex);
        Assert.AreEqual((ushort)0xFE91, DominoFrame.ComputeCrc(bytes.AsSpan(0, DominoFrame.Length - sizeof(ushort))));
    }

    [TestMethod]
    public void TryParse_RoundTripsKnownVectors()
    {
        Assert.IsTrue(DominoFrame.TryParse(Convert.FromHexString(DefaultSystemFrameHex), out var parsed));
        Assert.AreEqual(DefaultSystemFrame, parsed);
        Assert.AreEqual(DominoSettings.DefaultSystemId, parsed.SystemId);
        Assert.AreEqual(0x123456u, parsed.Address);

        Assert.IsTrue(DominoFrame.TryParse(Convert.FromHexString(CustomSystemFrameHex), out var custom));
        Assert.AreEqual(CustomSystemFrame, custom);
    }

    [TestMethod]
    public void TryParse_RejectsAnySingleBitError()
    {
        var bytes = Convert.FromHexString(DefaultSystemFrameHex);
        for (var bit = 0; bit < bytes.Length * BitsPerByte; bit++)
        {
            var corrupted = (byte[])bytes.Clone();
            corrupted[bit / BitsPerByte] ^= (byte)(1 << (bit % BitsPerByte));
            Assert.IsFalse(DominoFrame.TryParse(corrupted, out var frame), $"bit {bit}");
            Assert.IsNull(frame);
        }
    }

    [TestMethod]
    public void TryParse_RejectsWrongLength()
    {
        var bytes = Convert.FromHexString(DefaultSystemFrameHex);
        Assert.IsFalse(DominoFrame.TryParse(bytes.AsSpan(0, DominoFrame.Length - 1), out _));
        Assert.IsFalse(DominoFrame.TryParse([.. bytes, 0x00], out _));
        Assert.IsFalse(DominoFrame.TryParse([], out _));
    }

    [TestMethod]
    public void Address_Over24Bits_Throws() =>
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new DominoFrame { Address = DominoFrame.MaxAddress + 1 });
}
