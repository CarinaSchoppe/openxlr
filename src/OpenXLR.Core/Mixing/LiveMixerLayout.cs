namespace OpenXLR.Core.Mixing;

public sealed partial class Mixer
{
    /// <summary>
    /// Add only the new application sink. Readers cannot observe its layout
    /// until persistence succeeds. A failed save removes only the new module.
    /// The caller serializes its save callback with other settings writers.
    /// </summary>
    public void CreateApplicationChannel(string name, Func<MixerSettings, string?> persist)
    {
        ArgumentNullException.ThrowIfNull(persist);
        name = name.Trim();
        if (name.Length is 0 or > 60 || name.Any(char.IsControl))
            throw new InvalidOperationException("channel name must contain 1 to 60 printable characters");
        lock (_gate)
        {
            if (!_built) throw new InvalidOperationException("mixer is not built");
            if (_config.Channels.Count(c => c.InputPair is null) >= MixerConfig.MaxApplicationChannels)
                throw new InvalidOperationException("application channel limit reached");
            string id = NewChannelId(name, _config.Channels.Select(c => c.Id));
            var channel = new ChannelDefinition(id, name)
            { Levels = _config.Mixes.ToDictionary(m => m.Id, _ => 1.0) };
            MixerConfig previous = _config;
            uint module = _pw.CreateCombineSink(channel.SinkName,
                _config.Mixes.Select(m => m.SinkName), $"OpenXLR {name}");
            try
            {
                _config = _config with { Channels = [.. _config.Channels, channel] };
                _combineModules[id] = module;
                var legs = _pw.FindCombineLegs(module);
                foreach (var mix in _config.Mixes)
                {
                    string cell = Cell(id, mix.Id);
                    _cells.Add(cell);
                    _levels[cell] = 1.0;
                    if (legs.TryGetValue(mix.SinkName, out int index)) _legIndex[cell] = index;
                    ApplyCellLocked(id, mix.Id);
                }
                if (persist(ExportSettings()) is string error) throw new IOException(error);
            }
            catch (Exception editError)
            {
                _config = previous;
                _combineModules.Remove(id);
                foreach (var mix in previous.Mixes)
                {
                    string cell = Cell(id, mix.Id);
                    _cells.Remove(cell);
                    _levels.Remove(cell);
                    _muted.Remove(cell);
                    _legIndex.Remove(cell);
                }
                try { _pw.UnloadModule(module); }
                catch (Exception cleanupError)
                { throw new AggregateException("channel creation failed and its module could not be removed", editError, cleanupError); }
                throw;
            }
            _meters.Add($"ch:{id}", channel.SinkName);
        }
    }

    internal static string NewChannelId(string name, IEnumerable<string> existing)
    {
        string slug = new(name.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray());
        slug = string.Join('-', slug.Split('-', StringSplitOptions.RemoveEmptyEntries));
        if (slug.Length == 0) slug = "channel";
        if (!char.IsAsciiLetter(slug[0])) slug = "channel-" + slug;
        if (slug.Length > 28) slug = slug[..28].TrimEnd('-');
        var used = new HashSet<string>(existing.Append(StreamMatcher.Ignore), StringComparer.OrdinalIgnoreCase);
        string id = slug;
        for (int n = 2; used.Contains(id); n++) id = $"{slug}-{n}";
        return id;
    }
}
