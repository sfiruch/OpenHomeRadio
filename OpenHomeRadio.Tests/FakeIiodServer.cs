using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace OpenHomeRadio.Tests;

/// <summary>
/// A loopback stand-in for the IIO daemon of an ADALM-PLUTO, implementing just the text commands the library uses
/// (PRINT, WRITE, OPEN, CLOSE, READBUF, WRITEBUF). Receive buffers are served one per permit from
/// <see cref="ReleaseReceiveBuffers"/>, buffer <c>k</c> filled with the value <c>k</c>, so tests control exactly how
/// many buffers flow. Everything the client does is logged per connection.
/// </summary>
internal sealed class FakeIiodServer : IAsyncDisposable
{
    public const string PhyId = "iio:device0";
    public const string TransmitId = "iio:device1";
    public const string ReceiveId = "iio:device2";
    private const string ContextXml =
        $"<?xml version=\"1.0\" encoding=\"utf-8\"?><context name=\"network\">" +
        $"<device id=\"{PhyId}\" name=\"ad9361-phy\"/><device id=\"{TransmitId}\" name=\"cf-ad9361-dds-core-lpc\"/>" +
        $"<device id=\"{ReceiveId}\" name=\"cf-ad9361-lpc\"/></context>";
    private const string ChannelMask = "00000003";
    private const int BusyError = -16;

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _receivePermits = new(0);
    private readonly ConcurrentBag<Task> _handlers = [];
    private readonly Task _acceptLoop;
    private int _connections;
    private int _nextReceiveBuffer;

    public FakeIiodServer()
    {
        _listener.Start();
        _acceptLoop = AcceptAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>Every command received, as (connection number, command line, payload as text or byte count).</summary>
    public ConcurrentQueue<(int Connection, string Command, string Payload)> Log { get; } = new();

    /// <summary>Interleaved samples of every WRITEBUF, in order.</summary>
    public ConcurrentQueue<short[]> WrittenBuffers { get; } = new();

    /// <summary>Number of OPEN commands for a device whose buffer was already open (iiod answers EBUSY).</summary>
    public int DoubleOpens;

    public void ReleaseReceiveBuffers(int count) => _receivePermits.Release(count);

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        await _acceptLoop;
        await Task.WhenAll(_handlers);
        _stop.Dispose();
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (true)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                _handlers.Add(HandleAsync(client, Interlocked.Increment(ref _connections)));
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException)
        {
        }
    }

    private async Task HandleAsync(TcpClient client, int connection)
    {
        using var _ = client;
        var stream = client.GetStream();
        var openDevices = new HashSet<string>();
        try
        {
            while (await ReadLineAsync(stream) is { } line)
            {
                var words = line.Split(' ');
                var payload = "";
                switch (words[0])
                {
                    case "PRINT":
                        var xml = Encoding.ASCII.GetBytes(ContextXml);
                        await WriteLineAsync(stream, xml.Length.ToString(CultureInfo.InvariantCulture));
                        await stream.WriteAsync(xml, _stop.Token);
                        await WriteLineAsync(stream, "");
                        break;
                    case "WRITE": // WRITE <device> <direction> <channel> <attribute> <length>, or without a channel for device attributes
                        var value = new byte[int.Parse(words[^1], CultureInfo.InvariantCulture)];
                        await stream.ReadExactlyAsync(value, _stop.Token);
                        payload = Encoding.ASCII.GetString(value);
                        await WriteLineAsync(stream, value.Length.ToString(CultureInfo.InvariantCulture));
                        break;
                    case "OPEN":
                        var opened = openDevices.Add(words[1]);
                        if (!opened)
                            Interlocked.Increment(ref DoubleOpens);
                        await WriteLineAsync(stream, (opened ? 0 : BusyError).ToString(CultureInfo.InvariantCulture));
                        break;
                    case "CLOSE":
                        openDevices.Remove(words[1]);
                        await WriteLineAsync(stream, "0");
                        break;
                    case "READBUF":
                        var length = int.Parse(words[2], CultureInfo.InvariantCulture);
                        await _receivePermits.WaitAsync(_stop.Token);
                        var samples = Enumerable.Repeat((short)(Interlocked.Increment(ref _nextReceiveBuffer) - 1), length / sizeof(short)).ToArray();
                        var bytes = new byte[length];
                        Buffer.BlockCopy(samples, 0, bytes, 0, length);
                        await WriteLineAsync(stream, length.ToString(CultureInfo.InvariantCulture));
                        await WriteLineAsync(stream, ChannelMask);
                        await stream.WriteAsync(bytes, _stop.Token);
                        break;
                    case "WRITEBUF":
                        var written = new byte[int.Parse(words[2], CultureInfo.InvariantCulture)];
                        await stream.ReadExactlyAsync(written, _stop.Token);
                        var writtenSamples = new short[written.Length / sizeof(short)];
                        Buffer.BlockCopy(written, 0, writtenSamples, 0, written.Length);
                        WrittenBuffers.Enqueue(writtenSamples);
                        payload = written.Length.ToString(CultureInfo.InvariantCulture);
                        await WriteLineAsync(stream, payload);
                        break;
                    default:
                        throw new InvalidDataException($"Unexpected iiod command '{line}'.");
                }
                Log.Enqueue((connection, line, payload));
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
        {
            // The client went away or the server is shutting down.
        }
    }

    private async Task WriteLineAsync(NetworkStream stream, string line) =>
        await stream.WriteAsync(Encoding.ASCII.GetBytes(line + "\n"), _stop.Token);

    private async Task<string?> ReadLineAsync(NetworkStream stream)
    {
        var line = new StringBuilder();
        var next = new byte[1];
        while (true)
        {
            if (await stream.ReadAsync(next, _stop.Token) == 0)
                return null;
            if (next[0] == '\n')
                return line.ToString().TrimEnd('\r');
            line.Append((char)next[0]);
        }
    }
}
