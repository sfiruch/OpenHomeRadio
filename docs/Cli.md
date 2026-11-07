# Command-line tool

```bash
dotnet tool install --global sfiruch.OpenHomeRadio.Cli
openhomeradio --help
```

Every protocol has the same `send`, `receive` and `decode` commands; protocol-specific extras sit under the protocol's
command. Addresses, groups, keys, sequences, counters and frames are hexadecimal (with or without `0x`); frequencies
are in Hz. Status messages go to stderr, decoded frames to stdout. All values below are examples.

Commands that use the radio accept `--host` (default `192.168.2.1`) and `--port` (default `30431`).

## All protocols

| Command | Description |
|---|---|
| `openhomeradio selftest` | Checks the protocol implementations against published test vectors and modulate/demodulate round trips. No hardware needed. |
| `openhomeradio receive` | Decodes all protocols at once from one shared receive stream. |
| `openhomeradio capture <seconds> <file.cs16>` | Records raw I/Q at the shared receive tuning. |
| `openhomeradio decode <file.cs16>` | Decodes all protocols from a capture. |

Capture files are raw interleaved little-endian 16-bit I/Q (`.cs16`). `decode` and `diagnose` take `--center`
(default `868750000`) and `--rate` (default `2500000`) to describe how a file was recorded.

## Domino

```bash
openhomeradio domino send 123456 20 Toggle
openhomeradio domino send 123456 20 ContinuousDown --counter 42
openhomeradio domino receive
openhomeradio domino decode capture.cs16
```

`send <address> <group> <command>` takes any `DominoCommand` name (case-insensitive) and the options `--value`,
`--counter` (default: random, so repeated runs are not ignored) and `--attenuation` (dB, default 10). It prints the
counter used.

## IoHome

```bash
openhomeradio iohome genkey
openhomeradio iohome pair 000102030405060708090A0B0C0D0E0F ABCDEF --sequence 0001
openhomeradio iohome send 000102030405060708090A0B0C0D0E0F ABCDEF down --sequence 0002
openhomeradio iohome send 000102030405060708090A0B0C0D0E0F ABCDEF 50 --sequence 0003 --offsets -2500
openhomeradio iohome receive
openhomeradio iohome decode capture.cs16
openhomeradio iohome diagnose capture.cs16
openhomeradio iohome recoverkey <keyTransferFrameHex>
openhomeradio iohome verify 000102030405060708090A0B0C0D0E0F <frameHex>
```

| Command | Description |
|---|---|
| `send <key> <address> <action>` | `action` is `up`, `down`, `stop` or a position 0–100. Options: `--sequence` (default 0, with a warning), `--offsets` (`min:max:step` or a single value; default `-20000:20000:2500`), `--attenuation` (dB, default 0), `--destination`. Prints the sequence used and the next one to pass. |
| `pair <key> <address>` | Sends a key transfer to actuators in learn mode. Same `--sequence`, `--offsets` and `--attenuation` options. |
| `receive`, `decode` | Print received frames with their raw hex, which `recoverkey` and `verify` accept. |
| `diagnose <file.cs16>` | Reports bursts, tones, deviation and sync-word positions, for radio bring-up and unfamiliar remotes. |
| `genkey` | Prints a new random 16-byte system key. Store it. |
| `recoverkey <frameHex>` | Prints the system key from a captured single-way key transfer and checks it against the frame's MAC. Only on installations you own or are authorised to control. |
| `verify <key> <frameHex>` | Checks whether a key signed a captured frame. Transmits nothing. |

Keep the sequence you pass above the highest value the actuator has accepted from that address, and only slightly
above a physical remote's value when acting as that remote (see [IoHome.md](IoHome.md#sequence-numbers-and-lock-out)).
