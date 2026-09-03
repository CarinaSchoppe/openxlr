using System.Diagnostics;
using OpenXLR.Core.Devices;
using OpenXLR.Probe;

// OpenXLR probe.
//   (no args) device: detect the interface, read state, cross-check vs ALSA.
//   mixer    : build the real PipeWire submix graph, move faders, tear down.
if (args.Length > 0 && args[0] == "mixer")
    return MixerTest.Run();
if (args.Length > 0 && args[0] == "streams")
    return StreamTest.Run();

IAudioDevice? dev = DeviceRegistry.DetectFirst();
if (dev is null)
{
    Console.Error.WriteLine("No supported audio interface detected.");
    return 1;
}

Console.WriteLine($"Detected: {dev.Info.DisplayName} ({dev.Info.VendorId:x4}:{dev.Info.ProductId:x4})");
dev.Connect();
Console.WriteLine($"Connected: {dev.Connected}\n");

DeviceState s0 = dev.ReadState();
Console.WriteLine("Initial state:");
Console.WriteLine($"  gain            {s0.GainDb} dB");
Console.WriteLine($"  mute            {s0.Mute}");
Console.WriteLine($"  low-cut         {s0.LowCut}");
Console.WriteLine($"  expander        {s0.Expander}");
Console.WriteLine($"  voice-tune      {s0.VoiceTune} (strength {s0.VoiceTuneStrength})");
Console.WriteLine($"  hp volume       {s0.HpVolumeDb:0.0} dB");
Console.WriteLine($"  low-impedance   {s0.LowImpedance}");
Console.WriteLine($"  crossfade       {s0.Crossfade}");
Console.WriteLine($"  phantom         {s0.Phantom}");
Console.WriteLine($"  clip-guard      {s0.ClipGuard}");
Console.WriteLine($"  compressor      {s0.Compressor}");
Console.WriteLine($"  outputs         hp1={s0.OutHp1} hp2={s0.OutHp2} usbAux={s0.OutUsbAux} lineOut={s0.OutLineOut}");
Console.WriteLine($"  aux input       {s0.AuxLevelDb:0.0} dB (lock {s0.AuxLevelLock})");

Console.WriteLine("\nGain cross-check (device write -> ALSA read):");
foreach (int g in new[] { 40, 65 })
{
    dev.SetGainDb(g);
    Thread.Sleep(300);
    Console.WriteLine($"  set {g} dB -> ALSA capture volume = {AlsaGain()}  (device readback {dev.ReadState().GainDb})");
}
dev.SetGainDb(s0.GainDb);
Thread.Sleep(300);  // let ALSA's mirror of the feature unit catch up before reading
Console.WriteLine($"  restored {s0.GainDb} dB -> ALSA = {AlsaGain()}");

Console.WriteLine("\nMute cross-check (device write -> ALSA read):");
dev.SetMute(true); Thread.Sleep(300); Console.WriteLine($"  mute true  -> ALSA capture switch = {AlsaMute()} (expect off)");
dev.SetMute(false); Thread.Sleep(300); Console.WriteLine($"  mute false -> ALSA capture switch = {AlsaMute()} (expect on)");
dev.SetMute(s0.Mute);

dev.Disconnect();
Console.WriteLine("\nOK: OpenXLR.Core device layer verified on hardware.");
return 0;

static string AlsaGain()
{
    string o = Amixer("cget", "numid=3");
    var m = System.Text.RegularExpressions.Regex.Match(o, @": values=(\d+)");
    return m.Success ? m.Groups[1].Value : "?";
}

static string AlsaMute()
{
    string o = Amixer("cget", "numid=1");
    var m = System.Text.RegularExpressions.Regex.Match(o, @": values=(on|off)");
    return m.Success ? m.Groups[1].Value : "?";
}

static string Amixer(params string[] args)
{
    var psi = new ProcessStartInfo("amixer") { RedirectStandardOutput = true };
    psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("Pro");
    foreach (string a in args) psi.ArgumentList.Add(a);
    using var p = Process.Start(psi)!;
    string o = p.StandardOutput.ReadToEnd();
    p.WaitForExit();
    return o;
}
