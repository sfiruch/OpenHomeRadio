# Hardware

## ADALM-PLUTO

OpenHomeRadio talks to an [ADALM-PLUTO](https://www.analog.com/en/resources/evaluation-hardware-and-software/evaluation-boards-kits/adalm-pluto.html)
through its IIO network daemon (iiod, TCP port 30431). It needs no native libraries (no libiio): only a TCP connection.

- **Over USB**: the Pluto appears as a USB Ethernet adapter and is reachable at `192.168.2.1`. On Windows this may need
  Analog Devices' USB drivers; on Linux (including Raspberry Pi OS) the RNDIS/CDC Ethernet drivers are built in.
- **Over Ethernet**: with a USB-Ethernet adapter on the Pluto, configure its address in the Pluto's `config.txt` and pass
  it as `PlutoSdrOptions.Host` / `--host`.

The 868 MHz band is within the AD9361's standard tuning range, so no firmware changes are needed. The library uses
2.5 MS/s, above the Pluto's minimum of about 2.1 MS/s.

`PlutoSdr` opens one connection for transmitting and one for receiving, so it can transmit while a receive stream runs.
Only one `PlutoSdr` instance should use a given Pluto at a time; share that instance between protocol clients.

## Antenna

The antennas supplied with the Pluto are not tuned for 868 MHz. A simple quarter-wave antenna for 868 MHz (about 8.2 cm)
or a commercial 868 MHz antenna improves range a lot. Indoor range was sufficient in testing for both protocols.

## Frequency error

The Pluto's reference oscillator is specified at about ±25 ppm, roughly ±22 kHz at 868 MHz. This does not matter for
the wideband Domino protocol or for receiving, but IoHome actuators only accept a narrow window. The IoHome client
therefore sweeps a range of transmit corrections by default; see [IoHome.md](IoHome.md#transmit-frequency). An external
reference clock or a calibrated `xo_correction` on the Pluto may make a single correction sufficient.

## Raspberry Pi

The library is pure managed .NET and runs on Linux arm64. A Pi 4 or 5 handles the shared 2.5 MS/s receive stream;
demodulation work grows with the number of protocols listening. Plug the Pluto into a USB port (powered hubs help) and
use `192.168.2.1`, or connect it over Ethernet.

## Other radios

Implement `ISdrTransceiver` to use a different SDR. Transmit signals arrive as `IqBuffer`s centred on the protocol's
carrier; receive buffers must report the centre frequency and sample rate they were captured at.
