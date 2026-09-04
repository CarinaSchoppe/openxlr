// Pure mixer helpers shared by the OpenDeck runtime and its tests. Keeping
// target discovery here prevents the property inspectors from drifting back
// to a hard-coded list when users add, rename, reorder, or delete channels.

const LEGACY_CHANNEL_NAMES = {
  xlr1: "XLR 1", xlr2: "XLR 2", aux: "Aux In", game: "Game",
  music: "Music", browser: "Browser", system: "System",
  voicechat: "Voice Chat", sfx: "SFX",
};
const LEGACY_MIX_NAMES = {
  monitor: "Monitor", stream: "Stream", chat: "Chat", auxout: "Aux",
};

export function channelName(mixer, id) {
  return mixer?.channels?.find((channel) => channel.id === id)?.name
    ?? LEGACY_CHANNEL_NAMES[id] ?? id;
}

export function mixName(mixer, id) {
  return mixer?.mixes?.find((mix) => mix.id === id)?.name
    ?? LEGACY_MIX_NAMES[id] ?? id;
}

export function shortName(value, limit = 11) {
  const text = String(value ?? "").trim();
  return text.length <= limit ? text : `${text.slice(0, Math.max(1, limit - 1))}…`;
}

function item(target, label) {
  return { target, label };
}

/**
 * Build every mixer-dependent property-inspector choice from authoritative
 * daemon state. Stable ids live in targets; display names remain freely
 * editable without invalidating existing Stream Deck actions.
 */
export function mixerChoices(mixer) {
  const mixes = mixer?.mixes ?? [];
  const channels = mixer?.channels ?? [];
  if (!mixes.length || !channels.length) return { toggleGroups: [], levelGroups: [] };

  const toggleGroups = [
    {
      id: "listen-mix-group",
      label: "Listen to mix",
      items: mixes.map((mix) => item(`listen:${mix.id}`, mix.name)),
    },
    {
      id: "mix-mute-group",
      label: "Mute an output mix",
      items: mixes.map((mix) => item(`mixmute:${mix.id}`, mix.name)),
    },
    {
      id: "route-group",
      label: "Channel routing (lit = included)",
      items: mixes.flatMap((mix) => channels.map((channel) =>
        item(`route:${channel.id}:${mix.id}`, `${channel.name} → ${mix.name}`))),
    },
  ];

  const levelGroups = [
    {
      id: "mix-level-group",
      label: "Output mix level",
      items: mixes.map((mix) => item(`mixvol:${mix.id}`, mix.name)),
    },
    {
      id: "channel-all-level-group",
      label: "Channel level in every mix",
      items: channels.map((channel) => item(`send:${channel.id}:all`, channel.name)),
    },
    ...mixes.map((mix) => ({
      id: `channel-${mix.id}-level-group`,
      label: `Channel level in ${mix.name}`,
      items: channels.map((channel) =>
        item(`send:${channel.id}:${mix.id}`, `${channel.name} → ${mix.name}`)),
    })),
  ];

  return { toggleGroups, levelGroups };
}

export function monitorOutputChoices(state) {
  return (state?.devices ?? [])
    .filter((device) => device.kind === 0 && !device.isOwn)
    .map((device) => ({
      target: `monitor:${device.name}`,
      label: device.description || device.name,
    }));
}

/** Return a new monitor-output set after toggling one device. */
export function toggledMonitorOutputs(current, device) {
  const outputs = [...new Set(current ?? [])];
  return outputs.includes(device)
    ? outputs.filter((name) => name !== device)
    : [...outputs, device];
}

/** Deterministic OpenXLR accent per stable target, independent of its name. */
export function targetAccent(target) {
  const palette = ["#39D98A", "#55A7FF", "#A879FF", "#FFB547", "#3DD6D0", "#FF6B8A"];
  let hash = 2166136261;
  for (const char of String(target ?? "")) {
    hash ^= char.codePointAt(0);
    hash = Math.imul(hash, 16777619);
  }
  return palette[(hash >>> 0) % palette.length];
}

export function normalisedLevelCommand(target, percent, mixer) {
  const value = Math.min(100, Math.max(0, Number(percent))) / 100;
  if (target?.startsWith("send:")) {
    const [, channel, mix] = target.split(":");
    const mixIds = mix === "all"
      ? Object.keys(mixer?.channels?.find((entry) => entry.id === channel)?.levels ?? {})
      : [mix];
    return mixIds.map((mixId) => ({ cmd: "setLevel", channel, mix: mixId, value }));
  }
  if (target?.startsWith("mixvol:"))
    return [{ cmd: "setMixVolume", mix: target.slice(7), value }];
  if (target === "outputVolume") return [{ cmd: "setOutputVolume", value }];
  if (target === "gain" || target === "gain2")
    return [{ cmd: "set", control: target, value: Math.round(value * 80) }];
  if (target === "hp" || target === "hp2")
    return [{ cmd: "set", control: target === "hp" ? "hpVolumeDb" : "hp2VolumeDb",
      value: -60 + value * 60 }];
  if (target === "auxLevel")
    return [{ cmd: "set", control: "auxLevelDb", value: -60 + value * 60 }];
  if (target === "crossfade")
    return [{ cmd: "set", control: "crossfade", value: Math.round(value * 200) }];
  return [];
}
