// The voice changer key: a Toggle action target "voice:<channel>:<voice>",
// read from the daemon's mixer state with no I/O, so plugin.mjs stays small
// and this stays testable. The key is lit while that microphone plays that
// voice. In toggle mode a press switches the voice on, or off when it is
// the one playing; in hold mode the voice plays while the key is down. Each
// press is one setVoiceChanger carrying the switch, and the voice when it
// switches on, so no press reads the state first.

export const VOICES = ["custom", "deep", "high", "robot", "radio", "alien", "dracula", "frankenstein", "mummy", "werewolf", "witch"];
export const VOICE_MODES = ["toggle", "hold"];

const NAMES = {custom: "Custom", deep: "Deep", high: "High", robot: "Robot", radio: "Radio", alien: "Alien",
  dracula: "Dracula", frankenstein: "Frankenstein", mummy: "Mummy", werewolf: "Werewolf", witch: "Witch"};

const channels = mixer => Array.isArray(mixer?.channels) ? mixer.channels : [];
const nameOf = channel => typeof channel.name === "string" && channel.name ? channel.name : channel.id;

export const voiceName = voice => Object.hasOwn(NAMES, voice) ? NAMES[voice] : voice;

export function isVoiceTarget(target) {
  return typeof target === "string" && target.startsWith("voice:");
}

// What a voice key reads, or null when the target is malformed, the
// channel is gone or absent, or the daemon reports no voice changer on it:
// {channel, voice, setting, lit, available}.
export function voiceTarget(mixer, target) {
  if (!isVoiceTarget(target) || target.length > 128) return null;
  const [, id, voice, ...extra] = target.split(":");
  if (!id || id.length > 64 || !/^[a-zA-Z0-9_-]+$/.test(id) || extra.length || !VOICES.includes(voice)) return null;
  const channel = channels(mixer).find(c => c?.id === id && c.present !== false);
  const setting = channel?.voice;
  if (!setting || typeof setting.enabled !== "boolean") return null;
  return {channel, voice, setting, lit: setting.enabled && setting.voice === voice, available: setting.available !== false};
}

// The press mode a key's settings choose: toggle unless it says hold.
export const voiceMode = settings => VOICE_MODES.includes(settings?.voiceMode) ? settings.voiceMode : "toggle";

// What a press sends, {payload, hold}, or null when it can do nothing: a
// voice cannot be switched on while the native plugin host is missing.
// Inside a multi-action OpenDeck sends the key up 100 ms after the key down,
// so a hold there would end at once: it toggles instead.
export function voicePress(entry, mode = "toggle", inMultiAction = false) {
  if (!entry) return null;
  const channel = entry.channel.id;
  const hold = mode === "hold" && !inMultiAction;
  if (!hold && entry.lit) return {payload: {cmd: "setVoiceChanger", channel, enabled: false}, hold: false};
  if (!entry.available) return null;
  return {payload: {cmd: "setVoiceChanger", channel, enabled: true, voice: entry.voice}, hold};
}

// What a hold key's release sends: the voice changer off, with no read.
export const voiceRelease = channel => ({cmd: "setVoiceChanger", channel, enabled: false});

export function voiceLabel(entry, target) {
  if (!entry) {
    const voice = typeof target === "string" ? target.split(":")[2] : undefined;
    return VOICES.includes(voice) ? `Voice\n${voiceName(voice)}` : "Voice";
  }
  return `${nameOf(entry.channel)}\n${voiceName(entry.voice)}`;
}

// The inspector's groups: one per microphone with a voice changer, a key per voice.
export function voiceChoices(mixer) {
  const toggleGroups = [];
  for (const channel of channels(mixer)) {
    if (typeof channel?.id !== "string" || !voiceTarget(mixer, `voice:${channel.id}:deep`)) continue;
    const name = nameOf(channel);
    toggleGroups.push({id: `layout-voice-keys-${channel.id}`, label: `Voice changer: ${name}`,
      items: VOICES.map(voice => ({target: `voice:${channel.id}:${voice}`, label: `${name} voice: ${voiceName(voice)}`}))});
  }
  return {toggleGroups};
}
