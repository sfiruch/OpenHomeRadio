using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace OpenHomeRadio.Sdr;

/// <summary>
/// Shares one receive stream among any number of concurrent subscribers. The source is started when the first
/// subscriber arrives and cancelled when the last one leaves; a restart waits for the previous run to finish, so the
/// hardware is never opened twice. Each subscriber has its own bounded queue that drops the oldest buffer when the
/// subscriber falls behind, so a slow consumer never stalls the radio or the other subscribers. If the source fails,
/// every current subscriber's enumeration throws the error; the next subscriber starts a fresh run.
/// </summary>
internal sealed class ReceiveFanOut(Func<CancellationToken, IAsyncEnumerable<IqBuffer>> source, int queueCapacity) : IAsyncDisposable
{
    private readonly Lock _lock = new();
    private Session? _session;
    private Task _lastRun = Task.CompletedTask;
    private bool _disposed;

    /// <summary>Joins the shared stream; buffers flow until the enumeration is cancelled or disposed.</summary>
    public async IAsyncEnumerable<IqBuffer> SubscribeAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var queue = Channel.CreateBounded<IqBuffer>(new BoundedChannelOptions(queueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });
        Session session;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_session == null)
            {
                var started = new Session();
                var previous = _lastRun;
                _lastRun = Task.Run(() => RunAsync(started, previous));
                _session = started;
            }
            session = _session;
            session.Subscribers.Add(queue.Writer);
        }

        try
        {
            await foreach (var buffer in queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                yield return buffer;
        }
        finally
        {
            lock (_lock)
            {
                session.Subscribers.Remove(queue.Writer);
                if (session.Subscribers.Count == 0 && _session == session)
                {
                    _session = null;
                    session.Stop.Cancel();
                }
            }
        }
    }

    /// <summary>Stops the source, ends every subscription normally, and waits for the source to shut down.</summary>
    public async ValueTask DisposeAsync()
    {
        Task run;
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            _session?.Stop.Cancel();
            run = _lastRun;
        }
        await run.ConfigureAwait(false);
    }

    private async Task RunAsync(Session session, Task previous)
    {
        await previous.ConfigureAwait(false);
        Exception? error = null;
        try
        {
            await foreach (var buffer in source(session.Stop.Token).WithCancellation(session.Stop.Token).ConfigureAwait(false))
                lock (_lock)
                    foreach (var subscriber in session.Subscribers)
                        subscriber.TryWrite(buffer);
        }
        catch (OperationCanceledException) when (session.Stop.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            error = exception;
        }

        lock (_lock)
        {
            if (_session == session)
                _session = null;
            foreach (var subscriber in session.Subscribers)
                subscriber.TryComplete(error);
        }
    }

    private sealed class Session
    {
        public List<ChannelWriter<IqBuffer>> Subscribers { get; } = [];
        public CancellationTokenSource Stop { get; } = new();
    }
}
