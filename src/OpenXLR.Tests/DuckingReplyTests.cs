using OpenXLR.UI;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class DuckingReplyTests
{
    private sealed class Deferred : IDuckingCommands
    {
        public List<TaskCompletionSource<string?>> Replies { get; } = [];
        public bool Throw;
        public Task<string?> SetDuckingAsync(IReadOnlyDictionary<string, object> fields) => Next();
        public Task<string?> SetDuckingProcessingAsync(string value) => Next();
        private Task<string?> Next()
        {
            if (Throw) throw new InvalidOperationException("connection lost");
            var reply = new TaskCompletionSource<string?>();
            Replies.Add(reply);
            return reply.Task;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ARepeatedValueDoesNotLetAnOldRefusalUndoTheNewestEdit(bool processing)
    {
        var commands = new Deferred();
        var model = new DuckingViewModel(commands, new ManualTime());
        model.Apply(DuckingViewModelTests.Ducking(device: DuckingViewModelTests.Pro()), DuckingViewModelTests.Mixer());
        if (processing)
        {
            model.ChooseProcessing("software");
            model.ChooseProcessing("device");
            model.ChooseProcessing("software");
        }
        else { model.Amount = 0; model.Amount = 1; model.Amount = 0; }
        Assert.Equal(3, commands.Replies.Count);
        commands.Replies[0].SetResult("old refusal");
        if (processing) Assert.Equal("software", model.Processing);
        else Assert.Equal(0, model.Amount);
        Assert.Null(model.Refusal);
        commands.Replies[1].SetResult(null);
        commands.Replies[2].SetResult(null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnOlderRefusalCannotReplaceANewerOutcome(bool refused)
    {
        var commands = new Deferred();
        var model = new DuckingViewModel(commands, new ManualTime());
        model.Apply(DuckingViewModelTests.Ducking(), DuckingViewModelTests.Mixer());
        model.Amount = 0;
        model.Amount = 1;
        commands.Replies[1].SetResult(refused ? "new refusal" : null);
        if (!refused)
        {
            var report = DuckingViewModelTests.Ducking();
            report["amount"] = "medium";
            model.Apply(report, DuckingViewModelTests.Mixer());
        }
        commands.Replies[0].SetResult("old refusal");
        Assert.Equal(refused ? "new refusal" : null, model.Refusal);
        Assert.Equal(refused ? 2 : 1, model.Amount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LosingTheMixerInvalidatesRepliesBeforeTheSameValueIsEditedAgain(bool unreported)
    {
        var commands = new Deferred();
        var time = new ManualTime();
        var model = new DuckingViewModel(commands, time);
        model.Apply(DuckingViewModelTests.Ducking(), DuckingViewModelTests.Mixer());
        model.Enabled = true;
        if (unreported) model.ResetForNewConnection();
        else model.Apply(DuckingViewModelTests.Ducking(), null);
        Assert.False(model.CanEdit);
        time.Advance(DuckingViewModel.EchoWait);
        model.Apply(DuckingViewModelTests.Ducking(), DuckingViewModelTests.Mixer());
        model.Enabled = true;
        Assert.Equal(2, commands.Replies.Count);
        commands.Replies[0].SetResult("old connection");
        Assert.True(model.Enabled);
        Assert.Null(model.Refusal);
        commands.Replies[1].SetResult(null);
    }

    [Fact]
    public void APartlySupersededCommandStillReportsItsUnchangedField()
    {
        var commands = new Deferred();
        var model = new DuckingViewModel(commands, new ManualTime());
        model.Apply(DuckingViewModelTests.Ducking(channels: "\"xlr2\",\"music\""), DuckingViewModelTests.Mixer());
        model.SelectedKey = model.Keys.Single(choice => choice.Id == "xlr2");
        model.Channels.Single(choice => choice.Id == "game").IsChecked = true;
        commands.Replies[0].SetResult("key refused");
        Assert.Equal("key refused", model.Refusal);
        Assert.Equal("xlr1", model.SelectedKey!.Id);
        Assert.True(model.Channels.Single(choice => choice.Id == "game").IsChecked);
        commands.Replies[1].SetResult(null);
    }

    [Fact]
    public void ACommandThatThrowsBeforeReturningATaskIsARefusal()
    {
        var commands = new Deferred { Throw = true };
        var model = new DuckingViewModel(commands, new ManualTime());
        model.Apply(DuckingViewModelTests.Ducking(), DuckingViewModelTests.Mixer());
        Assert.Null(Record.Exception(() => model.Amount = 0));
        Assert.Equal("connection lost", model.Refusal);
        Assert.Equal(2, model.Amount);
    }
}
