using System.Text.Json.Nodes;
using OpenXLR.UI;
using OpenXLR.UI.Localization;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class MicSetupReliabilityTests : IDisposable
{
    public MicSetupReliabilityTests() => SliderSync.ReleaseTouchGuards();
    public void Dispose() => SliderSync.ReleaseTouchGuards();

    private static JsonNode UnitState()
    {
        JsonNode state = JsonNode.Parse(InputBindingModelTests.ProState(
            InputBindingModelTests.Xlr("xlr1", 0, InputBindingModelTests.Input(1))))!;
        state["capabilities"]!["gainMaxDb"] = 80;
        state["state"] = new JsonObject { ["gainDb"] = 40, ["gainLocked"] = false };
        return state;
    }

    private static void Readings(MicSetupViewModel setup, ManualTime time)
    {
        for (int i = 0; i < 75; i++)
        {
            time.Advance(TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 15 + 1));
            setup.ApplyInputLevel(new JsonArray(.5, .7, 0)); // -30 dB RMS, -18 dB peak.
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AGainChangeCannotBeUndoneToReuseAnOldMeasurement(bool finished)
    {
        await using var client = new DaemonClient("ws://127.0.0.1:1/ws");
        var main = new MainViewModel(client);
        CaptureInputWindowTests.Apply(main, UnitState());
        MicSetupViewModel setup = main.Inserts.MicSetup;
        var time = new ManualTime(); setup.Time = time;
        setup.StartGainMeasure();
        if (finished) Readings(setup, time);
        main.GainDb = 45;
        main.GainDb = 40;
        if (!finished) Readings(setup, time);
        setup.AcceptGain();
        Assert.Equal(40, main.GainDb);
        Assert.Null(setup.Suggestion);
        Assert.False(setup.Measuring || setup.CanAcceptGain);
        Assert.Equal(Localizer.Text("MicSetupSuggestStale"), setup.SuggestAdvice);
    }

    [Fact]
    public async Task AnotherUnitAtTheSameGainCannotReceiveThePreviousUnitsSuggestion()
    {
        await using var client = new DaemonClient("ws://127.0.0.1:1/ws");
        var main = new MainViewModel(client);
        JsonNode state = UnitState();
        CaptureInputWindowTests.Apply(main, state);
        MicSetupViewModel setup = main.Inserts.MicSetup;
        UnitInputViewModel input = setup.Unit!;
        var time = new ManualTime(); setup.Time = time;
        setup.StartGainMeasure(); Readings(setup, time);
        Assert.True(setup.CanAcceptGain);
        state["mixer"]!["channels"]![0]!["unitInput"]!["unit"] = "another-unit";
        CaptureInputWindowTests.Apply(main, state);
        Assert.Same(input, setup.Unit); // The primary input's view model is reused.
        setup.AcceptGain();
        Assert.Equal(40, main.GainDb);
        Assert.Null(setup.Suggestion);
        Assert.Equal(Localizer.Text("MicSetupSuggestStale"), setup.SuggestAdvice);
    }

    [Fact]
    public async Task RenamingTheChannelKeepsTheMeasurementButChangingTheGainRangeDoesNot()
    {
        await using var client = new DaemonClient("ws://127.0.0.1:1/ws");
        var main = new MainViewModel(client);
        JsonNode state = UnitState();
        CaptureInputWindowTests.Apply(main, state);
        MicSetupViewModel setup = main.Inserts.MicSetup;
        var time = new ManualTime(); setup.Time = time;
        setup.StartGainMeasure(); Readings(setup, time);
        GainSuggestion suggestion = setup.Suggestion!;
        state["mixer"]!["channels"]![0]!["name"] = "Renamed microphone";
        CaptureInputWindowTests.Apply(main, state);
        Assert.Same(suggestion, setup.Suggestion);
        Assert.True(setup.CanAcceptGain);
        state["capabilities"]!["gainMaxDb"] = 60;
        CaptureInputWindowTests.Apply(main, state);
        Assert.Null(setup.Suggestion);
        Assert.False(setup.CanAcceptGain);
        Assert.Equal(40, main.GainDb);
    }

    [Fact]
    public async Task AnAdditionalInterfacesGainChangesInvalidateItsMeasurement()
    {
        await using var client = new DaemonClient("ws://127.0.0.1:1/ws");
        var main = new MainViewModel(client);
        CaptureInputWindowTests.Apply(main, JsonNode.Parse(InputBindingWindowTests.DrivenState(withMix: false, captureCarried: true))!);
        MicSetupViewModel setup = main.Channels.Single(c => c.Id == "wave3mic").Inserts!.MicSetup;
        WaveUnitInputViewModel input = setup.ExtraUnit!;
        Assert.True(input.CanAdjustGain);
        int before = input.GainDb;
        var time = new ManualTime(); setup.Time = time;
        setup.StartGainMeasure(); Readings(setup, time);
        Assert.NotNull(setup.Suggestion);
        input.GainDb = before == 0 ? 1 : before - 1;
        input.GainDb = before;
        setup.AcceptGain();
        Assert.Equal(before, input.GainDb);
        Assert.Null(setup.Suggestion);
        Assert.Equal(Localizer.Text("MicSetupSuggestStale"), setup.SuggestAdvice);
    }

    [Fact]
    public async Task ARefusedStopKeepsTheMicrophoneAndTheErrorVisible()
    {
        await using var server = await SocketTestServer.Start(async (socket, stop) =>
        {
            while (!stop.IsCancellationRequested)
            {
                JsonNode command = await SocketTestServer.Receive(socket, stop);
                await SocketTestServer.Send(socket, new { type = "commandResult",
                    requestId = command["requestId"]!.GetValue<string>(), error = "The input could not be restored." }, stop);
            }
        });
        await using var client = new DaemonClient(server.Url);
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionChanged += up => { if (up) connected.TrySetResult(); };
        client.Start();
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var current = new MicSetupViewModel(client, "xlr1") { Step = MicSetupStep.Level };
        var next = new MicSetupViewModel(client, "guest");
        current.Apply(JsonNode.Parse("""{"channel":"xlr1","mode":"live","seconds":1}"""));
        Assert.False(await current.HandOverAsync(next));
        Assert.Equal(MicSetupStep.Position, next.Step);
        Assert.Equal("The input could not be restored.", current.Error);
        Assert.True(current.Active);
    }

    [Theory]
    [InlineData("remove")]
    [InlineData("replace")]
    [InlineData("rename")]
    public async Task ChangingThePickerDuringStopOnlyAcceptsTheSameInputInstance(string change)
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await SocketTestServer.Start(async (socket, cancellation) =>
        {
            JsonNode command = await SocketTestServer.Receive(socket, cancellation);
            received.TrySetResult();
            await release.Task.WaitAsync(cancellation);
            await SocketTestServer.Send(socket, new { type = "commandResult",
                requestId = command["requestId"]!.GetValue<string>() }, cancellation);
            await Task.Delay(Timeout.Infinite, cancellation);
        });
        await using var client = new DaemonClient(server.Url);
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionChanged += up => { if (up) connected.TrySetResult(); };
        client.Start();
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var inputs = new MicSetupInputs();
        var current = new MicSetupViewModel(client, "xlr1") { Inputs = inputs, Step = MicSetupStep.Level };
        var next = new MicSetupViewModel(client, "guest") { Inputs = inputs };
        inputs.Update([new("Mic", current), new("Guest", next)]);
        current.Apply(JsonNode.Parse("""{"channel":"xlr1","mode":"live","seconds":1}"""));
        Task<bool> handover = current.HandOverAsync(next);
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        inputs.Update(change switch
        {
            "remove" => [new("Mic", current)],
            "replace" => [new("Mic", current), new("Guest", new MicSetupViewModel(client, "guest") { Inputs = inputs })],
            _ => [new("Mic", current), new("Renamed guest", next)],
        });
        release.TrySetResult();
        Assert.Equal(change == "rename", await handover.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(change == "rename" ? MicSetupStep.Level : MicSetupStep.Position, next.Step);
    }

    [Fact]
    public async Task ARetiredCaptureSetupReleasesTheSharedGainAndEqModels()
    {
        await using var client = new DaemonClient("ws://127.0.0.1:1/ws");
        var main = new MainViewModel(client);
        var chain = new InsertsViewModel(client, "guest", 1);
        MicSetupViewModel setup = chain.MicSetup;
        setup.Unit = main.UnitInput1;
        using var eq = new MicEqViewModel(client, "guest", "Guest");
        setup.Eq = eq;
        chain.Detach();
        Assert.False(setup.CarriesUnit);
        Assert.Null(setup.Eq);
    }

    [Fact]
    public async Task ALateWaveformCannotReplaceANewerTakeOrOneAlreadyRequested()
    {
        await using var client = new DaemonClient("ws://127.0.0.1:1/ws");
        var setup = new MicSetupViewModel(client, "xlr1");
        JsonNode State(int take) => JsonNode.Parse($$"""{"channel":"xlr1","mode":"live","seconds":1,"take":{{take}}}""")!;
        JsonNode Take(int take, double peak) => new JsonObject { ["channel"] = "xlr1", ["take"] = take, ["peaks"] = new JsonArray(peak) };
        setup.Apply(State(1)); setup.ApplyTake(Take(1, .1));
        IReadOnlyList<double> first = setup.Peaks!;
        setup.ApplyTake(Take(1, .9));
        Assert.Same(first, setup.Peaks); // A duplicate does not allocate or replace an immutable waveform.
        setup.Apply(State(2)); setup.ApplyTake(Take(1, .2));
        Assert.Equal([.1], setup.Peaks!);
        setup.ApplyTake(Take(2, .8)); setup.ApplyTake(Take(1, .3));
        Assert.Equal([.8], setup.Peaks!);
        setup.Reset();
        setup.Apply(State(3)); setup.Apply(State(2));
        setup.ApplyTake(Take(2, .2));
        Assert.False(setup.HasTake);
        setup.ApplyTake(Take(3, .7));
        Assert.Equal([.7], setup.Peaks!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AReplyFromBeforeStopCannotUnlockItOrClearItsError(bool stopFirst)
    {
        var received = new[] { new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var release = new[] { new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var server = await SocketTestServer.Start(async (socket, cancellation) =>
        {
            var replies = new List<Task>();
            for (int i = 0; i < 2; i++)
            {
                JsonNode command = await SocketTestServer.Receive(socket, cancellation);
                int index = i;
                replies.Add(Reply());
                received[index].TrySetResult();
                async Task Reply()
                {
                    await release[index].Task.WaitAsync(cancellation);
                    await SocketTestServer.Send(socket, new { type = "commandResult", requestId = command["requestId"]!.GetValue<string>(),
                        error = index == 1 ? "stop failure" : null }, cancellation);
                }
            }
            await Task.WhenAll(replies);
            await Task.Delay(Timeout.Infinite, cancellation);
        });
        await using var client = new DaemonClient(server.Url);
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionChanged += up => { if (up) connected.TrySetResult(); };
        client.Start();
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var setup = new MicSetupViewModel(client, "xlr1");
        Task record = setup.RunAsync("record");
        await received[0].Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task stop = setup.StopOnCloseAsync();
        await received[1].Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (stopFirst)
        {
            release[1].TrySetResult(); await stop.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("stop failure", setup.Error);
        }
        release[0].TrySetResult(); await record.WaitAsync(TimeSpan.FromSeconds(5));
        if (stopFirst) Assert.Equal("stop failure", setup.Error);
        else
        {
            Assert.False(setup.CanRecord);
            release[1].TrySetResult(); await stop.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("stop failure", setup.Error);
        }
    }
}
