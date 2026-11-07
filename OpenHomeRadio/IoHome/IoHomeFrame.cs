using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;

namespace OpenHomeRadio.IoHome;

/// <summary>
/// One authenticated single-way (1W) IoHome frame. Layout after the sync word: control byte 0, control byte 1,
/// 3-byte destination, 3-byte source, command, 0–21 parameter bytes, 2-byte sequence (rolling code), 6-byte MAC and a
/// CRC-16/KERMIT (low byte first). Command and parameters travel in clear; the MAC authenticates them under the
/// installation's system key.
/// </summary>
/// <remarks>
/// A frame you build has no <see cref="Mac"/> until you call <see cref="Sign"/>; a parsed frame carries the received
/// MAC, which <see cref="IoHomeKeys.Verify"/> checks against a key.
/// </remarks>
public sealed class IoHomeFrame
{
    /// <summary>The broadcast destination: reaches every actuator paired to the source controller.</summary>
    public const uint BroadcastAddress = 0x00003F;

    /// <summary>The largest valid node address (24 bits).</summary>
    public const uint MaxAddress = 0xFFFFFF;

    /// <summary>The most parameter bytes (data plus trailer) a frame can carry.</summary>
    public const int MaxParameterLength = 21;

    /// <summary>The shortest valid frame: no parameters.</summary>
    internal const int MinLength = HeaderLength + SequenceSize + IoHomeCrypto.MacSize + CrcSize;

    private const byte StartEndFlags = 0xC0;   // control byte 0, bits 7-6: first and last frame of the exchange
    private const byte Protocol1WBit = 0x20;   // control byte 0, bit 5: single-way frame
    private const int LengthMask = 0x1F;       // control byte 0, bits 4-0: length
    private const int NodeIdSize = 3;
    private const int SequenceSize = 2;
    private const int CrcSize = 2;
    private const int BitsPerByte = 8;
    private const int DestinationIndex = 2;
    private const int SourceIndex = DestinationIndex + NodeIdSize;
    private const int CommandIndex = SourceIndex + NodeIdSize;
    private const int HeaderLength = CommandIndex + 1;

    // The length field counts every byte except control byte 0 and the CRC, so it includes the MAC. Key-transfer
    // frames exclude the MAC instead; parsing accepts both rules.
    private const int LengthOverheadWithMac = HeaderLength - 1 + SequenceSize + IoHomeCrypto.MacSize;
    private const int LengthOverheadWithoutMac = LengthOverheadWithMac - IoHomeCrypto.MacSize;

    /// <summary>
    /// Overrides the whole of control byte 0. When null (the default), it is computed as first+last frame, single-way,
    /// and the length. Parsed frames keep the received value so that re-serialising reproduces them exactly.
    /// </summary>
    public byte? ControlByte0 { get; init; }

    /// <summary>
    /// Control byte 1 (beacon, routed, low-power, acknowledge and version flags). 0 suits a plain single-way command;
    /// some battery-powered actuators may need the low-power flag 0x20 for directed (non-broadcast) commands.
    /// </summary>
    public byte ControlByte1 { get; init; }

    /// <summary>24-bit destination node; usually <see cref="BroadcastAddress"/>.</summary>
    public uint Destination { get; init; }

    /// <summary>24-bit source node: the sending controller's address, to which an actuator binds the stored key.</summary>
    public uint Source { get; init; }

    /// <summary>The command.</summary>
    public IoHomeCommand Command { get; init; }

    /// <summary>The command's parameters, which the MAC authenticates. For <see cref="IoHomeCommand.Execute"/>: originator, ACEI, main parameter (2 bytes), two functional parameters.</summary>
    public byte[] Data { get; init; } = [];

    /// <summary>
    /// Extra parameter bytes after <see cref="Data"/> that the MAC does not cover: the manufacturer and tag bytes of a
    /// <see cref="IoHomeCommand.KeyTransfer1W"/>. Empty for other commands.
    /// </summary>
    public byte[] Trailer { get; init; } = [];

    /// <summary>
    /// The rolling code. It feeds the MAC, and an actuator rejects any value not above the last one it accepted from
    /// this source.
    /// </summary>
    public ushort Sequence { get; init; }

    /// <summary>The 6-byte MAC: empty for an unsigned frame, set by <see cref="Sign"/> or by parsing.</summary>
    public byte[] Mac { get; init; } = [];

    /// <summary>Returns a copy of this frame with <see cref="Mac"/> computed under <paramref name="systemKey"/>.</summary>
    /// <param name="systemKey">The 16-byte system key.</param>
    public IoHomeFrame Sign(ReadOnlySpan<byte> systemKey) => new()
    {
        ControlByte0 = ControlByte0,
        ControlByte1 = ControlByte1,
        Destination = Destination,
        Source = Source,
        Command = Command,
        Data = Data,
        Trailer = Trailer,
        Sequence = Sequence,
        Mac = IoHomeCrypto.Create1WMac([(byte)Command, .. Data], [(byte)(Sequence >> BitsPerByte), (byte)Sequence], systemKey),
    };

    /// <summary>Serialises a signed frame to its on-air bytes (from control byte 0 to the CRC).</summary>
    /// <exception cref="InvalidOperationException">The frame is not signed, or has too many parameter bytes or an address over 24 bits.</exception>
    public byte[] ToBytes()
    {
        var parameterLength = Data.Length + Trailer.Length;
        if (Mac.Length != IoHomeCrypto.MacSize)
            throw new InvalidOperationException($"The frame has no MAC; call {nameof(Sign)} first.");
        if (parameterLength > MaxParameterLength)
            throw new InvalidOperationException($"A frame carries at most {MaxParameterLength} parameter bytes.");
        if (Destination > MaxAddress || Source > MaxAddress)
            throw new InvalidOperationException("Node addresses are 24 bits.");

        var frame = new byte[MinLength + parameterLength];
        // Key transfers count the length without the MAC (as in the published reference frame); otherwise their 18
        // parameter bytes would overflow the 5-bit field.
        frame[0] = ControlByte0 ?? (byte)(StartEndFlags | Protocol1WBit
            | ((parameterLength + (Command == IoHomeCommand.KeyTransfer1W ? LengthOverheadWithoutMac : LengthOverheadWithMac)) & LengthMask));
        frame[1] = ControlByte1;
        WriteNodeId(frame.AsSpan(DestinationIndex), Destination);
        WriteNodeId(frame.AsSpan(SourceIndex), Source);
        frame[CommandIndex] = (byte)Command;
        Data.CopyTo(frame, HeaderLength);
        Trailer.CopyTo(frame, HeaderLength + Data.Length);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(HeaderLength + parameterLength), Sequence);
        Mac.CopyTo(frame, HeaderLength + parameterLength + SequenceSize);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(frame.Length - CrcSize), IoHomeCrypto.Crc16Kermit(frame.AsSpan(0, frame.Length - CrcSize)));
        return frame;
    }

    /// <summary>
    /// Parses a single-way frame from the start of <paramref name="bytes"/> (trailing bytes are ignored), verifying the
    /// CRC. The MAC is not checked, since that needs the system key; see <see cref="IoHomeKeys.Verify"/>.
    /// </summary>
    /// <param name="bytes">The received bytes, starting at control byte 0.</param>
    /// <param name="frame">The parsed frame, or null on failure.</param>
    public static bool TryParse(ReadOnlySpan<byte> bytes, [NotNullWhen(true)] out IoHomeFrame? frame)
    {
        frame = null;
        if (bytes.Length < MinLength || (bytes[0] & Protocol1WBit) == 0)
            return false;

        foreach (var overhead in (ReadOnlySpan<int>)[LengthOverheadWithMac, LengthOverheadWithoutMac])
        {
            var parameterLength = (bytes[0] & LengthMask) - overhead;
            var length = MinLength + parameterLength;
            if (parameterLength < 0 || parameterLength > MaxParameterLength || bytes.Length < length
                || IoHomeCrypto.Crc16Kermit(bytes[..(length - CrcSize)]) != BinaryPrimitives.ReadUInt16LittleEndian(bytes[(length - CrcSize)..]))
                continue;

            var command = (IoHomeCommand)bytes[CommandIndex];
            var parameters = bytes.Slice(HeaderLength, parameterLength);
            var dataLength = command == IoHomeCommand.KeyTransfer1W ? Math.Min(parameterLength, IoHomeCrypto.KeySize) : parameterLength;
            frame = new IoHomeFrame
            {
                ControlByte0 = bytes[0],
                ControlByte1 = bytes[1],
                Destination = ReadNodeId(bytes[DestinationIndex..]),
                Source = ReadNodeId(bytes[SourceIndex..]),
                Command = command,
                Data = parameters[..dataLength].ToArray(),
                Trailer = parameters[dataLength..].ToArray(),
                Sequence = BinaryPrimitives.ReadUInt16BigEndian(bytes[(HeaderLength + parameterLength)..]),
                Mac = bytes.Slice(HeaderLength + parameterLength + SequenceSize, IoHomeCrypto.MacSize).ToArray(),
            };
            return true;
        }
        return false;
    }

    /// <summary>A compact, human-readable description of the frame.</summary>
    public override string ToString() =>
        $"{Command} dest 0x{Destination:X6} src 0x{Source:X6} seq 0x{Sequence:X4} data {Convert.ToHexString(Data)}" +
        (Trailer.Length > 0 ? $" trailer {Convert.ToHexString(Trailer)}" : "") + $" mac {Convert.ToHexString(Mac)}";

    private static void WriteNodeId(Span<byte> destination, uint nodeId)
    {
        destination[0] = (byte)(nodeId >> (2 * BitsPerByte));
        destination[1] = (byte)(nodeId >> BitsPerByte);
        destination[2] = (byte)nodeId;
    }

    private static uint ReadNodeId(ReadOnlySpan<byte> source) =>
        (uint)((source[0] << (2 * BitsPerByte)) | (source[1] << BitsPerByte) | source[2]);
}
