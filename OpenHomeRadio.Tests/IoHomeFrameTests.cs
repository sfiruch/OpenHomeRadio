using OpenHomeRadio.IoHome;

namespace OpenHomeRadio.Tests;

[TestClass]
public sealed class IoHomeFrameTests
{
    internal static readonly byte[] SyntheticKey = Convert.FromHexString("000102030405060708090A0B0C0D0E0F");
    internal const uint SyntheticAddress = 0xABCDEF;
    private const ushort SyntheticSequence = 0x0042;
    private const int CommandByteIndex = 8;
    private const byte FlipTopBit = 0x80;
    private const byte SingleWayBit = 0x20; // control byte 0, bit 5

    // Computed once with the library and checked field by field: control byte 0 (first+last, 1W, length 22),
    // control byte 1, destination, source, Execute, originator/ACEI/Down/FP1/FP2, sequence, MAC, CRC (low byte first).
    private const string ExecuteDownHex = "F6" + "00" + "00003F" + "ABCDEF" + "00" + "0161C8000000" + "0042" + "AAC28281E20A" + "9C73";

    internal static IoHomeFrame ExecuteDown(ushort sequence = SyntheticSequence) => new IoHomeFrame
    {
        Command = IoHomeCommand.Execute,
        Destination = IoHomeFrame.BroadcastAddress,
        Source = SyntheticAddress,
        Sequence = sequence,
        Data = [0x01, IoHomeRemote.DefaultAcei, (byte)((ushort)IoHomeAction.Down >> 8), unchecked((byte)IoHomeAction.Down), 0x00, 0x00],
    }.Sign(SyntheticKey);

    [TestMethod]
    public void ToBytes_ProducesKnownVector()
    {
        var bytes = ExecuteDown().ToBytes();
        Assert.AreEqual(ExecuteDownHex, Convert.ToHexString(bytes));
        Assert.AreEqual((ushort)0, IoHomeCrypto.Crc16Kermit(bytes));
    }

    [TestMethod]
    public void TryParse_RoundTrips()
    {
        Assert.IsTrue(IoHomeFrame.TryParse(Convert.FromHexString(ExecuteDownHex), out var parsed));
        Assert.AreEqual(IoHomeCommand.Execute, parsed.Command);
        Assert.AreEqual(IoHomeFrame.BroadcastAddress, parsed.Destination);
        Assert.AreEqual(SyntheticAddress, parsed.Source);
        Assert.AreEqual(SyntheticSequence, parsed.Sequence);
        Assert.AreEqual("0161C8000000", Convert.ToHexString(parsed.Data));
        Assert.AreEqual(0, parsed.Trailer.Length);
        Assert.AreEqual("AAC28281E20A", Convert.ToHexString(parsed.Mac));
        Assert.AreEqual(ExecuteDownHex, Convert.ToHexString(parsed.ToBytes()));
    }

    [TestMethod]
    public void TryParse_IgnoresTrailingBytes() =>
        Assert.IsTrue(IoHomeFrame.TryParse(Convert.FromHexString(ExecuteDownHex + "FFFF00"), out _));

    [TestMethod]
    public void TryParse_RejectsBadCrc()
    {
        var bytes = Convert.FromHexString(ExecuteDownHex);
        bytes[^1] ^= 0x01;
        Assert.IsFalse(IoHomeFrame.TryParse(bytes, out var frame));
        Assert.IsNull(frame);
    }

    [TestMethod]
    public void TryParse_RejectsCorruptedPayload()
    {
        var bytes = Convert.FromHexString(ExecuteDownHex);
        bytes[CommandByteIndex + 1] ^= FlipTopBit; // corrupt the first parameter byte
        Assert.IsFalse(IoHomeFrame.TryParse(bytes, out _));
    }

    [TestMethod]
    public void TryParse_RejectsTruncatedFrame()
    {
        var bytes = Convert.FromHexString(ExecuteDownHex);
        Assert.IsFalse(IoHomeFrame.TryParse(bytes.AsSpan(0, bytes.Length - 1), out _));
        Assert.IsFalse(IoHomeFrame.TryParse(bytes.AsSpan(0, IoHomeFrame.MinLength - 1), out _));
    }

    [TestMethod]
    public void TryParse_RejectsTwoWayFrames()
    {
        var bytes = Convert.FromHexString(ExecuteDownHex);
        bytes[0] &= unchecked((byte)~SingleWayBit); // the CRC no longer matters
        Assert.IsFalse(IoHomeFrame.TryParse(bytes, out _));
    }

    [TestMethod]
    public void ToBytes_UnsignedFrame_Throws() =>
        Assert.ThrowsExactly<InvalidOperationException>(() => new IoHomeFrame { Command = IoHomeCommand.Execute }.ToBytes());

    [TestMethod]
    public void ToBytes_TooManyParameters_Throws() =>
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            new IoHomeFrame { Data = new byte[IoHomeFrame.MaxParameterLength + 1] }.Sign(SyntheticKey).ToBytes());

    [TestMethod]
    public void Sign_DependsOnSequenceAndKey()
    {
        var mac = ExecuteDown().Mac;
        CollectionAssert.AreNotEqual(mac, ExecuteDown(SyntheticSequence + 1).Mac);
        CollectionAssert.AreNotEqual(mac, ExecuteDown().Sign(new byte[IoHomeCrypto.KeySize]).Mac);
    }
}
