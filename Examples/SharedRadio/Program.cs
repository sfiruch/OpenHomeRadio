using System.Text.Json;
using OpenHomeRadio.Domino;
using OpenHomeRadio.IoHome;
using OpenHomeRadio.Sdr;

// How a home-automation host uses OpenHomeRadio: ONE radio shared by both protocol clients, both receive loops running
// concurrently, commands sent while receiving, and the rolling counters persisted after every send so that devices keep
// accepting commands after a restart. The addresses are synthetic examples; use your own devices' values.

const string StatePath = "sharedradio-state.json";
const uint WindowRemoteAddress = 0xABCDEF;              // the address our own IoHome remote identity transmits as
var blind = new DominoDevice(Address: 0x123456, Group: 0x20); // a Domino button address the blind has learned
var listenAfterSends = TimeSpan.FromSeconds(30);

// The state file holds the IoHome system key, so a real application should store it somewhere protected.
var saved = File.Exists(StatePath) ? JsonSerializer.Deserialize<RadioState>(await File.ReadAllTextAsync(StatePath)) : null;
var state = saved ?? new RadioState(Convert.ToHexString(IoHomeKeys.Generate()), DominoCounter: 0, IoHomeSequence: 0);

await using var sdr = new PlutoSdr(); // the Pluto's factory-default address 192.168.2.1:30431
var domino = new DominoClient(sdr) { Counter = state.DominoCounter };
var ioHome = new IoHomeClient(sdr);
var window = new IoHomeRemote(Convert.FromHexString(state.IoHomeKey), WindowRemoteAddress) { Sequence = state.IoHomeSequence };

async Task SaveAsync() =>
    await File.WriteAllTextAsync(StatePath, JsonSerializer.Serialize(new RadioState(state.IoHomeKey, domino.Counter, window.Sequence)));

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.Cancel();
};

// Both receive loops share the radio's single receive stream.
var receivers = Task.WhenAll(
    Task.Run(async () =>
    {
        await foreach (var press in domino.ReceiveAsync(stop.Token))
            Console.WriteLine($"{DateTime.Now:T}  Domino  {press}");
    }),
    Task.Run(async () =>
    {
        await foreach (var frame in ioHome.ReceiveAsync(stop.Token))
            Console.WriteLine($"{DateTime.Now:T}  IoHome  {frame}");
    }));

try
{
    if (saved is null)
    {
        Console.WriteLine("New IoHome remote identity: put the window actuator into learn mode now; pairing...");
        await ioHome.PairAsync(window, stop.Token);
        await SaveAsync();
    }

    await domino.SendAsync(blind, DominoCommand.ContinuousDown, stop.Token);
    await SaveAsync();
    Console.WriteLine("Sent Domino ContinuousDown.");

    await ioHome.SendAsync(window, IoHomeAction.Down, stop.Token); // sweeps the transmit frequency; takes several seconds
    await SaveAsync();
    Console.WriteLine("Sent IoHome Down.");

    Console.WriteLine($"Listening for another {listenAfterSends.TotalSeconds} s (Ctrl+C to stop)...");
    stop.CancelAfter(listenAfterSends);
    await receivers;
}
catch (OperationCanceledException)
{
}

/// <summary>What must survive a restart: the IoHome key, and the counters devices use to reject replays.</summary>
internal sealed record RadioState(string IoHomeKey, byte DominoCounter, ushort IoHomeSequence);
