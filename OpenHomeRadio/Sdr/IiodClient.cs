using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;

namespace OpenHomeRadio.Sdr;

/// <summary>
/// A minimal client for the text protocol of the IIO network daemon (iiod) that runs on the ADALM-PLUTO and other
/// libiio devices. Pure managed TCP, so it works on every platform. One connection holds at most one open buffer per
/// device, so receive and transmit each use their own connection. Not thread-safe.
/// </summary>
internal sealed class IiodClient : IAsyncDisposable
{
    private const int BytesPerSample = 2 * sizeof(short); // int16 I + int16 Q
    private const int ReadBufferBytes = 64 * 1024;
    private const string AllIqChannelsMask = "00000003";

    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly BufferedStream _reader;
    private readonly TimeSpan _timeout;
    private readonly Dictionary<(string Device, string Channel, string Attribute), string> _written = [];
    private Dictionary<string, string> _deviceIds = [];
    private byte[] _receiveBytes = [];

    private IiodClient(TcpClient tcp, TimeSpan timeout)
    {
        _tcp = tcp;
        _stream = tcp.GetStream();
        _reader = new BufferedStream(_stream, ReadBufferBytes);
        _timeout = timeout;
    }

    /// <summary>Connects and reads the device list.</summary>
    public static async Task<IiodClient> ConnectAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            return await WithTimeoutAsync(timeout, async ct =>
            {
                await tcp.ConnectAsync(host, port, ct).ConfigureAwait(false);
                var client = new IiodClient(tcp, timeout); // the stream exists only once connected
                await client.SendAsync("PRINT", ct).ConfigureAwait(false);
                var xml = new byte[await client.ReadIntegerAsync(ct).ConfigureAwait(false)];
                await client._reader.ReadExactlyAsync(xml, ct).ConfigureAwait(false);
                await client.ReadLineAsync(ct).ConfigureAwait(false);
                client._deviceIds = XDocument.Parse(Encoding.ASCII.GetString(xml)).Root!.Elements("device")
                    .ToDictionary(d => (string)d.Attribute("name")!, d => (string)d.Attribute("id")!);
                return client;
            }, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    /// <summary>The iiod identifier (e.g. "iio:device0") of the device with the given name.</summary>
    public string GetDeviceId(string name) =>
        _deviceIds.TryGetValue(name, out var id) ? id : throw new IOException($"The radio does not expose the IIO device '{name}'.");

    /// <summary>Writes a device or channel attribute, skipping the write if this connection already set that value.</summary>
    public Task WriteAttributeAsync(string device, string channel, string attribute, string value, CancellationToken cancellationToken) =>
        _written.TryGetValue((device, channel, attribute), out var current) && current == value
            ? Task.CompletedTask
            : WithTimeoutAsync(_timeout, async ct =>
            {
                var data = Encoding.ASCII.GetBytes(value);
                await SendAsync($"WRITE {device} {channel} {attribute} {data.Length}", ct).ConfigureAwait(false);
                await _stream.WriteAsync(data, ct).ConfigureAwait(false);
                await ReadIntegerAsync(ct).ConfigureAwait(false);
                _written[(device, channel, attribute)] = value;
                return true;
            }, cancellationToken);

    /// <summary>Opens a streaming buffer of <paramref name="samples"/> I/Q samples on the device's first I/Q channel pair.</summary>
    public Task OpenAsync(string device, int samples, CancellationToken cancellationToken) =>
        CommandAsync($"OPEN {device} {samples} {AllIqChannelsMask}", cancellationToken);

    /// <summary>Closes the device's open buffer.</summary>
    public Task CloseAsync(string device, CancellationToken cancellationToken) =>
        CommandAsync($"CLOSE {device}", cancellationToken);

    /// <summary>Reads one buffer of interleaved int16 I/Q samples from the device's open buffer.</summary>
    public Task<short[]> ReadBufferAsync(string device, int samples, CancellationToken cancellationToken) =>
        WithTimeoutAsync(_timeout, async ct =>
        {
            var length = samples * BytesPerSample;
            if (_receiveBytes.Length != length)
                _receiveBytes = new byte[length];
            await SendAsync($"READBUF {device} {length}", ct).ConfigureAwait(false);
            for (var (offset, maskRead) = (0, false); offset < length; maskRead = true)
            {
                var chunk = await ReadIntegerAsync(ct).ConfigureAwait(false);
                if (!maskRead)
                    await ReadLineAsync(ct).ConfigureAwait(false); // the channel mask precedes the first chunk
                await _reader.ReadExactlyAsync(_receiveBytes.AsMemory(offset, chunk), ct).ConfigureAwait(false);
                offset += chunk;
            }
            var result = new short[length / sizeof(short)];
            Buffer.BlockCopy(_receiveBytes, 0, result, 0, length);
            return result;
        }, cancellationToken);

    /// <summary>Writes interleaved int16 I/Q samples to the device's open buffer.</summary>
    public Task WriteBufferAsync(string device, ReadOnlyMemory<short> samples, CancellationToken cancellationToken) =>
        WithTimeoutAsync(_timeout, async ct =>
        {
            var bytes = new byte[samples.Length * sizeof(short)];
            Buffer.BlockCopy(samples.ToArray(), 0, bytes, 0, bytes.Length);
            await SendAsync($"WRITEBUF {device} {bytes.Length}", ct).ConfigureAwait(false);
            await _stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            return await ReadIntegerAsync(ct).ConfigureAwait(false);
        }, cancellationToken);

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _reader.Dispose();
        _tcp.Dispose();
        return ValueTask.CompletedTask;
    }

    private Task CommandAsync(string command, CancellationToken cancellationToken) =>
        WithTimeoutAsync(_timeout, async ct =>
        {
            await SendAsync(command, ct).ConfigureAwait(false);
            return await ReadIntegerAsync(ct).ConfigureAwait(false);
        }, cancellationToken);

    private static async Task<T> WithTimeoutAsync<T>(TimeSpan timeout, Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            return await operation(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"The radio did not respond within {timeout.TotalSeconds:0.#} s.");
        }
    }

    private Task SendAsync(string command, CancellationToken cancellationToken) =>
        _stream.WriteAsync(Encoding.ASCII.GetBytes(command + "\r\n"), cancellationToken).AsTask();

    private async Task<string> ReadLineAsync(CancellationToken cancellationToken)
    {
        var line = new StringBuilder();
        var next = new byte[1];
        while (true)
        {
            if (await _reader.ReadAsync(next, cancellationToken).ConfigureAwait(false) == 0)
                throw new EndOfStreamException("The radio closed the connection.");
            if (next[0] == '\n')
                return line.ToString().TrimEnd('\r');
            line.Append((char)next[0]);
        }
    }

    private async Task<int> ReadIntegerAsync(CancellationToken cancellationToken)
    {
        var value = int.Parse(await ReadLineAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        return value < 0 ? throw new IOException($"The radio returned error code {-value}.") : value;
    }
}
