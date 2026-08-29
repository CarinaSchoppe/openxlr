// OpenXLR plugin for OpenDeck (OpenAction / Stream Deck SDK compatible).
// A thin bridge: one WebSocket to the OpenDeck host, one to the OpenXLR
// daemon. The daemon owns all state and broadcasts every change, so keys
// and dials stay in sync with the UI (and with the hardware) for free.

import process from "node:process";

// ---------- launch arguments ----------
const arg = (name) => {
  const i = process.argv.indexOf(name);
  return i >= 0 ? process.argv[i + 1] : undefined;
};
const port = arg("-port");
const pluginUUID = arg("-pluginUUID");
const registerEvent = arg("-registerEvent");

// ---------- static naming ----------
const CHANNELS = {
  xlr1: "XLR 1", xlr2: "XLR 2", aux: "Aux In", game: "Game",
  music: "Music", browser: "Browser", system: "System",
  voicechat: "Voice Chat", sfx: "SFX",
};
const MIXES = { monitor: "Monitor", stream: "Stream", chat: "Chat", auxout: "Aux" };
const MIX_SHORT = { monitor: "Mon", stream: "Str", chat: "Cht", auxout: "Aux", all: "All" };

// Toggle targets on the device state block, with short key labels.
const DEVICE_TOGGLES = {
  mute: "XLR 1\nMute", mute2: "XLR 2\nMute",
  phantom: "XLR 1\n48V", phantom2: "XLR 2\n48V",
  lowCut: "XLR 1\nLow Cut", lowCut2: "XLR 2\nLow Cut",
  expander: "XLR 1\nExpander", expander2: "XLR 2\nExpander",
  voiceTune: "XLR 1\nVoice Tune", voiceTune2: "XLR 2\nVoice Tune",
  clipGuard: "XLR 1\nClipGuard", clipGuard2: "XLR 2\nClipGuard",
  compressor: "XLR 1\nComp", compressor2: "XLR 2\nComp",
  lowImpedance: "Low Z", auxLevelLock: "Aux In\nLock",
  outHp1: "HP 1\nOut", outHp2: "HP 2\nOut", outLineOut: "Line\nOut",
  gainLocked: "Gain\nLock",
  softClipGuard: "Clip\nGuard",
};
// Targets whose ON state means "muted" (shown red instead of lit green).
const MUTE_LIKE = new Set(["mute", "mute2"]);

// ---------- daemon connection ----------
let daemon = null;
let daemonState = null;   // last full {"type":"state"} message
let daemonUp = false;
let meterLevels = null;   // last {"type":"meters"} levels, keyed ch:/mix:

function connectDaemon() {
  daemon = new WebSocket("ws://127.0.0.1:37890/ws");
  daemon.onopen = () => { daemonUp = true; refreshAll(); };
  daemon.onmessage = (e) => {
    let m;
    try { m = JSON.parse(e.data); } catch { return; }
    if (m.type === "state") { daemonState = m; refreshAll(); }
    else if (m.type === "meters") { meterLevels = m.levels; refreshMeters(); }
  };
  daemon.onclose = () => {
    daemonUp = false; daemonState = null; refreshAll();
    setTimeout(connectDaemon, 2000);
  };
  daemon.onerror = () => { /* onclose follows */ };
}
const cmd = (o) => { if (daemonUp) daemon.send(JSON.stringify(o)); };

// ---------- OpenDeck host connection ----------
const host = new WebSocket(`ws://localhost:${port}`);
const send = (o) => host.send(JSON.stringify(o));
host.onopen = () => send({ event: registerEvent, uuid: pluginUUID });
host.onclose = () => process.exit(0);

// Visible action instances: context -> {action, settings, controller}
const instances = new Map();

// OpenDeck keeps one persisted title field per key, shared by the plugin's
// setTitle and the user's own edits. So the plugin fills in a default title
// only while the title is empty, and never overwrites one the user typed.
// Clearing the title restores the default. Everything the user needs to read
// lives in the key image (frame colour, glyph, and the low-cut frequency),
// not the title, so a custom name never hides the state.
const emptyTitle = new Set();   // contexts whose OpenDeck title is ""

// The default label for a target, drawn INSIDE the key image (so it uses our
// styling, not the host's title font). A user-typed title replaces it: the
// host draws that on top and the image drops its own label.
function defaultTitle(target) {
  if (target === "softLowCut") return "Low Cut";
  return toggleLabel(target);
}

// Earlier versions pushed default titles into the host's persisted title
// field; recognise and clear those once so the in-image label takes over.
function isLegacyDefaultTitle(inst, title) {
  if (title === "offline" || title === "OpenXLR") return true;
  const t = inst.settings.target ?? "";
  if (t === "softLowCut" && title.startsWith("Low Cut")) return true;
  return title === defaultTitle(t);
}

// A dial can hold a stack of targets; long-pressing the strip cycles them.
const targetsOf = (inst) =>
  Array.isArray(inst.settings.targets) && inst.settings.targets.length
    ? inst.settings.targets
    : inst.settings.target ? [inst.settings.target] : [];
const activeTarget = (inst) => {
  const ts = targetsOf(inst);
  return ts.length ? ts[(inst.settings.activeIndex ?? 0) % ts.length] : undefined;
};
// Which gesture cycles the stack (the other keeps its mute role); only
// meaningful once the stack has at least two entries.
const cyclesOn = (inst, gesture) =>
  targetsOf(inst).length > 1 && (inst.settings.cycleGesture ?? "tap") === gesture;
function cycleStack(context, inst) {
  const ts = targetsOf(inst);
  if (ts.length < 2) return;
  inst.settings.activeIndex = ((inst.settings.activeIndex ?? 0) + 1) % ts.length;
  send({ event: "setSettings", context, payload: inst.settings });
  refresh(context);
}

host.onmessage = (e) => {
  let m;
  try { m = JSON.parse(e.data); } catch { return; }
  const inst = instances.get(m.context);
  switch (m.event) {
    case "willAppear":
      instances.set(m.context, {
        action: m.action,
        settings: m.payload?.settings ?? {},
        controller: m.payload?.controller ?? "Keypad",
      });
      refresh(m.context);
      break;
    case "willDisappear":
      instances.delete(m.context);
      emptyTitle.delete(m.context);
      break;
    case "didReceiveSettings":
      if (inst) { inst.settings = m.payload?.settings ?? {}; refresh(m.context); }
      break;
    case "keyDown":
      if (inst) onKeyDown(m.context, inst);
      break;
    case "dialRotate":
      if (inst) onDialRotate(m.context, inst, m.payload?.ticks ?? 0);
      break;
    case "dialDown":
      if (!inst) break;
      if (cyclesOn(inst, "push")) cycleStack(m.context, inst);
      else onDialPress(m.context, inst);
      break;
    case "touchTap":
      if (!inst) break;
      if (cyclesOn(inst, "tap")) cycleStack(m.context, inst);
      else onDialPress(m.context, inst);
      break;
    case "titleParametersDidChange": {
      const title = m.payload?.title ?? "";
      if (title === "") { emptyTitle.add(m.context); refresh(m.context); }
      else if (inst && isLegacyDefaultTitle(inst, title)) {
        send({ event: "setTitle", context: m.context, payload: { title: "" } });
      } else { emptyTitle.delete(m.context); refresh(m.context); }
      break;
    }
    case "sendToPlugin":
      if (m.payload?.request === "outputs")
        send({ event: "sendToPropertyInspector", context: m.context,
               payload: { outputs: outputDevices() } });
      break;
  }
};

// Physical output sinks the monitor mix can feed, for the PI's picker.
function outputDevices() {
  return (daemonState?.devices ?? [])
    .filter((d) => d.kind === 0 && !d.isOwn)
    .map((d) => ({ name: d.name, description: d.description }));
}

// ---------- state readers ----------
const dev = () => daemonState?.state ?? null;
const mixer = () => daemonState?.mixer ?? null;
const mixOf = (id) => mixer()?.mixes?.find((x) => x.id === id);
const chOf = (id) => mixer()?.channels?.find((x) => x.id === id);

// A toggle target's current boolean, or null when unknown.
function toggleValue(target) {
  if (!target) return null;
  if (target === "auxPort") return mixer()?.auxPortEnabled ?? null;
  if (target === "softLowCut") {
    const hz = mixer()?.lowCutHz;
    return hz == null ? null : hz > 0;
  }
  if (target === "softClipGuard") return mixer()?.softClipGuard ?? null;
  if (target.startsWith("monitor:")) {
    const outs = mixer()?.monitorOutputs;
    return outs ? outs.includes(target.slice(8)) : null;
  }
  if (target.startsWith("mixmute:")) return mixOf(target.slice(8))?.muted ?? null;
  if (target.startsWith("sendmute:")) {
    const [, ch, mix] = target.split(":");
    return chOf(ch)?.mutedIn?.includes(mix) ?? null;
  }
  return dev()?.[target] ?? null;
}

function toggleLabel(target) {
  if (!target) return "OpenXLR";
  if (target === "auxPort") return "Aux\nPort";
  if (target === "softLowCut") {
    const hz = mixer()?.lowCutHz ?? 0;
    return hz ? `Low Cut\n${hz} Hz` : "Low Cut\nOff";
  }
  if (target.startsWith("monitor:")) {
    const sink = target.slice(8);
    const d = daemonState?.devices?.find((x) => x.name === sink);
    const name = d?.description ?? sink.split(".").pop();
    return "Monitor\n" + name;
  }
  if (target.startsWith("mixmute:")) return `${MIXES[target.slice(8)] ?? target.slice(8)}\nMute`;
  if (target.startsWith("sendmute:")) {
    const [, ch, mix] = target.split(":");
    return `${CHANNELS[ch] ?? ch}\n· ${MIX_SHORT[mix] ?? mix}`;
  }
  return DEVICE_TOGGLES[target] ?? target;
}

const isMuteLike = (t) =>
  MUTE_LIKE.has(t) || t?.startsWith("mixmute:") || t?.startsWith("sendmute:");

// A dial target as {label, pct 0..100, text, muted}, or null when unknown.
function dialValue(target) {
  if (!daemonState || !target) return null;
  const pct = (v) => Math.round(v * 100);
  if (target.startsWith("send:")) {
    const [, chId, mix] = target.split(":");
    const ch = chOf(chId);
    if (!ch) return null;
    const v = mix === "all" ? (ch.levels?.monitor ?? 0) : (ch.levels?.[mix] ?? 0);
    const muted = mix === "all"
      ? Object.keys(ch.levels ?? {}).every((m) => ch.mutedIn?.includes(m))
      : ch.mutedIn?.includes(mix) ?? false;
    return { pin: CHANNELS[chId] ?? chId,
             scroll: mix === "all" ? "All mixes" : MIXES[mix] ?? mix,
             pct: pct(v), text: muted ? "MUTED" : `${pct(v)}%`, muted };
  }
  if (target.startsWith("mixvol:")) {
    const mix = mixOf(target.slice(7));
    if (!mix) return null;
    return { label: `${mix.name} mix`, pct: pct(mix.volume),
             text: mix.muted ? "MUTED" : `${pct(mix.volume)}%`, muted: mix.muted };
  }
  const s = dev(), x = mixer();
  switch (target) {
    case "outputVolume": {
      const v = x?.outputVolume ?? 0;
      const muted = mixOf("monitor")?.muted ?? false;
      return { label: "Monitor", pct: pct(v), text: muted ? "MUTED" : `${pct(v)}%`, muted };
    }
    case "gain": case "gain2": {
      const db = target === "gain" ? s?.gainDb : s?.gain2Db;
      const muted = target === "gain" ? s?.mute : s?.mute2;
      if (db == null) return null;
      return { label: target === "gain" ? "XLR 1 gain" : "XLR 2 gain",
               pct: Math.round((db / 80) * 100), text: muted ? "MUTED" : `${db} dB`, muted };
    }
    case "hp": case "hp2": {
      const db = target === "hp" ? s?.hpVolumeDb : s?.hp2VolumeDb;
      if (db == null) return null;
      const p = Math.round(((60 + db) / 60) * 100);
      const jackOff = (target === "hp" ? s?.outHp1 : s?.outHp2) === false;
      return { label: target === "hp" ? "Phones 1" : "Phones 2", pct: p,
               text: jackOff ? "MUTED" : `${p}%`, muted: jackOff };
    }
    case "auxLevel": {
      const db = s?.auxLevelDb;
      if (db == null) return null;
      const p = Math.round(((60 + db) / 60) * 100);
      return { label: "Aux In level", pct: p, text: `${p}%`, muted: false };
    }
    case "crossfade": {
      const v = s?.crossfade;
      if (v == null) return null;
      const text = v === 100 ? "centre" : v < 100 ? `mic +${100 - v}` : `pc +${v - 100}`;
      return { label: "Mic ↔ PC", pct: Math.round(v / 2), text, muted: false };
    }
  }
  return null;
}

// ---------- input handlers ----------
function onKeyDown(context, inst) {
  const t = inst.settings.target;
  const cur = toggleValue(t);
  if (cur === null) { send({ event: "showAlert", context }); return; }
  if (t === "auxPort") cmd({ cmd: "setAuxPortEnabled", value: !cur });
  else if (t === "softLowCut") {
    const hz = mixer()?.lowCutHz ?? 0;
    cmd({ cmd: "setLowCutHz", value: hz === 0 ? 80 : hz === 80 ? 120 : 0 });
  }
  else if (t === "softClipGuard") cmd({ cmd: "setSoftClipGuard", value: !cur });
  else if (t === "gainLocked") cmd({ cmd: "set", control: "gainLock", value: !cur });
  else if (t.startsWith("monitor:")) cmd({ cmd: "setMonitorOutputs", devices: [t.slice(8)] });
  else if (t.startsWith("mixmute:"))
    cmd({ cmd: "setMixMuted", mix: t.slice(8), value: !cur });
  else if (t.startsWith("sendmute:")) {
    const [, ch, mix] = t.split(":");
    cmd({ cmd: "setChannelMuted", channel: ch, mix, value: !cur });
  } else cmd({ cmd: "set", control: t, value: !cur });
}

function onDialRotate(context, inst, ticks) {
  const t = activeTarget(inst);
  if (!t || !daemonState) return;
  const clamp = (v, lo, hi) => Math.min(hi, Math.max(lo, v));
  if (t.startsWith("send:")) {
    const [, ch, mix] = t.split(":");
    const levels = chOf(ch)?.levels;
    if (!levels) return;
    for (const m of mix === "all" ? Object.keys(levels) : [mix]) {
      if (levels[m] == null) continue;
      cmd({ cmd: "setLevel", channel: ch, mix: m, value: clamp(levels[m] + ticks * 0.01, 0, 1) });
    }
  } else if (t.startsWith("mixvol:")) {
    const mix = t.slice(7), v = mixOf(mix)?.volume;
    if (v == null) return;
    cmd({ cmd: "setMixVolume", mix, value: clamp(v + ticks * 0.01, 0, 1) });
  } else if (t === "outputVolume") {
    const v = mixer()?.outputVolume;
    if (v == null) return;
    cmd({ cmd: "setOutputVolume", value: clamp(v + ticks * 0.01, 0, 1) });
  } else if (t === "gain" || t === "gain2") {
    const db = t === "gain" ? dev()?.gainDb : dev()?.gain2Db;
    if (db == null) return;
    cmd({ cmd: "set", control: t === "gain" ? "gain" : "gain2",
          value: clamp(db + ticks, 0, 80) });
  } else if (t === "hp" || t === "hp2") {
    const db = t === "hp" ? dev()?.hpVolumeDb : dev()?.hp2VolumeDb;
    if (db == null) return;
    cmd({ cmd: "set", control: t === "hp" ? "hpVolumeDb" : "hp2VolumeDb",
          value: clamp(db + ticks * 0.6, -60, 0) });
  } else if (t === "auxLevel") {
    const db = dev()?.auxLevelDb;
    if (db == null) return;
    cmd({ cmd: "set", control: "auxLevelDb", value: clamp(db + ticks * 0.6, -60, 0) });
  } else if (t === "crossfade") {
    const v = dev()?.crossfade;
    if (v == null) return;
    cmd({ cmd: "set", control: "crossfade", value: clamp(v + ticks * 5, 0, 200) });
  }
}

function onDialPress(context, inst) {
  const t = activeTarget(inst);
  if (!t) return;
  if (t.startsWith("send:")) {
    const [, ch, mix] = t.split(":");
    const c = chOf(ch);
    if (!c) return;
    if (mix === "all") {
      const allMuted = Object.keys(c.levels ?? {}).every((m) => c.mutedIn?.includes(m));
      for (const m of Object.keys(c.levels ?? {}))
        cmd({ cmd: "setChannelMuted", channel: ch, mix: m, value: !allMuted });
    } else {
      const muted = c.mutedIn?.includes(mix);
      if (muted != null) cmd({ cmd: "setChannelMuted", channel: ch, mix, value: !muted });
    }
  } else if (t.startsWith("mixvol:")) {
    const mix = t.slice(7), muted = mixOf(mix)?.muted;
    if (muted != null) cmd({ cmd: "setMixMuted", mix, value: !muted });
  } else if (t === "gain" || t === "gain2") {
    const control = t === "gain" ? "mute" : "mute2";
    const muted = dev()?.[control];
    if (muted != null) cmd({ cmd: "set", control, value: !muted });
  } else if (t === "outputVolume") {
    const muted = mixOf("monitor")?.muted;
    if (muted != null) cmd({ cmd: "setMixMuted", mix: "monitor", value: !muted });
  } else if (t === "hp" || t === "hp2") {
    // no per-jack mute register exists; the output selector is the mute
    const control = t === "hp" ? "outHp1" : "outHp2";
    const on = dev()?.[control];
    if (on != null) cmd({ cmd: "set", control, value: !on });
  } else if (t === "crossfade") {
    cmd({ cmd: "set", control: "crossfade", value: 100 });   // back to centre
  }
}

// ---------- rendering ----------
// Visual language borrowed from Wave Link's deck plugin (all artwork is
// ours): a full-bleed colored frame that reads state at a glance (red =
// muted, light = engaged), an inner dark card, and a white glyph. Words
// (48V, EXP, ...) ride the deck's own title renderer via setTitle.

// Centered glyphs in a 144x144 viewBox, drawn in white.
const GLYPHS = {
  mic: `<rect x="58" y="30" width="28" height="48" rx="14" fill="currentColor"/>
        <path d="M46 62 a26 26 0 0 0 52 0" stroke="currentColor" stroke-width="7" fill="none" stroke-linecap="round"/>
        <line x1="72" y1="90" x2="72" y2="104" stroke="currentColor" stroke-width="7" stroke-linecap="round"/>
        <line x1="56" y1="104" x2="88" y2="104" stroke="currentColor" stroke-width="7" stroke-linecap="round"/>`,
  speaker: `<path d="M42 58 h16 l20 -18 v64 l-20 -18 h-16 z" fill="currentColor"/>
        <path d="M88 56 a22 22 0 0 1 0 32 M96 46 a34 34 0 0 1 0 52"
              stroke="currentColor" stroke-width="6" fill="none" stroke-linecap="round"/>`,
  headphones: `<path d="M40 92 v-16 a32 32 0 0 1 64 0 v16" stroke="currentColor" stroke-width="8" fill="none" stroke-linecap="round"/>
        <rect x="34" y="86" width="16" height="26" rx="6" fill="currentColor"/>
        <rect x="94" y="86" width="16" height="26" rx="6" fill="currentColor"/>`,
  fader: `<g stroke="currentColor" stroke-width="6" stroke-linecap="round">
          <line x1="50" y1="40" x2="50" y2="104"/><line x1="72" y1="40" x2="72" y2="104"/>
          <line x1="94" y1="40" x2="94" y2="104"/></g>
        <g fill="currentColor"><rect x="41" y="76" width="18" height="12" rx="4"/>
          <rect x="63" y="52" width="18" height="12" rx="4"/>
          <rect x="85" y="66" width="18" height="12" rx="4"/></g>`,
  knob: `<circle cx="72" cy="72" r="34" stroke="currentColor" stroke-width="7" fill="none"/>
        <line x1="72" y1="72" x2="52" y2="50" stroke="currentColor" stroke-width="8" stroke-linecap="round"/>`,
  xfade: `<path d="M40 56 h50 m0 0 l-12 -10 m12 10 l-12 10" stroke="currentColor" stroke-width="7" fill="none" stroke-linecap="round" stroke-linejoin="round"/>
        <path d="M104 88 h-50 m0 0 l12 -10 m-12 10 l12 10" stroke="currentColor" stroke-width="7" fill="none" stroke-linecap="round" stroke-linejoin="round"/>`,
  jack: `<circle cx="72" cy="72" r="28" stroke="currentColor" stroke-width="7" fill="none"/>
        <circle cx="72" cy="72" r="9" fill="currentColor"/>`,
};

// Which glyph a toggle target wears; unlisted targets are word keys
// (frame + LED + title only).
function glyphFor(t) {
  if (!t) return null;
  if (t === "mute" || t === "mute2") return "mic";
  if (t === "outHp1" || t === "outHp2" || t === "lowImpedance") return "headphones";
  if (t === "outLineOut") return "jack";
  if (t.startsWith("monitor:") || t.startsWith("mixmute:")) return "speaker";
  if (t.startsWith("sendmute:")) return "fader";
  return null;
}


// Badge text drawn as seven-segment figures (like the LED displays on rack
// gear), so it renders identically on every machine instead of through
// whatever font the host's SVG rasterizer finds. Segments: a top, b top
// right, c bottom right, d bottom, e bottom left, f top left, g middle.
const SEGMENTS = {
  "0": "abcdef", "1": "bc", "2": "abged", "3": "abgcd", "4": "fgbc",
  "5": "afgcd", "6": "afgedc", "7": "abc", "8": "abcdefg", "9": "abcfgd",
  "O": "abcdef", "F": "afge",
};
function sevenSegText(text, x, y, h, color) {
  const w = h * 0.58, t = h * 0.20, gap = w * 0.40;   // digit box + stroke
  const seg = {
    a: [t * 0.7, 0, w - 1.4 * t, t], b: [w - t, t * 0.6, t, h / 2 - t],
    c: [w - t, h / 2 + t * 0.4, t, h / 2 - t], d: [t * 0.7, h - t, w - 1.4 * t, t],
    e: [0, h / 2 + t * 0.4, t, h / 2 - t], f: [0, t * 0.6, t, h / 2 - t],
    g: [t * 0.7, (h - t) / 2, w - 1.4 * t, t],
  };
  const total = text.length * w + (text.length - 1) * gap;
  const draw = (cx, names, fill, opacity) => {
    let o = `<g fill="${fill}" opacity="${opacity}">`;
    for (const sName of names) {
      const [sx, sy, sw, sh] = seg[sName];
      o += `<rect x="${(cx + sx).toFixed(1)}" y="${(y + sy).toFixed(1)}" width="${sw.toFixed(1)}" height="${sh.toFixed(1)}" rx="${(t / 2).toFixed(1)}"/>`;
    }
    return o + "</g>";
  };
  let out = "";
  let cx = x - total / 2;
  for (const ch of text) {
    const lit = SEGMENTS[ch.toUpperCase()] ?? "";
    out += draw(cx, "abcdefg", color, 0.14);   // ghost of unlit segments
    out += draw(cx, lit, color, 1);
    cx += w + gap;
  }
  return out;
}

function keySvg(on, muteLike, known, glyphName, badge, label) {
  // Same material as the dial strips: a rounded card, a top-lit gradient, a
  // fine border. State is carried by colour, not a flat frame: the accent
  // (green engaged, red muted) tints the glyph, brightens the border, and
  // adds a soft glow, so a lit key reads at a glance without shouting.
  const accent = !known ? null : on ? (muteLike ? "#FF3C4E" : "#3ecf7a") : null;
  const border = accent ?? "#505050";
  const ink = !known ? "#5d6474" : accent ?? "#c7ccd6";
  const glyph = glyphName ? GLYPHS[glyphName].replaceAll("currentColor", ink) : "";
  const slash = muteLike && on
    ? `<line x1="44" y1="106" x2="100" y2="40" stroke="#FF3C4E" stroke-width="9" stroke-linecap="round"/>`
    : "";
  const glow = accent
    ? `<rect x="7" y="7" width="130" height="130" rx="15" fill="none" stroke="${accent}" stroke-width="12" opacity="0.16"/>`
    : "";
  // A key with a glyph shows it; a plain state key shows the badge big (the
  // low-cut frequency) or, with neither, a target-style indicator dot.
  // With an in-image label the artwork rides higher; without one it centres.
  const lines = label ? label.split("\n").slice(0, 2) : [];
  const lift = lines.length ? -12 : 0;
  let center;
  if (glyphName) center = `<g transform="translate(0 ${lift})">${glyph}</g>`;
  else if (badge)
    center = sevenSegText(badge, 72, lines.length ? 30 : 48, lines.length ? 42 : 48, ink);
  else
    center = `<g transform="translate(0 ${lift})">` +
             `<circle cx="72" cy="72" r="21" fill="none" stroke="${ink}" stroke-width="3" opacity="0.45"/>` +
             `<circle cx="72" cy="72" r="12" fill="${ink}"/></g>`;
  const smallBadge = glyphName && badge
    ? `<rect x="47" y="${91 + lift}" width="50" height="26" rx="13" fill="#0d0e11"/>` +
      sevenSegText(badge, 72, 96 + lift, 16, ink)
    : "";
  const labelSvg = lines.map((line, i) => {
    const size = line.length > 11 ? 17 : line.length > 8 ? 21 : 26;
    const y = lines.length === 1 ? 126 : 106 + i * 24;
    return `<text x="72" y="${y}" text-anchor="middle" fill="#e8ebf2" ` +
      `stroke="#000" stroke-width="4" paint-order="stroke" stroke-linejoin="round" ` +
      `font-family="Inter, Noto Sans, DejaVu Sans, sans-serif" font-size="${size}" font-weight="700">` +
      escXml(line) + `</text>`;
  }).join("");
  return "data:image/svg+xml;base64," + Buffer.from(
    `<svg xmlns="http://www.w3.org/2000/svg" width="288" height="288" viewBox="0 0 144 144">
      <defs><linearGradient id="g" x1="72" y1="8" x2="72" y2="136" gradientUnits="userSpaceOnUse">
        <stop stop-color="#fff" stop-opacity="0.10"/><stop offset="0.5" stop-opacity="0"/>
        <stop offset="1" stop-opacity="0.22"/></linearGradient></defs>
      <rect x="6" y="6" width="132" height="132" rx="16" fill="#33363c"/>
      <rect x="6" y="6" width="132" height="132" rx="16" fill="url(#g)"/>
      ${glow}
      <rect x="8" y="8" width="128" height="128" rx="14" fill="none" stroke="${border}" stroke-width="5"/>
      ${center}${slash}${smallBadge}${labelSvg}
    </svg>`).toString("base64");
}

// 24x24 white icons for the dial layout's corner slot.
function dialIcon(t) {
  const inner = (name) => GLYPHS[name]
    ? `<g transform="scale(0.1667)">${GLYPHS[name].replaceAll("currentColor", "#ffffff")}</g>` : "";
  let name = "knob";
  if (t?.startsWith("send:")) name = "fader";
  else if (t?.startsWith("mixvol:")) name = "speaker";
  else if (t === "outputVolume") name = "speaker";
  else if (t === "gain" || t === "gain2") name = "mic";
  else if (t === "hp" || t === "hp2") name = "headphones";
  else if (t === "crossfade") name = "xfade";
  return "data:image/svg+xml;base64," + Buffer.from(
    `<svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24">${inner(name)}</svg>`
  ).toString("base64");
}

// The rotating needle over the half-knob, Wave Link style: a tick rotated
// around a center below the visible strip. 0..100% sweeps -50°..+50°.
function needleSvg(pct) {
  const angle = (Math.max(0, Math.min(100, pct)) / 100) * 100 - 50;
  return "data:image/svg+xml;base64," + Buffer.from(
    `<svg xmlns="http://www.w3.org/2000/svg" width="130" height="52" viewBox="0 0 130 52">
      <g transform="rotate(${angle}, 65, 61)">
        <rect x="63.5" y="30" width="3" height="16" rx="1.5" fill="#fff"/>
      </g>
    </svg>`).toString("base64");
}

// The meter key feeding a dial target's level bar.
function meterKeyFor(t) {
  if (!t) return null;
  if (t.startsWith("send:")) return `ch:${t.split(":")[1]}`;
  if (t.startsWith("mixvol:")) return `mix:${t.slice(7)}`;
  if (t === "gain") return "ch:xlr1";
  if (t === "gain2") return "ch:xlr2";
  if (t === "auxLevel") return "ch:aux";
  return "mix:monitor";   // outputVolume, hp, hp2, crossfade
}

function meterSvg(level) {
  const w = Math.round(Math.max(0, Math.min(1, level)) * 130);
  const hot = level > 0.92;
  return "data:image/svg+xml;base64," + Buffer.from(
    `<svg xmlns="http://www.w3.org/2000/svg" width="130" height="6" viewBox="0 0 130 6">
      <rect width="130" height="6" rx="3" fill="#252525"/>
      ${w > 0 ? `<rect width="${w}" height="6" rx="3" fill="${hot ? "#FF3C4E" : "#3ecf7a"}"/>` : ""}
    </svg>`).toString("base64");
}

// Meters tick at 15 Hz; only redraw a dial's bar when its value moved
// visibly, so the strip is not re-rendered for noise.
const lastMeter = new Map();   // context -> rounded width last drawn
function refreshMeters() {
  if (!meterLevels) return;
  for (const [context, inst] of instances) {
    if (inst.action !== "com.emaspa.openxlr.dial") continue;
    const key = meterKeyFor(activeTarget(inst));
    if (!key || !(key in meterLevels)) continue;
    const lr = meterLevels[key];
    const level = Math.max(lr[0] ?? 0, lr[1] ?? 0);
    const bucket = Math.round(level * 65);
    if (lastMeter.get(context) === bucket) continue;
    lastMeter.set(context, bucket);
    send({ event: "setFeedback", context, payload: { meter: meterSvg(level) } });
  }
}

// The title is rendered as our own pixmap so the scroll is pixel-exact
// across the full strip width, the way Wave Link's plugin uses it. Text
// width is made deterministic with SVG textLength (approximated from a
// per-character average, then enforced by the renderer). A send dial
// pins the channel name and scrolls the mix name in the space that
// remains; other long titles scroll whole.
const TITLE_W = 158, TITLE_H = 24, CHAR_W = 8.1, GAP_PX = 20, STEP_PX = 7;
const escXml = (t) => t.replace(/&/g, "&amp;").replace(/</g, "&lt;");
const textW = (t) => Math.round(t.length * CHAR_W);

function titleSvg(pinText, scroll, offsetPx) {
  const attrs = 'y="17" font-family="sans-serif" font-size="14.5" font-weight="700" fill="#ffffff"';
  const pinW = textW(pinText);
  const scrollW = textW(scroll);
  const avail = TITLE_W - pinW;
  const pinPart = pinText === "" ? "" :
    `<text x="0" ${attrs} textLength="${pinW - 4}" lengthAdjust="spacingAndGlyphs">${escXml(pinText)}</text>`;
  let body;
  if (scrollW <= avail) {
    body = `<text x="${pinW}" ${attrs}>${escXml(scroll)}</text>`;
  } else {
    const total = scrollW + GAP_PX;
    const o = offsetPx % total;
    const t = (x) =>
      `<text x="${x}" ${attrs} textLength="${scrollW}" lengthAdjust="spacingAndGlyphs">${escXml(scroll)}</text>`;
    body = `<svg x="${pinW}" y="0" width="${avail}" height="${TITLE_H}">${t(-o)}${t(-o + total)}</svg>`;
  }
  return "data:image/svg+xml;base64," + Buffer.from(
    `<svg xmlns="http://www.w3.org/2000/svg" width="${TITLE_W}" height="${TITLE_H}" viewBox="0 0 ${TITLE_W} ${TITLE_H}">${pinPart}${body}</svg>`
  ).toString("base64");
}

const marquee = new Map();   // context -> {pin, scroll, offset, hold, total}
function marqueeTitle(context, pin, scroll) {
  let pinText = pin === "" ? "" : `${pin} · `;
  // with no room left beside the pin, scroll the whole thing instead
  if (pinText !== "" && TITLE_W - textW(pinText) < 40) { scroll = pinText + scroll; pinText = ""; }
  if (textW(scroll) <= TITLE_W - textW(pinText)) { marquee.delete(context); return titleSvg(pinText, scroll, 0); }
  let m = marquee.get(context);
  if (!m || m.pinText !== pinText || m.scroll !== scroll)
    { m = { pinText, scroll, offset: 0, hold: 3, total: textW(scroll) + GAP_PX }; marquee.set(context, m); }
  return titleSvg(pinText, m.scroll, m.offset);
}
setInterval(() => {
  for (const [context, m] of marquee) {
    if (!instances.has(context)) { marquee.delete(context); continue; }
    if (m.hold > 0) { m.hold--; continue; }
    m.offset += STEP_PX;
    if (m.offset >= m.total) { m.offset = 0; m.hold = 3; }   // pause at each wrap
    send({ event: "setFeedback", context,
           payload: { title: titleSvg(m.pinText, m.scroll, m.offset) } });
  }
}, 350);

function refresh(context) {
  const inst = instances.get(context);
  if (!inst) return;
  const t = inst.action === "com.emaspa.openxlr.dial"
    ? activeTarget(inst) : inst.settings.target;
  if (inst.action === "com.emaspa.openxlr.toggle") {
    const v = toggleValue(t);
    const badge = t === "softLowCut" ? (mixer()?.lowCutHz ? String(mixer().lowCutHz) : "OFF") : "";
    const label = emptyTitle.has(context) ? (daemonUp ? defaultTitle(t) : "offline") : "";
    // The user can pick a glyph per key (a monitor output may be headphones
    // rather than speakers); "auto" or unset keeps the target's default.
    const iconChoice = inst.settings.icon;
    const glyphName = iconChoice && GLYPHS[iconChoice] ? iconChoice : glyphFor(t);
    send({ event: "setImage", context,
           payload: { image: keySvg(v === true, isMuteLike(t), v !== null && daemonUp, glyphName, badge, label) } });
  } else if (inst.action === "com.emaspa.openxlr.dial") {
    const d = dialValue(t);
    const isDb = t === "gain" || t === "gain2";
    send({ event: "setFeedback", context, payload: d
      ? { title: marqueeTitle(context, d.pin ?? "", d.scroll ?? d.label),
          value: isDb && !d.muted ? d.text.replace(" dB", "") : d.text,
          unit: { enabled: isDb && !d.muted },
          icon: dialIcon(t),
          needle: needleSvg(d.pct),
          muteOverlay: { enabled: d.muted } }
      : { title: "OpenXLR", value: daemonUp ? "set up" : "offline",
          unit: { enabled: false }, icon: dialIcon(null),
          needle: needleSvg(0), muteOverlay: { enabled: false } } });
  }
}

function refreshAll() { for (const context of instances.keys()) refresh(context); }

connectDaemon();
