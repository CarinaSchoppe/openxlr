using System.Reflection;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenXLR.UI;
using OpenXLR.UI.Localization;
using OpenXLR.UI.Skinning;
using static OpenXLR.Tests.WindowLayoutTests;

namespace OpenXLR.Tests;

internal static class MixerPopOutWindowTests
{
    internal static void Check()
    {
        string dir = Directory.CreateTempSubdirectory("openxlr-popout-").FullName;
        string? config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        SkinEntry skin = SkinService.Current;
        bool touch = SkinService.TouchControls;
        MainWindow? main = null;
        try
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", dir);
            main = new MainWindow(new DaemonClient("ws://127.0.0.1:1/ws"));
            var vm = (MainViewModel)main.DataContext!;
            typeof(MainViewModel).GetMethod("ApplyMixer", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(vm, [JsonNode.Parse("""
                {"mixes":[{"id":"monitor","name":"Monitor A with a long name","kind":"monitor"},
                          {"id":"monitor2","name":"Monitor B","kind":"monitor"}],
                 "channels":[{"id":"music","name":"Music with an especially long channel name","levels":{"monitor":0.5,"monitor2":0.7}},
                             {"id":"game","name":"Game","levels":{"monitor":1,"monitor2":1}}]}
                """)]);
            main.Show();
            Layout(main, 1040, 900);
            var host = main.FindControl<ContentControl>("MixerHost")!;
            var panel = (Control)host.Content!;
            InsertWindows.OpenChain(main, vm.Mixes[0].Inserts, "mix:monitor");
            var originalChain = Assert.Single(main.OwnedWindows.OfType<MixInsertsWindow>());
            var insert = new InsertViewModel(vm.Mixes[0].Inserts, "popout-test", "urn:openxlr:test", "Test effect");
            InsertWindows.OpenControls(main, insert);
            var originalControls = Assert.Single(main.OwnedWindows.OfType<InsertControlsWindow>());
            MixerWindow popped = main.PopOutMixer();
            Layout(popped, 800, 480);
            Assert.Same(popped, main.PopOutMixer());
            Assert.Null(host.Content);
            Assert.Same(vm, popped.DataContext);
            Assert.Same(panel, popped.FindControl<ScrollViewer>("MixerScroll")!.Content);
            Slider master = Master(popped, vm);

            main.Hide();
            Assert.True(popped.IsVisible);
            vm.Mixes[0].ApplyFromDaemon(JsonNode.Parse("""{"volume":0.8,"kind":"monitor"}""")!);
            Layout(popped, 800, 480);
            Assert.Equal(0.8, master.Value);
            master.Value = 0.42;
            Assert.Equal(0.42, vm.Mixes[0].Volume);
            // A chain opened from the detached panel belongs to its visible window.
            popped.GetVisualDescendants().OfType<Button>().Single(b =>
                b.DataContext == vm.Mixes[0] && Equals(b.Content, vm.Mixes[0].Inserts.ButtonText))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var chain = Assert.Single(popped.OwnedWindows.OfType<MixInsertsWindow>());
            Assert.NotSame(originalChain, chain);
            Assert.False(originalChain.IsVisible);
            Assert.Same(vm.Mixes[0].Inserts, chain.DataContext);
            chain.Close();
            InsertWindows.OpenControls(popped, insert);
            var controls = Assert.Single(popped.OwnedWindows.OfType<InsertControlsWindow>());
            Assert.False(originalControls.IsVisible);
            Assert.Same(insert, controls.DataContext);
            controls.Hide();
            InsertWindows.OpenControls(popped, insert);
            var restored = Assert.Single(popped.OwnedWindows.OfType<InsertControlsWindow>());
            Assert.True(restored.IsVisible);
            Assert.Same(insert, restored.DataContext);
            restored.Close();
            Capture(popped, "popout-800");

            foreach (string id in new[] { SkinPackage.DefaultId, "opendeck" })
            {
                SkinService.Apply(SkinCatalog.Find(id)!);
                SkinService.ApplyControlSizing(true);
                Layout(popped, 480, 320);
                Assert.Contains("large-targets", popped.Classes);
                AssertInside(popped.FindControl<ScrollViewer>("MixerScroll")!, popped);
                Assert.True(popped.FindControl<ScrollViewer>("MixerScroll")!.Extent.Height
                    > popped.FindControl<ScrollViewer>("MixerScroll")!.Viewport.Height);
                foreach (var row in popped.GetVisualDescendants().OfType<Grid>()
                    .Where(g => g.DataContext is SendViewModel && g.Children.OfType<TextBlock>().Count() == 2))
                    AssertNoOverlap(row.Children.ToArray());
                Capture(popped, "popout-480-" + id);
            }

            vm.ChooseMixerView(MainViewModel.MiniView);
            Layout(popped, 320, 300);
            Assert.True(main.FindControl<ComboBox>("CompactChannelPicker")!.IsEffectivelyVisible);
            Capture(popped, "popout-mini-320");
            popped.Close();
            Dispatcher.UIThread.RunJobs();
            Assert.True(main.IsVisible);
            Assert.Same(panel, host.Content);
            Layout(main, 1040, 900);
            Assert.Equal(0.42, Master(main, vm).Value);

            // Reopening moves the same controls; quitting closes the separate window too.
            popped = main.PopOutMixer();
            Layout(popped, 480, 320);
            Assert.Equal(0.42, Master(popped, vm).Value);
            main.Quit();
            Assert.False(popped.IsVisible);
            Assert.False(main.IsVisible);

            CheckRecreatedMix();
            MixerFaderLifecycleWindowTests.Check();
        }
        finally
        {
            main?.Quit();
            SkinService.ApplyControlSizing(touch);
            SkinService.Apply(skin);
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", config);
            Directory.Delete(dir, true);
        }
    }

    private static Slider Master(Window window, MainViewModel vm) => window.GetVisualDescendants().OfType<Slider>()
        .Single(s => s.DataContext == vm.Mixes[0]);

    private static void CheckRecreatedMix()
    {
        var client = new DaemonClient("ws://127.0.0.1:1/ws");
        var owner = new Window();
        try
        {
            owner.Show();
            var vm = new MainViewModel(client);
            var apply = typeof(MainViewModel).GetMethod("ApplyMixer", BindingFlags.NonPublic | BindingFlags.Instance)!;
            const string state = """{"mixes":[{"id":"headset","name":"Headset","kind":"monitor"}],"channels":[]}""";
            apply.Invoke(vm, [JsonNode.Parse(state)]);
            InsertWindows.OpenChain(owner, vm.Mixes[0].Inserts, "mix:headset");
            var old = Assert.Single(owner.OwnedWindows.OfType<MixInsertsWindow>());
            apply.Invoke(vm, [JsonNode.Parse("""{"mixes":[{"id":"headset","name":"Speakers","kind":"monitor"}],"channels":[]}""")]);
            Dispatcher.UIThread.RunJobs();
            string title = Localizer.Format("MixInsertsTitle", "Speakers");
            Assert.Equal(title, vm.Mixes[0].Inserts.Title);
            Assert.Equal(Localizer.Format("InsertsTitle", title), old.Title);
            var insert = new InsertViewModel(vm.Mixes[0].Inserts, "old-effect", "urn:openxlr:test", "Old effect");
            vm.Mixes[0].Inserts.Items.Add(insert);
            InsertWindows.OpenControls(owner, insert);
            var oldControls = Assert.Single(owner.OwnedWindows.OfType<InsertControlsWindow>());
            // Deleting and re-creating a name reuses its id, but not its live chain.
            apply.Invoke(vm, [JsonNode.Parse("""{"mixes":[],"channels":[]}""")]);
            Assert.False(old.IsVisible);
            Assert.False(oldControls.IsVisible);
            apply.Invoke(vm, [JsonNode.Parse(state)]);
            InsertWindows.OpenChain(owner, vm.Mixes[0].Inserts, "mix:headset");
            var current = Assert.Single(owner.OwnedWindows.OfType<MixInsertsWindow>());
            Assert.Same(vm.Mixes[0].Inserts, current.DataContext);
            Assert.False(old.IsVisible);
            InsertWindows.OpenChain(owner, vm.Mixes[0].Inserts, "mix:headset");
            Assert.Same(current, Assert.Single(owner.OwnedWindows.OfType<MixInsertsWindow>()));
        }
        finally { owner.Close(); client.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }
}
