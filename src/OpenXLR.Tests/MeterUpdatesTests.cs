using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using OpenXLR.UI;

namespace OpenXLR.Tests;

public sealed class MeterUpdatesTests
{
    [Fact]
    public void BurstKeepsOneCallbackAndOnlyItsNewestFrame()
    {
        var jobs = new Queue<Action>();
        var applied = new List<int>();
        var updates = new MeterUpdates(jobs.Enqueue, frame => applied.Add(frame.GetValue<int>()));
        for (int i = 0; i < 10000; i++) updates.Publish(JsonValue.Create(i)!);

        Assert.Single(jobs);
        jobs.Dequeue()();
        Assert.Equal([9999], applied);
        Assert.Empty(jobs);

        updates.Publish(JsonValue.Create(10000)!);
        Assert.Single(jobs);
        jobs.Dequeue()();
        Assert.Equal([9999, 10000], applied);
    }

    [Fact]
    public void FramesArrivingDuringApplicationGetAnotherTurn()
    {
        var jobs = new Queue<Action>();
        var applied = new List<int>();
        MeterUpdates? updates = null;
        updates = new MeterUpdates(jobs.Enqueue, frame =>
        {
            int value = frame.GetValue<int>();
            applied.Add(value);
            if (value != 1) return;
            updates!.Publish(JsonValue.Create(2)!);
            updates.Publish(JsonValue.Create(3)!);
        });
        updates.Publish(JsonValue.Create(1)!);

        jobs.Dequeue()();
        Assert.Equal([1], applied);
        Assert.Single(jobs);
        jobs.Dequeue()();
        Assert.Equal([1, 3], applied);
        Assert.Empty(jobs);
    }

    [Fact]
    public void ConcurrentPublishersStillQueueOneLatestFrame()
    {
        var jobs = new ConcurrentQueue<Action>();
        JsonNode? applied = null;
        var updates = new MeterUpdates(jobs.Enqueue, frame => applied = frame);
        Parallel.For(0, 1000, i => updates.Publish(JsonValue.Create(i)!));
        JsonNode last = JsonValue.Create(-1)!;
        updates.Publish(last);

        Assert.Single(jobs);
        Assert.True(jobs.TryDequeue(out Action? drain));
        drain();
        Assert.Same(last, applied);
        Assert.Empty(jobs);
    }
}
