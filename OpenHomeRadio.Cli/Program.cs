using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Parsing;
using System.Globalization;
using System.Net.Sockets;
using OpenHomeRadio.Domino;
using OpenHomeRadio.IoHome;
using OpenHomeRadio.Sdr;

const int ExitSuccess = 0;
const int ExitFailure = 1;
const string ToolCommandName = "openhomeradio";
const string HexPrefix = "0x";
const string TimeFormat = "HH:mm:ss";
const char RangeSeparator = ':';
const double RangeTolerance = 1e-9;          // so that min:max:step includes max despite floating-point rounding
const double MaxPositionPercent = 100;
const int FileChunkSamples = 1 << 20;        // files are demodulated in pieces of about 0.4 s, so long captures stay manageable
const int FileChunkOverlapSamples = 1 << 17; // longer than any burst: a burst cut at the end of one piece is whole in the next
const int SignalFactor = 3;                  // capture: a buffer is "signal" if its peak exceeds the quiet baseline this many times...
const int MinSignalLevel = 150;              // ...and this absolute level, so that quiet noise is not flagged
const int WideDeviationFactor = 2;           // some IoHome remotes transmit with about twice the nominal deviation
const uint ExampleIoHomeAddress = 0xABCDEF;  // the node address of the published IoHome test vectors
const string OwnerWarning = "Only use this on installations you own or are authorised to control.";

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
var plutoDefaults = new PlutoSdrOptions();
var dominoDefaults = new DominoSettings();
var ioHomeDefaults = new IoHomeSettings();
var defaultSweep = IoHomeSettings.DefaultTxFrequencyOffsetsHz;

Func<ArgumentResult, uint> Hex(uint max) => result =>
{
    var text = result.Tokens.Single().Value;
    if (uint.TryParse(text.StartsWith(HexPrefix, StringComparison.OrdinalIgnoreCase) ? text[HexPrefix.Length..] : text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value) && value <= max)
        return value;
    result.AddError($"'{text}' is not a hexadecimal number between 0 and {max:X}.");
    return 0;
};

Func<ArgumentResult, byte[]?> HexBytes(int? length) => result =>
{
    var text = result.Tokens.Single().Value;
    try
    {
        var bytes = Convert.FromHexString(text.StartsWith(HexPrefix, StringComparison.OrdinalIgnoreCase) ? text[HexPrefix.Length..] : text);
        if (length is null || bytes.Length == length)
            return bytes;
    }
    catch (FormatException)
    {
    }
    result.AddError(length is null ? $"'{text}' is not a hexadecimal byte string." : $"'{text}' is not {length} hexadecimal bytes.");
    return null;
};

// Options and arguments shared by several commands.
var hostOption = new Option<string>("--host") { Description = "Network address of the ADALM-PLUTO.", DefaultValueFactory = _ => PlutoSdrOptions.DefaultHost };
var portOption = new Option<int>("--port") { Description = "TCP port of the radio's IIO daemon.", DefaultValueFactory = _ => PlutoSdrOptions.DefaultPort };
var fileArgument = new Argument<FileInfo>("file.cs16") { Description = "Capture file: raw interleaved little-endian int16 I/Q, as written by 'capture'." }.AcceptExistingOnly();
var centerOption = new Option<long>("--center") { Description = "The tuning the file was recorded at, in Hz.", DefaultValueFactory = _ => plutoDefaults.ReceiveCenterFrequencyHz };
var rateOption = new Option<long>("--rate") { Description = "The file's sample rate, in Hz.", DefaultValueFactory = _ => plutoDefaults.SampleRateHz };
var keyArgument = new Argument<byte[]>("key") { Description = "16-byte system key, hex (e.g. 000102030405060708090A0B0C0D0E0F).", Arity = ArgumentArity.ExactlyOne, CustomParser = HexBytes(IoHomeCrypto.KeySize) };
var ioHomeAddressArgument = new Argument<uint>("address") { Description = "24-bit address of the remote to transmit as, hex (e.g. ABCDEF).", CustomParser = Hex(IoHomeFrame.MaxAddress) };
var sequenceOption = new Option<uint>("--sequence") { Description = "Sequence number (rolling code) to send, hex. Must be above the last one the actuator accepted from this address. Default 0.", CustomParser = Hex(ushort.MaxValue) };
var offsetsOption = new Option<IReadOnlyList<double>>("--offsets")
{
    Description = $"Transmit frequency corrections in Hz: min:max:step for a sweep, or one value for no sweep. Default {defaultSweep[0]}:{defaultSweep[^1]}:{defaultSweep[1] - defaultSweep[0]}.",
    CustomParser = result =>
    {
        var text = result.Tokens.Single().Value;
        var values = text.Split(RangeSeparator).Select(part => double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : double.NaN).ToArray();
        if (values.Length == 1 && !double.IsNaN(values[0]))
            return values;
        if (values.Length == 3 && !values.Any(double.IsNaN) && values[2] > 0 && values[1] >= values[0])
            return [.. Enumerable.Range(0, (int)((values[1] - values[0]) / values[2] + RangeTolerance) + 1).Select(step => values[0] + step * values[2])];
        result.AddError($"'{text}' is neither min:max:step (step > 0, max >= min) nor a single frequency offset.");
        return null;
    },
};
var destinationOption = new Option<uint>("--destination") { Description = "Destination node, hex. Default: broadcast to every actuator paired with this remote.", CustomParser = Hex(IoHomeFrame.MaxAddress) };
var ioHomeAttenuationOption = new Option<double>("--attenuation") { Description = "Transmit power back-off in dB (0 = maximum power).", DefaultValueFactory = _ => ioHomeDefaults.TxAttenuationDb };

PlutoSdr Connect(ParseResult p) => new(new PlutoSdrOptions { Host = p.GetValue(hostOption)!, Port = p.GetValue(portOption) });

async Task<IEnumerable<IqBuffer>> LoadChunksAsync(ParseResult p, int overlap, CancellationToken ct)
{
    var whole = await IqBuffer.ReadCs16Async(p.GetValue(fileArgument)!.FullName, p.GetValue(centerOption), p.GetValue(rateOption), ct);
    return Enumerable.Range(0, Math.Max(1, (int)Math.Ceiling((double)(whole.SampleCount - overlap) / (FileChunkSamples - overlap))))
        .Select(chunk => whole.Slice(chunk * (FileChunkSamples - overlap), Math.Min(FileChunkSamples, whole.SampleCount - chunk * (FileChunkSamples - overlap))));
}

async Task<int> DecodeAsync(ParseResult p, bool domino, bool ioHome, CancellationToken ct)
{
    HashSet<string> seen = [];
    foreach (var chunk in await LoadChunksAsync(p, FileChunkOverlapSamples, ct))
    {
        foreach (var frame in domino ? DominoModem.Demodulate(chunk, dominoDefaults) : [])
            if (seen.Add(frame.ToString()))
                Console.WriteLine($"domino  {frame}");
        foreach (var frame in ioHome ? IoHomeModem.Demodulate(chunk, ioHomeDefaults) : [])
            if (seen.Add(Convert.ToHexString(frame.ToBytes())))
                Console.WriteLine($"iohome  {frame}  raw {Convert.ToHexString(frame.ToBytes())}");
    }
    Console.Error.WriteLine($"{seen.Count} distinct frame(s) decoded.");
    return ExitSuccess;
}

async Task ReceiveDominoAsync(ISdrTransceiver sdr, CancellationToken ct)
{
    await foreach (var frame in new DominoClient(sdr).ReceiveAsync(ct))
        Console.WriteLine($"{DateTime.Now.ToString(TimeFormat)}  domino  {frame}");
}

async Task ReceiveIoHomeAsync(ISdrTransceiver sdr, CancellationToken ct)
{
    await foreach (var frame in new IoHomeClient(sdr).ReceiveAsync(ct))
        Console.WriteLine($"{DateTime.Now.ToString(TimeFormat)}  iohome  {frame}  raw {Convert.ToHexString(frame.ToBytes())}");
}

async Task<int> ReceiveAsync(ParseResult p, string protocols, Func<PlutoSdr, Task> receive)
{
    await using var sdr = Connect(p);
    Console.Error.WriteLine($"Listening for {protocols} at {sdr.Options.ReceiveCenterFrequencyHz} Hz via {sdr.Options.Host}:{sdr.Options.Port} (Ctrl+C to stop)...");
    await receive(sdr);
    return ExitSuccess;
}

async Task<int> TransmitIoHomeAsync(ParseResult p, Func<IoHomeClient, IoHomeRemote, Task<IoHomeFrame>> transmit)
{
    if (p.GetResult(sequenceOption) is null)
        Console.Error.WriteLine("warning: no --sequence given, using 0000. An actuator ignores sequence numbers that are not above the last one it accepted from this address; pass the 'next' value printed by the previous command.");
    await using var sdr = Connect(p);
    var remote = new IoHomeRemote(p.GetValue(keyArgument), p.GetValue(ioHomeAddressArgument))
    {
        Sequence = (ushort)p.GetValue(sequenceOption),
        Destination = p.GetResult(destinationOption) is null ? IoHomeFrame.BroadcastAddress : p.GetValue(destinationOption),
    };
    var client = new IoHomeClient(sdr, new IoHomeSettings
    {
        TxFrequencyOffsetsHz = p.GetValue(offsetsOption) ?? defaultSweep,
        TxAttenuationDb = p.GetValue(ioHomeAttenuationOption),
    });
    Console.Error.WriteLine($"Transmitting at {client.Settings.TxFrequencyOffsetsHz.Count} frequency offset(s) via {sdr.Options.Host}:{sdr.Options.Port}...");
    var frame = await transmit(client, remote);
    Console.WriteLine($"Sent {frame}");
    Console.WriteLine($"Used sequence {frame.Sequence:X4}; next sequence {remote.Sequence:X4} (pass --sequence {remote.Sequence:X4} next time).");
    return ExitSuccess;
}

// selftest
var selftestCommand = new Command("selftest", "Check the protocol implementations without hardware: the published IoHome test vectors, and modulate/demodulate round trips of both protocols.");
selftestCommand.SetAction(_ =>
{
    var passed = 0;
    var failed = 0;
    void Check(string name, bool ok)
    {
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}");
        if (ok)
            passed++;
        else
            failed++;
    }

    Console.WriteLine("IoHome published test vectors:");
    var key = Convert.FromHexString("01020304050607080910111213141516");
    var crcSample = Convert.FromHexString("F80000003F1A380B000161000080D8050002A624222E8BA351");
    Check("CRC-16/KERMIT of the sample frame is 525F", IoHomeCrypto.Crc16Kermit(crcSample).ToString("X4") == "525F");
    Check("CRC over the frame including its CRC is 0000", IoHomeCrypto.Crc16Kermit([.. crcSample, .. Convert.FromHexString("5F52")]) == 0);
    Check("MAC initial value is 000143D2000000550500059955555555",
        Convert.ToHexString(IoHomeCrypto.ConstructIv1W(Convert.FromHexString("000143D2000000"), Convert.FromHexString("0599"))) == "000143D2000000550500059955555555");
    var encryptedKey = IoHomeCrypto.Encrypt1WKey(key, ExampleIoHomeAddress);
    Check("1W key transfer encryption is 7E60491F976ADF653DB0ED785E49A201", Convert.ToHexString(encryptedKey) == "7E60491F976ADF653DB0ED785E49A201");
    Check("1W MAC is 19E81EC43D5E",
        Convert.ToHexString(IoHomeCrypto.Create1WMac([(byte)IoHomeCommand.KeyTransfer1W, .. encryptedKey], Convert.FromHexString("1234"), key)) == "19E81EC43D5E");
    const string PairingFrame = "FC0000003FABCDEF307E60491F976ADF653DB0ED785E49A2010201123419E81EC43D5E9BF2";
    Check("key transfer frame is reproduced byte for byte", Convert.ToHexString(new IoHomeFrame
    {
        ControlByte0 = Convert.FromHexString(PairingFrame)[0], // this sample uses the length rule that excludes the MAC
        Command = IoHomeCommand.KeyTransfer1W,
        Destination = IoHomeFrame.BroadcastAddress,
        Source = ExampleIoHomeAddress,
        Sequence = Convert.ToUInt16("1234", 16),
        Data = encryptedKey,
        Trailer = Convert.FromHexString("0201"),
    }.Sign(key).ToBytes()) == PairingFrame);
    Check("key is recovered from the key transfer frame and verifies against its MAC",
        IoHomeFrame.TryParse(Convert.FromHexString(PairingFrame), out var pairing)
        && IoHomeKeys.RecoverFromKeyTransfer(pairing).AsSpan().SequenceEqual(key)
        && IoHomeKeys.Verify(key, pairing)
        && !IoHomeKeys.Verify(new byte[IoHomeCrypto.KeySize], pairing));

    Console.WriteLine("Round trips (synthetic values):");
    var sampleRate = plutoDefaults.SampleRateHz;
    var dominoFrame = new DominoFrame { Command = DominoCommand.Toggle, Address = 0x123456, Group = 0x20, Counter = 0x42, Flag = 0x18 };
    Check("Domino frame serialise/parse", DominoFrame.TryParse(dominoFrame.ToBytes(), out var parsedDomino) && parsedDomino == dominoFrame);
    var dominoSignal = DominoModem.Modulate([dominoFrame], dominoDefaults, sampleRate);
    Check("Domino modulate/demodulate", DominoModem.Demodulate(dominoSignal, dominoDefaults).Contains(dominoFrame));
    var ioHomeFrame = new IoHomeFrame
    {
        Command = IoHomeCommand.Execute,
        Destination = IoHomeFrame.BroadcastAddress,
        Source = ExampleIoHomeAddress,
        Sequence = 0x0042,
        Data = Convert.FromHexString("0161C8000000"), // originator user, priority 0x61, main parameter C800 (down), no functional parameters
    }.Sign(key);
    var ioHomeBytes = Convert.ToHexString(ioHomeFrame.ToBytes());
    Check("IoHome frame serialise/parse/verify",
        IoHomeFrame.TryParse(ioHomeFrame.ToBytes(), out var parsedIoHome) && Convert.ToHexString(parsedIoHome.ToBytes()) == ioHomeBytes && IoHomeKeys.Verify(key, parsedIoHome));
    var ioHomeSignal = IoHomeModem.Modulate([ioHomeFrame], ioHomeDefaults, sampleRate);
    Check("IoHome modulate/demodulate", IoHomeModem.Demodulate(ioHomeSignal, ioHomeDefaults).Any(f => Convert.ToHexString(f.ToBytes()) == ioHomeBytes));
    Check("IoHome with twice the nominal deviation",
        IoHomeModem.Demodulate(IoHomeModem.Modulate([ioHomeFrame], new IoHomeSettings { DeviationHz = WideDeviationFactor * ioHomeDefaults.DeviationHz }, sampleRate), ioHomeDefaults)
            .Any(f => Convert.ToHexString(f.ToBytes()) == ioHomeBytes));
    // One buffer with both bursts, tuned to the Domino carrier; the IoHome burst sits at its own carrier's offset.
    var shared = new IqBuffer(
        (short[])[.. dominoSignal.Samples.Span, .. IoHomeModem.Modulate([ioHomeFrame], ioHomeDefaults, sampleRate, ioHomeDefaults.CarrierHz - dominoDefaults.CarrierHz).Samples.Span],
        dominoDefaults.CarrierHz,
        sampleRate);
    Check("both protocols decoded from one shared buffer",
        DominoModem.Demodulate(shared, dominoDefaults).Contains(dominoFrame) && IoHomeModem.Demodulate(shared, ioHomeDefaults).Any(f => Convert.ToHexString(f.ToBytes()) == ioHomeBytes));

    Console.WriteLine($"{passed} passed, {failed} failed.");
    return failed == 0 ? ExitSuccess : ExitFailure;
});

// receive / capture / decode (all protocols)
var receiveCommand = new Command("receive", "Print every decoded frame of all protocols from one shared receive stream.") { hostOption, portOption };
receiveCommand.SetAction((p, ct) => ReceiveAsync(p, "all protocols", sdr => Task.WhenAll(ReceiveDominoAsync(sdr, ct), ReceiveIoHomeAsync(sdr, ct))));

var secondsArgument = new Argument<double>("seconds") { Description = "How long to record." };
var outputArgument = new Argument<FileInfo>("file.cs16") { Description = "Output file (raw interleaved little-endian int16 I/Q)." };
var captureCommand = new Command("capture", "Record raw I/Q at the shared receive tuning, flagging and decoding buffers that contain a signal. Example: openhomeradio capture 10 remote.cs16")
{
    secondsArgument, outputArgument, hostOption, portOption,
};
captureCommand.SetAction(async (p, ct) =>
{
    await using var sdr = Connect(p);
    await using var file = p.GetValue(outputArgument)!.Create();
    var duration = TimeSpan.FromSeconds(p.GetValue(secondsArgument));
    var recorded = TimeSpan.Zero;
    var baseline = double.PositiveInfinity; // the lowest peak of a quiet buffer so far
    Console.Error.WriteLine($"Recording {duration.TotalSeconds} s at {sdr.Options.ReceiveCenterFrequencyHz} Hz, {sdr.SampleRateHz} samples/s, via {sdr.Options.Host}:{sdr.Options.Port}...");
    await foreach (var buffer in sdr.ReceiveAsync(ct))
    {
        await buffer.WriteCs16Async(file, ct);
        var peak = 0;
        foreach (var value in buffer.Samples.Span)
            peak = Math.Max(peak, Math.Abs((int)value));
        var signal = peak > MinSignalLevel && (double.IsPositiveInfinity(baseline) || peak > baseline * SignalFactor);
        if (!signal)
            baseline = Math.Min(baseline, peak);
        Console.WriteLine($"{recorded.TotalSeconds,7:F2} s  peak {peak,5}{(signal ? "  <-- signal" : "")}");
        if (signal)
        {
            foreach (var frame in DominoModem.Demodulate(buffer, dominoDefaults))
                Console.WriteLine($"           domino  {frame}");
            foreach (var frame in IoHomeModem.Demodulate(buffer, ioHomeDefaults))
                Console.WriteLine($"           iohome  {frame}  raw {Convert.ToHexString(frame.ToBytes())}");
        }
        recorded += buffer.Duration;
        if (recorded >= duration)
            break;
    }
    Console.Error.WriteLine($"Wrote {file.Length} bytes to {file.Name}.");
    return ExitSuccess;
});

var decodeCommand = new Command("decode", "Decode all protocols from a capture file.") { fileArgument, centerOption, rateOption };
decodeCommand.SetAction((p, ct) => DecodeAsync(p, domino: true, ioHome: true, ct));

// domino
var dominoAddressArgument = new Argument<uint>("address") { Description = "24-bit button address, hex (e.g. 123456).", CustomParser = Hex(DominoFrame.MaxAddress) };
var dominoGroupArgument = new Argument<uint>("group") { Description = "Group byte of the remote, hex (e.g. 20).", CustomParser = Hex(byte.MaxValue) };
var dominoCommandArgument = new Argument<DominoCommand>("command") { HelpName = "command", Description = $"One of {string.Join(", ", Enum.GetNames<DominoCommand>())}." };
var valueOption = new Option<byte>("--value") { Description = "The command's argument, decimal (for example a lock level)." };
var counterOption = new Option<uint>("--counter") { Description = "Counter byte to send, hex. An actuator ignores a counter equal to the last one it saw. Default random.", CustomParser = Hex(byte.MaxValue) };
var dominoAttenuationOption = new Option<double>("--attenuation") { Description = "Transmit power back-off in dB (0 = maximum power).", DefaultValueFactory = _ => dominoDefaults.TxAttenuationDb };
var dominoSendCommand = new Command("send", "Send a command as a remote button. Example: openhomeradio domino send 123456 20 ContinuousDown")
{
    dominoAddressArgument, dominoGroupArgument, dominoCommandArgument, valueOption, counterOption, dominoAttenuationOption, hostOption, portOption,
};
dominoSendCommand.SetAction(async (p, ct) =>
{
    await using var sdr = Connect(p);
    var client = new DominoClient(sdr, new DominoSettings { TxAttenuationDb = p.GetValue(dominoAttenuationOption) })
    {
        Counter = p.GetResult(counterOption) is null ? (byte)Random.Shared.Next(byte.MaxValue + 1) : (byte)p.GetValue(counterOption),
    };
    var device = new DominoDevice(p.GetValue(dominoAddressArgument), (byte)p.GetValue(dominoGroupArgument));
    var counter = client.Counter;
    await client.SendAsync(device, p.GetValue(dominoCommandArgument), p.GetValue(valueOption), ct);
    Console.WriteLine($"Sent {p.GetValue(dominoCommandArgument)} to address {device.Address:X6} group {device.Group:X2}.");
    Console.WriteLine($"Used counter {counter:X2}; next counter {client.Counter:X2} (pass --counter {client.Counter:X2} next time).");
    return ExitSuccess;
});

var dominoReceiveCommand = new Command("receive", "Print received Domino button presses, one line per press.") { hostOption, portOption };
dominoReceiveCommand.SetAction((p, ct) => ReceiveAsync(p, "Domino", sdr => ReceiveDominoAsync(sdr, ct)));

var dominoDecodeCommand = new Command("decode", "Decode Domino frames from a capture file.") { fileArgument, centerOption, rateOption };
dominoDecodeCommand.SetAction((p, ct) => DecodeAsync(p, domino: true, ioHome: false, ct));

// iohome
var actionArgument = new Argument<string>("action") { Description = "up, down, stop, or a position 0..100 % (positions follow the protocol documentation; not verified on hardware)." };
actionArgument.Validators.Add(result =>
{
    var text = result.Tokens.Single().Value;
    if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent) ? percent is < 0 or > MaxPositionPercent : !Enum.GetNames<IoHomeAction>().Contains(text, StringComparer.OrdinalIgnoreCase))
        result.AddError($"'{text}' is not up, down, stop or a position from 0 to {MaxPositionPercent}.");
});
var ioHomeSendCommand = new Command("send", "Move the actuators paired with a remote. Example: openhomeradio iohome send 000102030405060708090A0B0C0D0E0F ABCDEF down --sequence 1")
{
    keyArgument, ioHomeAddressArgument, actionArgument, sequenceOption, offsetsOption, ioHomeAttenuationOption, destinationOption, hostOption, portOption,
};
ioHomeSendCommand.SetAction((p, ct) => TransmitIoHomeAsync(p, (client, remote) =>
    double.TryParse(p.GetValue(actionArgument), NumberStyles.Float, CultureInfo.InvariantCulture, out var percent)
        ? client.SendPositionAsync(remote, percent, ct)
        : client.SendAsync(remote, Enum.Parse<IoHomeAction>(p.GetValue(actionArgument)!, ignoreCase: true), ct)));

var ioHomePairCommand = new Command("pair", "Pair a remote with every actuator in learn mode (1W key transfer). Put the actuator into learn mode first. Example: openhomeradio iohome pair 000102030405060708090A0B0C0D0E0F ABCDEF")
{
    keyArgument, ioHomeAddressArgument, sequenceOption, offsetsOption, ioHomeAttenuationOption, hostOption, portOption,
};
ioHomePairCommand.SetAction((p, ct) => TransmitIoHomeAsync(p, (client, remote) => client.PairAsync(remote, ct)));

var ioHomeReceiveCommand = new Command("receive", "Print received IoHome frames (CRC-checked, MAC not verified) with their raw bytes.") { hostOption, portOption };
ioHomeReceiveCommand.SetAction((p, ct) => ReceiveAsync(p, "IoHome", sdr => ReceiveIoHomeAsync(sdr, ct)));

var ioHomeDecodeCommand = new Command("decode", "Decode IoHome frames from a capture file.") { fileArgument, centerOption, rateOption };
ioHomeDecodeCommand.SetAction((p, ct) => DecodeAsync(p, domino: false, ioHome: true, ct));

var ioHomeDiagnoseCommand = new Command("diagnose", "Report bursts, tones, frequency offsets and sync words in a capture file, to tell 'no signal' from 'signal but not decodable'.")
{
    fileArgument, centerOption, rateOption,
};
ioHomeDiagnoseCommand.SetAction(async (p, ct) =>
{
    foreach (var (chunk, index) in (await LoadChunksAsync(p, 0, ct)).Select((chunk, index) => (chunk, index)))
    {
        Console.WriteLine($"from sample {index * FileChunkSamples} ({(double)index * FileChunkSamples / p.GetValue(rateOption):F2} s):");
        foreach (var line in IoHomeModem.Diagnose(chunk, ioHomeDefaults))
            Console.WriteLine($"  {line}");
    }
    return ExitSuccess;
});

var genkeyCommand = new Command("genkey", "Print a new random 16-byte system key for a new remote.");
genkeyCommand.SetAction(_ =>
{
    Console.WriteLine(Convert.ToHexString(IoHomeKeys.Generate()));
    Console.Error.WriteLine("Store this key safely: paired actuators only obey commands signed with it.");
    return ExitSuccess;
});

var keyTransferArgument = new Argument<byte[]>("keyTransferFrameHex") { Description = "A captured 1W key transfer (command 30), hex, as printed by 'receive'.", Arity = ArgumentArity.ExactlyOne, CustomParser = HexBytes(null) };
var recoverkeyCommand = new Command("recoverkey", $"Print the system key contained in a captured 1W key transfer. {OwnerWarning}") { keyTransferArgument };
recoverkeyCommand.SetAction(p =>
{
    Console.Error.WriteLine($"warning: {OwnerWarning}");
    if (!IoHomeFrame.TryParse(p.GetValue(keyTransferArgument), out var frame))
    {
        Console.Error.WriteLine("error: not a CRC-valid IoHome frame.");
        return ExitFailure;
    }
    if (frame.Command != IoHomeCommand.KeyTransfer1W)
    {
        Console.Error.WriteLine($"error: this is a {frame.Command} frame, not a 1W key transfer.");
        return ExitFailure;
    }
    var key = IoHomeKeys.RecoverFromKeyTransfer(frame);
    var verified = IoHomeKeys.Verify(key, frame);
    Console.WriteLine($"source {frame.Source:X6}  system key {Convert.ToHexString(key)}  MAC check {(verified ? "ok" : "FAILED")}");
    return verified ? ExitSuccess : ExitFailure;
});

var frameArgument = new Argument<byte[]>("frameHex") { Description = "A captured frame, hex, as printed by 'receive'.", Arity = ArgumentArity.ExactlyOne, CustomParser = HexBytes(null) };
var verifyCommand = new Command("verify", "Check a system key against a captured frame's MAC, without transmitting anything.") { keyArgument, frameArgument };
verifyCommand.SetAction(p =>
{
    if (!IoHomeFrame.TryParse(p.GetValue(frameArgument), out var frame))
    {
        Console.Error.WriteLine("error: not a CRC-valid IoHome frame.");
        return ExitFailure;
    }
    var verified = IoHomeKeys.Verify(p.GetValue(keyArgument), frame);
    Console.WriteLine(verified ? $"The key matches: it signed {frame}." : "The key does NOT match this frame.");
    return verified ? ExitSuccess : ExitFailure;
});

// A plain command rather than RootCommand, so help shows the tool command name rather than the assembly name.
var root = new Command(ToolCommandName, "Send, receive, capture and decode 868 MHz home-automation radio protocols with an ADALM-PLUTO SDR.")
{
    new HelpOption(),
    new VersionOption(),
    selftestCommand,
    receiveCommand,
    captureCommand,
    decodeCommand,
    new Command("domino", "Domino protocol (869.0 MHz): blinds, lights and switches.") { dominoSendCommand, dominoReceiveCommand, dominoDecodeCommand },
    new Command("iohome", "IoHome protocol (868.95 MHz, single-way): windows and blinds.")
    {
        ioHomeSendCommand, ioHomePairCommand, ioHomeReceiveCommand, ioHomeDecodeCommand, ioHomeDiagnoseCommand, genkeyCommand, recoverkeyCommand, verifyCommand,
    },
};

try
{
    return await root.Parse(args).InvokeAsync(new InvocationConfiguration { EnableDefaultExceptionHandler = false });
}
catch (OperationCanceledException)
{
    return ExitSuccess;
}
catch (Exception e) when (e is SocketException or TimeoutException)
{
    Console.Error.WriteLine($"error: cannot reach the radio: {e.Message} Check that it is connected and that --host/--port are right.");
    return ExitFailure;
}
catch (Exception e)
{
    Console.Error.WriteLine($"error: {e.Message}");
    return ExitFailure;
}
