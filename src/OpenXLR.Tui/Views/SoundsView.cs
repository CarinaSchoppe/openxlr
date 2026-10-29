namespace OpenXLR.Tui;

/// <summary>
/// The soundboard: the clips the daemon's player holds, played into the SFX
/// channel. One row a clip, in library order, with its length, press mode,
/// loop, gain and status, and a bar for how far a playing clip is. The
/// board's switch and SFX's own volume, the board's master, head the list;
/// the daemon's sentence says why the board or a clip cannot play.
///
/// A terminal sends no key-up, so a clip in hold mode is let go by pressing
/// it again: the first press holds it, the next one releases it.
/// </summary>
internal sealed class SoundsView : View
{
    private const double GainStep = 0.05;
    private const double FineGainStep = 0.01;

    // The selection is a clip id, so it follows the clip through a reorder;
    // the index picks a neighbour when the selected clip is deleted.
    private string? _selected;
    private int _lastIndex;
    private int _scroll;

    // Hold clips this terminal pressed and has not let go. The daemon ties a
    // hold to the connection, so a new connection holds nothing.
    private readonly HashSet<string> _held = new(StringComparer.Ordinal);
    private long _session = -1;

    private Task<string?>? _importing;
    private string? _importNote;
    private bool _importFailed;

    public override string Title => "Sounds";

    public override string Keys =>
        "Enter play  s stop  S stop all  i import  b board  -/+ [/] gain  m mode  l loop  r rename  d delete  Ctrl+Up/Down move  </> ,/. SFX volume";

    public override void Draw(Screen screen, Rect area, App app)
    {
        Theme theme = app.Theme;
        Snapshot? state = State(app);
        if (state is null)
        {
            screen.Text(area.X + 2, area.Y + 1, "Waiting for the daemon", theme.TextMuted, theme.Card);
            return;
        }
        SoundboardEntry? board = state.Mixer.Soundboard;
        if (board is null)
        {
            screen.Text(area.X + 2, area.Y + 1, "This daemon has no soundboard. Update OpenXLR to use it.",
                theme.TextMuted, theme.Card, maxWidth: area.Width - 4);
            return;
        }
        Update(app, board);
        if (area.Height < 6 || area.Width < 40) return;

        Header(screen, area, app, state, board);

        List<ClipEntry> clips = board.Clips;
        int listY = area.Y + 3;
        int listHeight = Math.Max(1, area.Height - 4);
        if (clips.Count == 0)
        {
            screen.Text(area.X + 2, listY, "No clips yet. Press i and type a file's path to import one.",
                theme.TextMuted, theme.Card, maxWidth: area.Width - 4);
        }
        else
        {
            Columns(screen, area, theme, area.Y + 2);
            int index = Index(clips);
            if (index < _scroll) _scroll = index;
            if (index >= _scroll + listHeight) _scroll = index - listHeight + 1;
            _scroll = Math.Clamp(_scroll, 0, Math.Max(0, clips.Count - listHeight));
            for (int line = 0; line < listHeight && _scroll + line < clips.Count; line++)
            {
                int at = _scroll + line;
                ClipRow(screen, new Rect(area.X, listY + line, area.Width, 1), app, state, clips[at], at == index);
            }
            if (_scroll > 0) screen.Set(area.Right - 1, listY, '▲', theme.TextMuted, theme.Card);
            if (_scroll + listHeight < clips.Count) screen.Set(area.Right - 1, listY + listHeight - 1, '▼', theme.TextMuted, theme.Card);
        }

        Detail(screen, area, theme, board);
    }

    /// <summary>Settles the selection, drops holds a closed connection ended, and takes an import's answer.</summary>
    private void Update(App app, SoundboardEntry board)
    {
        if (app.Link.SessionRevision != _session)
        {
            _session = app.Link.SessionRevision;
            _held.Clear();
        }
        _held.RemoveWhere(id => !board.Clips.Any(clip => clip.Id == id));
        if (_selected is null || !board.Clips.Any(clip => clip.Id == _selected))
            _selected = board.Clips.Count == 0 ? null : board.Clips[Math.Clamp(_lastIndex, 0, board.Clips.Count - 1)].Id;

        if (_importing is { IsCompleted: true } done)
        {
            _importing = null;
            string? error = done.IsCompletedSuccessfully ? done.Result : "The import did not finish";
            _importFailed = error is { Length: > 0 };
            if (_importFailed)
            {
                _importNote = error;
                app.Say(error!);
            }
            else
            {
                // The daemon adds an imported clip at the end of the library.
                if (board.Clips.Count > 0) _selected = board.Clips[^1].Id;
                _importNote = _selected is not null ? $"Imported {board.Clips.First(clip => clip.Id == _selected).Name}" : "Imported";
                app.Say(_importNote);
            }
        }
    }

    private int Index(List<ClipEntry> clips)
    {
        int index = Math.Max(0, clips.FindIndex(clip => clip.Id == _selected));
        _lastIndex = index;
        return index;
    }

    private static void Header(Screen screen, Rect area, App app, Snapshot state, SoundboardEntry board)
    {
        Theme theme = app.Theme;
        int x = area.X + 1;
        int y = area.Y;
        bool plays = board.Error is null;
        Widgets.Lamp(screen, x, y, board.Enabled && plays, theme, theme.Card, alert: board.Enabled && !plays);
        x += 2;
        x += screen.Text(x, y, board.Enabled ? "Board on " : "Board off", theme.TextPrimary, theme.Card, bold: true);
        x += 3;

        ChannelEntry? sfx = Sfx(state, board);
        if (sfx is not null)
        {
            string name = sfx.Name.Length > 10 ? sfx.Name[..10] : sfx.Name;
            x += screen.Text(x, y, name, sfx.Appearance.Accent ?? theme.TextDetail, theme.Card);
            x += 1;
            if (sfx.Volume is double volume)
            {
                Widgets.Fader(screen, x, y, 12, volume, 1, theme, theme.Card, false);
                x += 13;
                x += screen.Text(x, y, Widgets.Percent(volume).PadLeft(4), theme.TextDetail, theme.Card);
            }
        }
        else
        {
            x += screen.Text(x, y, "no SFX channel", theme.TextWarning, theme.Card);
        }

        int playing = board.Clips.Count(clip => clip.Playing);
        string count = $"{board.Clips.Count} {(board.Clips.Count == 1 ? "clip" : "clips")}"
            + (playing > 0 ? $", {playing} playing" : string.Empty);
        int countX = Math.Max(x + 3, area.Right - count.Length - 1);
        screen.Text(countX, y, count, theme.TextSecondary, theme.Card, maxWidth: Math.Max(0, area.Right - countX));

        // The daemon's sentence when the board cannot play; what it plays when it can.
        string line = board.Error ?? (board.Formats.Count > 0
            ? $"Plays {Join(board.Formats)} files of up to 30 s into {(sfx?.Name ?? "SFX")}"
            : "Ready");
        screen.TextPad(area.X + 1, y + 1, line, area.Width - 2, board.Error is null ? theme.TextMuted : theme.TextWarning, theme.Card);
    }

    private static string Join(List<string> formats) =>
        formats.Count == 1 ? formats[0] : $"{string.Join(", ", formats.Take(formats.Count - 1))} and {formats[^1]}";

    /// <summary>The widths a clip row splits into: the name takes what the fixed columns leave.</summary>
    private static (int Name, int Status) Widths(int width)
    {
        // Marker 4, length 7, mode 9, loop 6, gain 6, the gaps between.
        int fixedCells = 4 + 1 + 7 + 9 + 6 + 6;
        int name = Math.Clamp((width - fixedCells) * 2 / 5, 10, 28);
        int status = Math.Max(8, width - fixedCells - name - 1);
        return (name, status);
    }

    private static void Columns(Screen screen, Rect area, Theme theme, int y)
    {
        (int name, _) = Widths(area.Width);
        int x = area.X + 4;
        screen.Text(x, y, "CLIP", theme.TextMuted, theme.Card, bold: true);
        x += name + 1;
        screen.Text(x, y, " LENGTH", theme.TextMuted, theme.Card, bold: true);
        x += 7;
        screen.Text(x + 1, y, "MODE", theme.TextMuted, theme.Card, bold: true);
        x += 9;
        screen.Text(x + 1, y, "LOOP", theme.TextMuted, theme.Card, bold: true);
        x += 6;
        screen.Text(x + 1, y, "GAIN", theme.TextMuted, theme.Card, bold: true);
        x += 6;
        screen.Text(x + 1, y, "STATUS", theme.TextMuted, theme.Card, bold: true);
    }

    private void ClipRow(Screen screen, Rect row, App app, Snapshot state, ClipEntry clip, bool focused)
    {
        Theme theme = app.Theme;
        Rgb back = focused ? theme.Selection : theme.Card;
        screen.Fill(row.X, row.Y, row.Width, 1, back);
        if (focused) screen.Set(row.X, row.Y, '┃', theme.Accent, back);
        (int nameWidth, int statusWidth) = Widths(row.Width);

        (double Position, int Voices)? progress = clip.Playing ? app.Link.ClipProgress(clip.Id) : null;
        if (clip.Playing) screen.Set(row.X + 2, row.Y, '▶', theme.LedOn, back);

        int x = row.X + 4;
        string icon = LayoutIcons.TextFor(clip.Icon, Sfx(state, state.Mixer.Soundboard!)?.Appearance.DefaultIcon ?? string.Empty);
        string label = icon.Length > 0 ? $"{icon} {clip.Name}" : clip.Name;
        Rgb nameFore = Rgb.TryParse(clip.Colour) ?? (focused ? theme.TextPrimary : theme.TextDetail);
        screen.Text(x, row.Y, label, nameFore, back, bold: focused, maxWidth: nameWidth);
        x += nameWidth + 1;
        screen.Text(x, row.Y, $"{clip.Seconds:0.0} s".PadLeft(7), theme.TextDetail, back);
        x += 7;
        screen.Text(x + 1, row.Y, clip.Mode, clip.Hold ? theme.TextInfo : theme.TextDetail, back, maxWidth: 8);
        x += 9;
        screen.Text(x + 1, row.Y, clip.Loop ? "loop" : "-", clip.Loop ? theme.TextInfo : theme.TextMuted, back);
        x += 6;
        screen.Text(x + 1, row.Y, Widgets.Percent(clip.Gain).PadLeft(4), theme.TextDetail, back);
        x += 6;

        int statusX = x + 1;
        int room = Math.Min(statusWidth, row.Right - statusX - 1);
        if (room <= 0) return;
        if (clip.Playing)
        {
            string tail = (progress is { Voices: > 1 } many ? $" x{many.Voices}" : string.Empty)
                + (_held.Contains(clip.Id) ? " held" : string.Empty);
            int bar = Math.Max(4, room - tail.Length - 1);
            Progress(screen, statusX, row.Y, bar, progress?.Position ?? 0, theme, back);
            screen.Text(statusX + bar, row.Y, tail, theme.TextInfo, back, maxWidth: Math.Max(0, room - bar));
            return;
        }
        (string word, Rgb fore) = clip.Status switch
        {
            "ready" => ("ready", theme.TextSecondary),
            "loading" => ("loading", theme.TextInfo),
            "error" => (clip.Error is { Length: > 0 } error ? $"error: {error}" : "error", theme.TextWarning),
            _ => ("off", theme.TextMuted),
        };
        screen.Text(statusX, row.Y, word, fore, back, maxWidth: room);
    }

    /// <summary>How far a clip is: a solid bar on the meter's track, in half cells.</summary>
    private static void Progress(Screen screen, int x, int y, int width, double position, Theme theme, Rgb back)
    {
        double filled = Math.Clamp(position, 0, 1) * width;
        for (int cell = 0; cell < width; cell++)
        {
            double within = filled - cell;
            char ch = within >= 1 ? '█' : within >= 0.5 ? '▌' : '─';
            screen.Set(x + cell, y, ch, within >= 0.5 ? theme.MeterFill : theme.MeterTrack, back);
        }
    }

    /// <summary>The line under the list: the selected clip's reason, the hold rule, or the last import.</summary>
    private void Detail(Screen screen, Rect area, Theme theme, SoundboardEntry board)
    {
        int y = area.Bottom - 1;
        ClipEntry? clip = board.Clips.FirstOrDefault(entry => entry.Id == _selected);
        (string text, Rgb fore) =
            _importing is not null ? (_importNote ?? "Importing", theme.TextInfo)
            : clip is { Status: "error", Error.Length: > 0 } ? (clip.Error!, theme.TextWarning)
            : _importNote is { Length: > 0 } note && _importFailed ? (note, theme.TextWarning)
            : clip is { Hold: true } ? (_held.Contains(clip.Id) && clip.Playing
                ? "Held: press Enter or Space again to let it go"
                : "Hold: the first press plays it, the next press lets it go", theme.TextInfo)
            : _importNote is { Length: > 0 } done ? (done, theme.TextSecondary)
            : (string.Empty, theme.TextMuted);
        screen.TextPad(area.X + 1, y, text, area.Width - 2, fore, theme.Card);
    }

    private static ChannelEntry? Sfx(Snapshot state, SoundboardEntry board) =>
        board.Channel is { } id ? state.Mixer.Channels.FirstOrDefault(channel => channel.Id == id) : null;

    public override bool Handle(KeyPress key, App app)
    {
        Snapshot? state = State(app);
        if (state?.Mixer.Soundboard is not { } board) return false;
        Update(app, board);
        DaemonLink link = app.Link;

        if (key.Key == Key.Char && !key.Ctrl)
        {
            switch (key.Char)
            {
                case 'b':
                    link.Send("setSoundboard", body => body["enabled"] = !board.Enabled);
                    return true;
                case 'S':
                    link.Send("stopClips");
                    LetGoAll(link);
                    return true;
                case 'i':
                    Import(app);
                    return true;
                case '>': Master(app, state, board, 0.05); return true;
                case '<': Master(app, state, board, -0.05); return true;
                case '.': Master(app, state, board, 0.01); return true;
                case ',': Master(app, state, board, -0.01); return true;
            }
        }

        List<ClipEntry> clips = board.Clips;
        if (clips.Count == 0) return false;
        int index = Index(clips);
        ClipEntry clip = clips[index];

        switch (key.Key)
        {
            case Key.Up when key.Ctrl: Reorder(link, clips, index, -1); return true;
            case Key.Down when key.Ctrl: Reorder(link, clips, index, 1); return true;
            case Key.Up: Select(clips, Move(index, -1, clips.Count)); return true;
            case Key.Down: Select(clips, Move(index, 1, clips.Count)); return true;
            case Key.PageUp: Select(clips, Move(index, -8, clips.Count)); return true;
            case Key.PageDown: Select(clips, Move(index, 8, clips.Count)); return true;
            case Key.Home: Select(clips, 0); return true;
            case Key.End: Select(clips, clips.Count - 1); return true;
            case Key.Enter: Press(link, clip); return true;
            case Key.Char when key.Ctrl: return false;
        }
        if (key.Key != Key.Char) return false;

        switch (key.Char)
        {
            case ' ': Press(link, clip); return true;
            case 's':
                link.Send("stopClip", body => body["clip"] = clip.Id);
                LetGo(link, clip.Id);
                return true;
            case '-' or '_': Gain(link, clip, -GainStep); return true;
            case '+' or '=': Gain(link, clip, GainStep); return true;
            case '[': Gain(link, clip, -FineGainStep); return true;
            case ']': Gain(link, clip, FineGainStep); return true;
            case 'm':
                string mode = ClipEntry.Modes[(Array.IndexOf(ClipEntry.Modes, clip.Mode) + 1) % ClipEntry.Modes.Length];
                // A clip that leaves hold mode is let go, so this terminal's hold does not outlive it.
                if (mode != "hold") LetGo(link, clip.Id);
                link.Send("updateClip", body => { body["clip"] = clip.Id; body["mode"] = mode; });
                return true;
            case 'l':
                link.Send("updateClip", body => { body["clip"] = clip.Id; body["loop"] = !clip.Loop; });
                return true;
            case 'r':
                app.Ask($"Name for {clip.Name}", clip.Name, name =>
                    link.Send("updateClip", body => { body["clip"] = clip.Id; body["name"] = name; }), limit: 60);
                return true;
            case 'd':
                app.Ask($"Type yes to delete {clip.Name}", string.Empty, answer =>
                {
                    if (!answer.Equals("yes", StringComparison.OrdinalIgnoreCase)) return;
                    _held.Remove(clip.Id);
                    link.Send("deleteClip", body => body["clip"] = clip.Id);
                });
                return true;
            default:
                return false;
        }
    }

    /// <summary>SFX's own volume is the board's master, stepped as the Mixer tab steps a channel's.</summary>
    private static void Master(App app, Snapshot state, SoundboardEntry board, double by)
    {
        if (Sfx(state, board) is { } sfx) ChannelEntry.StepVolume(app, sfx, by);
        else app.Say("The SFX channel was deleted; b turns the board on and creates it again");
    }

    private void Select(List<ClipEntry> clips, int index)
    {
        _selected = clips[index].Id;
        _lastIndex = index;
    }

    /// <summary>
    /// A press. A hold clip this terminal holds while it plays is let go;
    /// any other press plays the clip in its own mode. The daemon refuses a
    /// press the board or the clip cannot take, with its sentence.
    /// </summary>
    private void Press(DaemonLink link, ClipEntry clip)
    {
        if (clip.Hold && _held.Contains(clip.Id) && clip.Playing)
        {
            LetGo(link, clip.Id);
            return;
        }
        if (clip.Hold) _held.Add(clip.Id);
        link.Send("playClip", body => body["clip"] = clip.Id);
    }

    private void LetGo(DaemonLink link, string id)
    {
        if (_held.Remove(id)) link.Send("releaseClip", body => body["clip"] = id);
    }

    private void LetGoAll(DaemonLink link)
    {
        foreach (string id in _held.ToArray()) LetGo(link, id);
    }

    private static void Gain(DaemonLink link, ClipEntry clip, double by)
    {
        double gain = Math.Round(Math.Clamp(clip.Gain + by, 0, 1), 2);
        if (gain == clip.Gain) return;
        link.Send("updateClip", body => { body["clip"] = clip.Id; body["gain"] = gain; });
    }

    private void Reorder(DaemonLink link, List<ClipEntry> clips, int index, int by)
    {
        int target = index + by;
        if (target < 0 || target >= clips.Count) return;
        List<string> order = [.. clips.Select(clip => clip.Id)];
        (order[index], order[target]) = (order[target], order[index]);
        _lastIndex = target;
        link.Send("setClipOrder", body => body["clips"] = new System.Text.Json.Nodes.JsonArray([.. order.Select(id => (System.Text.Json.Nodes.JsonNode?)id)]));
    }

    private void Import(App app)
    {
        if (_importing is not null)
        {
            app.Say("An import is still running");
            return;
        }
        app.Ask("Path of the file to import", string.Empty, typed =>
        {
            string path = ImportPath(typed);
            _importFailed = false;
            _importNote = $"Importing {Path.GetFileName(path)}";
            // Copying and checking a long file can take longer than a fader write.
            _importing = app.Link.Request("importClip", body => body["path"] = path, TimeSpan.FromMinutes(2));
        }, limit: 4096);
    }

    /// <summary>
    /// The path as the daemon wants it, absolute: quotes a file manager put
    /// around a dropped file are taken off, a <c>file://</c> address and
    /// <c>~</c> are expanded, and a relative path is taken from the
    /// directory the terminal mixer was started in, on the daemon's machine
    /// since both speak over the local socket.
    /// </summary>
    internal static string ImportPath(string typed)
    {
        string path = typed.Trim();
        if (path.Length >= 2 && (path[0] == '\'' && path[^1] == '\'' || path[0] == '"' && path[^1] == '"'))
            path = path[1..^1];
        if (path.StartsWith("file://", StringComparison.Ordinal) && Uri.TryCreate(path, UriKind.Absolute, out Uri? uri))
            path = uri.LocalPath;
        if (path == "~" || path.StartsWith("~/", StringComparison.Ordinal))
            path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + path[1..];
        try { return Path.GetFullPath(path); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return path; }
    }
}
