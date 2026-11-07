# IoHome protocol

Namespace `OpenHomeRadio.IoHome`. Compatible with single-way (1W) io-homecontrol actuators and remotes, such as
Velux roof windows and blinds. The description combines observation of real devices with publicly available community
documentation ([iown-homecontrol](https://github.com/Velocet/iown-homecontrol),
[home_io_control](https://github.com/laberning/home_io_control)); it is not an official specification. Two-way (2W)
operation is not implemented. All example values are synthetic.

## Physical layer (1W)

| Property | Value |
|---|---|
| Frequency | 868.95 MHz (channel 2; channels 1 and 3 are used only by 2W frequency hopping) |
| Modulation | 2-FSK, NRZ, ±19.2 kHz deviation, no shaping, no whitening, no FEC |
| Bit rate | 38.4 kbit/s |
| Preamble | `0x55` bytes (52 bytes, as real remotes send) |
| Sync word | `FF 33` |
| Byte framing | UART-style: start bit 0, 8 data bits least significant first, stop bit 1 |
| Repetition | Every frame is sent several times |

Some window remotes transmit with roughly twice the nominal deviation. `IoHomeSettings.DiscriminatorSmoothingSamples`
(default `[1, 16]`) demodulates every buffer with and without extra smoothing so both variants decode.

## Frame

```
ctrl0 | ctrl1 | dest[3] | src[3] | command | parameters[0..21] | sequence[2] | MAC[6] | CRC[2]
```

- **Control byte 0**: bits 7–6 first/last frame (both set), bit 5 single-way, bits 4–0 length. The length counts every
  byte except control byte 0 and the CRC, including the MAC (Execute: `0xF6` for 6 parameter bytes). Key transfers
  count it without the MAC (`0xFC`).
- **Control byte 1**: flags; bit 5 (`0x20`, low power) appears on some real frames.
- **Addresses**: 24-bit, big-endian. Destination `00 00 3F` is the broadcast that reaches every actuator paired to the
  source.
- **Sequence**: 16-bit rolling code, big-endian.
- **CRC**: CRC-16/KERMIT (poly `0x8408` reflected, init 0), appended low byte first.

### Execute (command `0x00`)

Parameters: `originator (0x01) · ACEI · main parameter (2 bytes) · FP1 (0) · FP2 (0)`.

| Main parameter | `IoHomeAction` |
|---|---|
| `0x0000` | `Up` / open (0 %) |
| `0xC800` | `Down` / closed (100 %) |
| `0xD200` | `Stop` |
| `0x0000`–`0xC800` | Position 0–100 % (`SendPositionAsync`; from the protocol description, not hardware-tested) |

The ACEI (priority) byte is `0x61` for roof-window and blind actuators (`IoHomeRemote.Acei`; some other actuator
families expect `0x43`).

Example (synthetic key `000102030405060708090A0B0C0D0E0F`, source `ABCDEF`, broadcast, `Down`, sequence `0x0001`):

```
F6 00 00003F ABCDEF 00 0161C8000000 0001 888B2B333B95 7D81
```

## Cryptography

- **MAC** (6 bytes): an initial value is built from the first 8 payload bytes (command and parameters, padded with
  `0x55`), two bytes of a proprietary checksum over the whole payload, the 2 sequence bytes and `0x55` padding. It is
  AES-128 encrypted under the system key; the MAC is the first 6 bytes. See `IoHomeCrypto.ConstructIv1W`.
- **System key**: 16 bytes per controller, shared with the actuators it is paired to. It never travels in clear.
- **1W key transfer** (command `0x30`): parameters `encryptedKey[16] · manufacturer · 0x01`. The key is XORed with
  AES-128 of the controller's 3-byte address (repeated to 16 bytes) under the protocol's published *transfer key*.
  The MAC covers only the command and the encrypted key.

### Why key recovery works

The transfer key is a fixed, public constant, and the controller's address is sent in clear in the same frame. Anyone
who receives a 1W key transfer can therefore undo the XOR and obtain the system key. This is a known property of the
protocol, not something this library introduces. A remote broadcasts such a frame when it is paired or when its key is
copied to another remote; a copy attempt without a second remote only emits discovery frames (`0x2E`).

`IoHomeKeys.RecoverFromKeyTransfer` does this for a captured frame, and `IoHomeKeys.Verify` proves a key by recomputing
the MAC of another captured frame, without transmitting anything. **Use this only on installations you own or are
authorised to control.** Two-way remotes exchange keys by challenge-response and do not leak them this way.

## Controllers and pairing

An actuator obeys commands from a controller address it has learned, signed with that controller's key. There are two
ways to get such a controller:

1. **Pair a new one.** Generate a key (`IoHomeKeys.Generate`), choose an address, put the actuator in learn mode and
   call `PairAsync`. Store the key, address and sequence; regenerating the key un-pairs every actuator.
2. **Act as an existing remote** of your installation, with its address (visible in its frames) and its recovered key.

One `IoHomeRemote` is one controller identity: only actuators paired to its address react. Use several identities to
control devices independently.

## Sequence numbers and lock-out

An actuator tracks the highest sequence it has accepted per controller address and rejects any value not above it.

- Persist `IoHomeRemote.Sequence` after every send; restarting from an old value makes commands silently ignored.
- When acting as an existing remote, every command moves the actuator's counter past the physical remote's, which is
  then ignored until its own counter catches up. Keep your sequence only slightly above the remote's current value
  (read it from a received frame).
- A frequency sweep uses a single sequence for all its transmissions, so it advances the counter by one.

## Transmit frequency

The actuator's receive window is a few kHz wide. An SDR's reference oscillator (about ±25 ppm on the ADALM-PLUTO) can
be off by more than that, and in testing the required correction was not stable between transmissions. `IoHomeClient`
therefore sends each frame across `IoHomeSettings.TxFrequencyOffsetsHz` (default −20 kHz to +20 kHz in 2.5 kHz steps)
with one sequence number. Reception is unaffected because the demodulator tracks the tones it sees.

## Open questions

- The root cause of the unstable transmit-frequency correction.
- Pairing a new controller and positions on real hardware.
- Whether directed (non-broadcast) commands to battery-powered actuators need the low-power flag in control byte 1.
