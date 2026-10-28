using OpenXLR.UI;

namespace OpenXLR.Tests;

[Collection("xdg-config")]
public sealed class X11FocusTests
{
    [Fact]
    public async Task ReadsFreshLocalProcessesAndRefusesAFocusChangeBetweenProperties()
    {
        string directory = Directory.CreateTempSubdirectory("ox-x11-").FullName;
        string? before = Environment.GetEnvironmentVariable("PATH");
        try
        {
            ExecutableScript.Write(Path.Combine(directory, "xprop"), """
                dir=${0%/*}
                printf '%s\n' "$*" >> "$dir/arguments"
                case "$3" in
                  -root)
                    if [ -e "$dir/change" ] && [ -e "$dir/queried" ]; then
                      echo '_NET_ACTIVE_WINDOW(WINDOW) = 0x43'
                    else echo '_NET_ACTIVE_WINDOW(WINDOW) = 0x42'; fi
                    /usr/bin/touch "$dir/queried"
                    ;;
                  -id) /bin/cat "$dir/process" ;;
                  *) exit 1 ;;
                esac
                """);
            Environment.SetEnvironmentVariable("PATH", directory + ":" + before);
            void Reply(int pid, string? machine = null) => File.WriteAllText(Path.Combine(directory, "process"),
                $"_NET_WM_PID(CARDINAL) = {pid}\nWM_CLIENT_MACHINE(STRING) = \"{machine ?? Environment.MachineName}\"\n");
            string? Env(string key) => key == "DISPLAY" ? ":42" : null;
            Reply(42);
            Assert.Equal(42u, await X11Focus.ReadAsync(CancellationToken.None, Env));
            Reply(73);
            Assert.Equal(73u, await X11Focus.ReadAsync(CancellationToken.None, Env));
            Assert.Contains("-display :42 -id 0x42 _NET_WM_PID WM_CLIENT_MACHINE", File.ReadAllLines(Path.Combine(directory, "arguments")));
            Reply(73, "different-machine");
            await Assert.ThrowsAsync<InvalidOperationException>(() => X11Focus.ReadAsync(CancellationToken.None, Env));
            Reply(73);
            File.Delete(Path.Combine(directory, "queried"));
            File.WriteAllText(Path.Combine(directory, "change"), "");
            var changed = await Assert.ThrowsAsync<InvalidOperationException>(() => X11Focus.ReadAsync(CancellationToken.None, Env));
            Assert.Contains("changed", changed.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", before);
            Directory.Delete(directory, true);
        }
    }
}
