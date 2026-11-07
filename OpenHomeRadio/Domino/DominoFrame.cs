using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;

namespace OpenHomeRadio.Domino;

/// <summary>
/// One Domino radio frame: 12 payload bytes followed by a CRC-16/CCITT-FALSE, sent most significant bit first.
/// There is no encryption or authentication; actuators only check the CRC and that <see cref="Counter"/> changed.
/// </summary>
/// <remarks>
/// Layout: bytes 0–3 system identifier, 4 command, 5–7 button address (little-endian), 8 counter, 9 group, 10 flag,
/// 11 value, 12–13 CRC (big-endian) over bytes 0–11.
/// </remarks>
public sealed record DominoFrame
{
    /// <summary>On-air frame length in bytes, including the CRC.</summary>
    public const int Length = PayloadLength + CrcLength;

    /// <summary>The largest valid <see cref="Address"/> (24 bits).</summary>
    public const uint MaxAddress = 0xFFFFFF;

    private const int PayloadLength = 12;
    private const int CrcLength = 2;
    private const int CommandIndex = 4;
    private const int AddressIndex = 5;
    private const int CounterIndex = 8;
    private const int GroupIndex = 9;
    private const int FlagIndex = 10;
    private const int ValueIndex = 11;
    private const int BitsPerByte = 8;
    private const ushort CrcPolynomial = 0x1021;
    private const ushort CrcInitial = 0xFFFF;
    private const ushort CrcTopBit = 0x8000;

    /// <summary>System identifier, bytes 0–3 (most significant byte first). See <see cref="DominoSettings.DefaultSystemId"/>.</summary>
    public uint SystemId { get; init; } = DominoSettings.DefaultSystemId;

    /// <summary>Command code (byte 4).</summary>
    public DominoCommand Command { get; init; }

    /// <summary>24-bit button address (bytes 5–7, little-endian on air).</summary>
    public uint Address
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, MaxAddress);
            field = value;
        }
    }

    /// <summary>
    /// De-duplication counter (byte 8). A receiver acts only when it differs from the value it last saw for the address,
    /// in either direction; the repeated frames of one press share it, successive presses change it.
    /// </summary>
    public byte Counter { get; init; }

    /// <summary>Byte 9, constant per remote (for example 0x20 or 0x23). Exact meaning unknown.</summary>
    public byte Group { get; init; }

    /// <summary>
    /// Byte 10. A press is sent as a few frames with one value followed by more with it decremented by one; the value
    /// depends on the command (0x05 for <see cref="DominoCommand.Up"/>/<see cref="DominoCommand.Down"/>, 0x18 otherwise).
    /// </summary>
    public byte Flag { get; init; }

    /// <summary>Byte 11, the command's argument (for example a lock level); zero for simple commands.</summary>
    public byte Value { get; init; }

    /// <summary>Serialises the frame to its 14 on-air bytes, CRC included.</summary>
    public byte[] ToBytes()
    {
        var frame = new byte[Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame, SystemId);
        frame[CommandIndex] = (byte)Command;
        frame[AddressIndex] = (byte)Address;
        frame[AddressIndex + 1] = (byte)(Address >> BitsPerByte);
        frame[AddressIndex + 2] = (byte)(Address >> (2 * BitsPerByte));
        frame[CounterIndex] = Counter;
        frame[GroupIndex] = Group;
        frame[FlagIndex] = Flag;
        frame[ValueIndex] = Value;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(PayloadLength), ComputeCrc(frame.AsSpan(0, PayloadLength)));
        return frame;
    }

    /// <summary>Parses 14 on-air bytes. Fails on a wrong length or a CRC mismatch; any system identifier is accepted.</summary>
    /// <param name="bytes">The frame bytes.</param>
    /// <param name="frame">The parsed frame, or null on failure.</param>
    public static bool TryParse(ReadOnlySpan<byte> bytes, [NotNullWhen(true)] out DominoFrame? frame)
    {
        frame = null;
        if (bytes.Length != Length || ComputeCrc(bytes[..PayloadLength]) != BinaryPrimitives.ReadUInt16BigEndian(bytes[PayloadLength..]))
            return false;

        frame = new DominoFrame
        {
            SystemId = BinaryPrimitives.ReadUInt32BigEndian(bytes),
            Command = (DominoCommand)bytes[CommandIndex],
            Address = (uint)(bytes[AddressIndex] | (bytes[AddressIndex + 1] << BitsPerByte) | (bytes[AddressIndex + 2] << (2 * BitsPerByte))),
            Counter = bytes[CounterIndex],
            Group = bytes[GroupIndex],
            Flag = bytes[FlagIndex],
            Value = bytes[ValueIndex],
        };
        return true;
    }

    /// <summary>CRC-16/CCITT-FALSE (polynomial 0x1021, initial value 0xFFFF, no reflection, no final XOR).</summary>
    /// <param name="data">The bytes to checksum.</param>
    public static ushort ComputeCrc(ReadOnlySpan<byte> data)
    {
        var crc = CrcInitial;
        foreach (var b in data)
        {
            crc ^= (ushort)(b << BitsPerByte);
            for (var bit = 0; bit < BitsPerByte; bit++)
                crc = (ushort)((crc & CrcTopBit) != 0 ? (crc << 1) ^ CrcPolynomial : crc << 1);
        }
        return crc;
    }

    /// <summary>A compact, human-readable description of the frame.</summary>
    public override string ToString() =>
        $"{Command} address 0x{Address:X6} group 0x{Group:X2} counter 0x{Counter:X2} flag 0x{Flag:X2} value 0x{Value:X2} system 0x{SystemId:X8}";
}
