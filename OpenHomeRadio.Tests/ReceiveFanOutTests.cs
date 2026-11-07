using System.Runtime.CompilerServices;
using OpenHomeRadio.Sdr;

namespace OpenHomeRadio.Tests;

[TestClass]
public sealed class ReceiveFanOutTests
{
    private const int QueueCapacity = 16;
    private const int BufferCount = 5;

    /// <summary>A receive source that waits for a gate, yields numbered buffers, then idles until cancelled.</summary>
    private sealed class GatedSource
    {
        private int _starts;

        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Stopped { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IqBuffer[] Buffers { get; } = [.. Enumerable.Range(0, BufferCount).Select(i => new IqBuffer(new short[] { (short)i, 0 }, TestSignals.SharedCenterHz, TestSignals.SampleRateHz))];
        public Exception? FailAfterBuffers { get; init; }
        public int Starts => Volatile.Read(ref _starts);

        public async IAsyncEnumerable<IqBuffer> RunAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _starts);
            Stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                await Gate.Task.WaitAsync(cancellationToken);
                foreach (var buffer in Buffers)
                    yield return buffer;
                if (FailAfterBuffers != null)
                    throw FailAfterBuffers;
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            finally
            {
                Stopped.TrySetResult();
            }
        }
    }

    private static async Task<IqBuffer[]> TakeAsync(IAsyncEnumerator<IqBuffer> subscriber, ValueTask<bool> firstMove)
    {
        var received = new List<IqBuffer>();
        for (var more = await firstMove; more; more = received.Count < BufferCount && await subscriber.MoveNextAsync())
            received.Add(subscriber.Current);
        return [.. received];
    }

    [TestMethod]
    public async Task ConcurrentSubscribers_ShareOneSourceAndSeeTheSameBuffers()
    {
        var source = new GatedSource();
        await using var fanOut = new ReceiveFanOut(source.RunAsync, QueueCapacity);
        await using var first = fanOut.SubscribeAsync().GetAsyncEnumerator();
        await using var second = fanOut.SubscribeAsync().GetAsyncEnumerator();

        // The first MoveNextAsync registers each subscriber before any buffer flows.
        var firstMove = first.MoveNextAsync();
        var secondMove = second.MoveNextAsync();
        source.Gate.SetResult();

        var received = await Task.WhenAll(TakeAsync(first, firstMove), TakeAsync(second, secondMove));
        CollectionAssert.AreEqual(source.Buffers, received[0]);
        CollectionAssert.AreEqual(source.Buffers, received[1]);
        Assert.AreEqual(1, source.Starts);
    }

    [TestMethod]
    public async Task LastSubscriberLeaving_StopsTheSource_AndTheNextOneRestartsIt()
    {
        var source = new GatedSource();
        source.Gate.SetResult();
        await using var fanOut = new ReceiveFanOut(source.RunAsync, QueueCapacity);

        await using (var subscriber = fanOut.SubscribeAsync().GetAsyncEnumerator())
            Assert.IsTrue(await subscriber.MoveNextAsync());
        await source.Stopped.Task;
        Assert.AreEqual(1, source.Starts);

        await using (var subscriber = fanOut.SubscribeAsync().GetAsyncEnumerator())
        {
            Assert.IsTrue(await subscriber.MoveNextAsync());
            Assert.AreSame(source.Buffers[0], subscriber.Current);
        }
        Assert.AreEqual(2, source.Starts);
    }

    [TestMethod]
    public async Task SourceFailure_IsThrownToSubscribers()
    {
        var source = new GatedSource { FailAfterBuffers = new IOException("radio gone") };
        source.Gate.SetResult();
        await using var fanOut = new ReceiveFanOut(source.RunAsync, QueueCapacity);

        var received = new List<IqBuffer>();
        var error = await Assert.ThrowsExactlyAsync<IOException>(async () =>
        {
            await foreach (var buffer in fanOut.SubscribeAsync())
                received.Add(buffer);
        });
        Assert.AreEqual("radio gone", error.Message);
        CollectionAssert.AreEqual(source.Buffers, received);
    }

    [TestMethod]
    public async Task Dispose_EndsSubscriptionsNormally_AndRejectsNewOnes()
    {
        var source = new GatedSource();
        var fanOut = new ReceiveFanOut(source.RunAsync, QueueCapacity);
        await using var subscriber = fanOut.SubscribeAsync().GetAsyncEnumerator();
        var move = subscriber.MoveNextAsync();

        await fanOut.DisposeAsync();

        Assert.IsFalse(await move);
        await source.Stopped.Task;
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(async () => await fanOut.SubscribeAsync().GetAsyncEnumerator().MoveNextAsync());
    }
}
