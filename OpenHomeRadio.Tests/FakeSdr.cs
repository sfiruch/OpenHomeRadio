using OpenHomeRadio.Sdr;

namespace OpenHomeRadio.Tests;

/// <summary>
/// An in-memory <see cref="ISdrTransceiver"/>: records every transmission and serves a scripted sequence of receive
/// buffers (enumerated lazily, so a script can advance a fake clock between buffers), after which reception ends.
/// </summary>
internal sealed class FakeSdr(IEnumerable<IqBuffer>? receiveScript = null) : ISdrTransceiver
{
    private readonly List<(IqBuffer Signal, TransmitOptions Options)> _transmissions = [];

    public long SampleRateHz => TestSignals.SampleRateHz;

    public IReadOnlyList<(IqBuffer Signal, TransmitOptions Options)> Transmissions
    {
        get
        {
            lock (_transmissions)
                return [.. _transmissions];
        }
    }

    public Task TransmitAsync(IqBuffer signal, TransmitOptions options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_transmissions)
            _transmissions.Add((signal, options));
        return Task.CompletedTask;
    }

    public IAsyncEnumerable<IqBuffer> ReceiveAsync(CancellationToken cancellationToken = default) =>
        (receiveScript ?? []).ToAsyncEnumerable();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
