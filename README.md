# OpenHomeRadio

[![.NET](https://github.com/sfiruch/OpenHomeRadio/actions/workflows/dotnet.yml/badge.svg)](https://github.com/sfiruch/OpenHomeRadio/actions/workflows/dotnet.yml)
[![NuGet](https://img.shields.io/nuget/v/sfiruch.OpenHomeRadio.svg)](https://www.nuget.org/packages/sfiruch.OpenHomeRadio/)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](https://github.com/sfiruch/OpenHomeRadio/blob/main/LICENSE)

A .NET library for controlling and listening to your own 868 MHz home-automation devices (blinds, roof windows,
lights, switches) with an ADALM-PLUTO software-defined radio. No vendor gateway is needed. It is pure managed code,
async-first, and runs anywhere .NET 10 runs, including a Raspberry Pi (Linux arm64).

The library currently supports **two device classes**. More protocols will follow; each lives in its own namespace
and follows the same pattern (see [Adding a protocol](#adding-a-protocol)).

> **Read the [Disclaimer](#disclaimer) before you transmit anything.** You are responsible for operating the radio
> lawfully and for what happens to your devices.

## Supported devices

| Namespace | Compatible with | Frequency | Capabilities | Verification |
|---|---|---|---|---|
| `OpenHomeRadio.Domino` | Dominoswiss-compatible blind, light and socket actuators and their remotes | 869.000 MHz | Send commands, receive button presses | Send and receive verified on real hardware |
| `OpenHomeRadio.IoHome` | io-homecontrol single-way (1W) actuators, e.g. Velux roof windows and blinds | 868.950 MHz | Send up/down/stop, receive and decode frames, recover and verify keys, pair a new controller, positions | Send, receive and key recovery verified on real hardware; pairing a *new* controller and positions are implemented from the protocol description but not yet hardware-tested |

Protocol details: [docs/Domino.md](docs/Domino.md), [docs/IoHome.md](docs/IoHome.md).

## Requirements

- An [ADALM-PLUTO](https://www.analog.com/en/resources/evaluation-hardware-and-software/evaluation-boards-kits/adalm-pluto.html)
  reachable over the network (its default USB-Ethernet address is `192.168.2.1`) and an antenna for 868 MHz.
  See [docs/Hardware.md](docs/Hardware.md).
- .NET 10.

## Installation

```bash
dotnet add package sfiruch.OpenHomeRadio
```

The command-line tool (for exploring, capturing and testing) is a separate package:

```bash
dotnet tool install --global sfiruch.OpenHomeRadio.Cli
```

## Quick start

One `PlutoSdr` serves every protocol client. Create it once and share it.

```csharp
using OpenHomeRadio.Sdr;
using OpenHomeRadio.Domino;
using OpenHomeRadio.IoHome;

await using var sdr = new PlutoSdr();                // defaults to 192.168.2.1:30431
```

### Domino

Domino devices have no pairing or encryption: an actuator obeys the button addresses it has learned. Read the
address and group of your remote's button with `ReceiveAsync` (or `openhomeradio domino receive`), then send as that
button.

```csharp
var domino = new DominoClient(sdr) { Counter = state.DominoCounter };
var blind = new DominoDevice(Address: 0x123456, Group: 0x20);

await domino.SendAsync(blind, DominoCommand.ContinuousDown);   // full travel; Up/Down step, Stop halts
state.DominoCounter = domino.Counter;                            // persist

await foreach (var press in domino.ReceiveAsync(cancellationToken))   // one event per physical press
    Console.WriteLine($"{press.Address:X6} {press.Command}");
```

### IoHome

IoHome frames are authenticated with a per-controller 16-byte system key. You either pair a new controller of your
own, or act as an existing remote of your installation whose key you have recovered (see [docs/IoHome.md](docs/IoHome.md)).

```csharp
var iohome = new IoHomeClient(sdr);

// A new controller: generate a key and pick an address ONCE, store both, then pair with the actuator in learn mode.
var remote = new IoHomeRemote(IoHomeKeys.Generate(), address: 0xABCDEF);
await iohome.PairAsync(remote);

// Later (restore key, address and sequence from your storage):
var stored = new IoHomeRemote(state.Key, state.Address) { Sequence = state.Sequence };
await iohome.SendAsync(stored, IoHomeAction.Down);       // Up / Down / Stop
state.Sequence = stored.Sequence;                        // persist after EVERY send

await foreach (var frame in iohome.ReceiveAsync(cancellationToken))
    Console.WriteLine(frame);
```

A send transmits the same frame across a sweep of frequency corrections (see [Frequency sweep](#frequency-sweep)),
so it takes several seconds with the default settings.

## Using both protocols at once

`PlutoSdr` is thread-safe and designed to be shared:

- Transmissions from all clients are queued and sent one at a time.
- Reception runs **one** wideband stream (default centre 868.75 MHz, 2.5 MS/s, covering both carriers) that starts
  with the first subscriber and stops with the last. Every `ReceiveAsync` enumeration sees the same buffers, and each
  protocol's demodulator finds its own carrier inside them.

In an ASP.NET Core (or generic host) application:

```csharp
builder.Services.AddSingleton<ISdrTransceiver>(_ => new PlutoSdr(new PlutoSdrOptions { Host = "192.168.2.1" }));
builder.Services.AddSingleton(sp => new DominoClient(sp.GetRequiredService<ISdrTransceiver>()));
builder.Services.AddSingleton(sp => new IoHomeClient(sp.GetRequiredService<ISdrTransceiver>()));
builder.Services.AddHostedService<RadioListener>();

sealed class RadioListener(DominoClient domino, IoHomeClient iohome) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.WhenAll(
        Task.Run(async () => { await foreach (var press in domino.ReceiveAsync(stoppingToken)) { /* react */ } }, stoppingToken),
        Task.Run(async () => { await foreach (var frame in iohome.ReceiveAsync(stoppingToken)) { /* react */ } }, stoppingToken));
}
```

A complete console example lives in [Examples/SharedRadio](Examples/SharedRadio).

## Persisting state

Both protocols use a per-sender counter that receivers check, so **store it after every send and restore it at
start-up**:

- `DominoClient.Counter`: an actuator ignores a command whose counter equals the previous one it saw. Any different
  value works, so an exact restore is not critical.
- `IoHomeRemote.Sequence`: an actuator rejects any sequence number not above the highest it has accepted from that
  controller address. Restoring an old value makes commands silently ignored. When you act as an existing remote, your
  commands also move the actuator's counter past the physical remote's, which is then ignored until it catches up, so
  stay only slightly above the remote's own value.

The system key, address and device mapping are installation data: keep them in your own configuration, never in code.

## Frequency sweep

The Pluto's reference oscillator is only accurate to about ±25 ppm (roughly ±22 kHz at 868 MHz), while an IoHome
actuator's receive window is a few kHz wide, and the error was not stable between transmissions in testing. By
default `IoHomeClient` therefore sends the same frame, with one sequence number, at corrections from −20 kHz to
+20 kHz in 2.5 kHz steps. The actuator acts on the first copy that lands and rejects the rest as replays, so the sweep
counts as one command. Once you know your radio's correction you can set `IoHomeSettings.TxFrequencyOffsetsHz` to a
single value. The Domino protocol is wideband and needs no correction.

## Command-line tool

```
openhomeradio selftest                          # protocol self-test, no hardware needed
openhomeradio receive                           # decode all protocols live
openhomeradio capture 30 capture.cs16           # record raw I/Q
openhomeradio decode capture.cs16
openhomeradio domino send 123456 20 Toggle
openhomeradio iohome send 000102030405060708090A0B0C0D0E0F ABCDEF down --sequence 0042
```

Full reference: [docs/Cli.md](docs/Cli.md).

## Adding a protocol

Each protocol is a namespace with the same four parts, built on the shared `OpenHomeRadio.Sdr` layer:

| Part | Role | Example |
|---|---|---|
| `XxxSettings` | Carrier, modulation, timing, transmit power | `DominoSettings` |
| `XxxFrame` | On-air frame: build, serialise, parse, checksum | `IoHomeFrame` |
| `XxxModem` | Frames ↔ `IqBuffer`; demodulation computes its offset from the buffer's centre frequency | `DominoModem` |
| `XxxClient` | Async send/receive over a shared `ISdrTransceiver` | `IoHomeClient` |

Because demodulators work on any buffer that contains their carrier, a new protocol automatically works alongside
the existing ones on the shared receive stream. Another SDR can be supported by implementing `ISdrTransceiver`.

## Disclaimer

**This software is provided "as is", without warranty of any kind, express or implied.** It is experimental and
based on independent observation. Use it entirely at your own risk. The author accepts no liability for any damage
to property, devices or persons, for loss of data, or for legal consequences arising from its use. See [LICENSE](LICENSE).

- **Radio regulations.** Transmitting on 868 MHz is regulated (in Europe, for example, by ETSI EN 300 220 with
  limits on power and duty cycle). An SDR is not a certified transmitter, and IoHome sends at full power by default.
  You alone are responsible for operating your equipment lawfully in your jurisdiction.
- **Your own devices only.** Only control devices you own or are authorised to control. Key recovery in particular
  must only be used on your own installation.
- **Device warranties.** Operating devices outside their vendor's ecosystem may void their warranty or put them in
  an unexpected state (for example, locking out a physical remote; see [Persisting state](#persisting-state)).
  Windows and blinds move: make sure nothing and nobody is in the way.
- **Trademarks.** Velux, Somfy, io-homecontrol, Dominoswiss, Brelag and other names are trademarks of their
  respective owners. This project is independent and is not affiliated with, endorsed by or supported by any of them.
  These names are used only to describe compatibility.
- **Patents.** No patent licence is granted or implied. The protocols implemented here may be subject to patents in
  some jurisdictions; you are responsible for ensuring your use is lawful.

The protocol descriptions come from observing the author's own devices and from publicly available community
documentation, in particular [iown-homecontrol](https://github.com/Velocet/iown-homecontrol) and
[home_io_control](https://github.com/laberning/home_io_control). No code was taken from those projects.

## License

[MIT](LICENSE) © 2026 Simon Felix
