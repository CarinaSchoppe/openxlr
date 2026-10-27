// Microphone EQ targets use stable channel ids and the daemon's reported
// ranges. There is no second EQ, preset table or hardware backend here.
const FIELDS = {eq: "enabled", eqband: "enabled", eqtype: "type", eqfreq: "frequency", eqgain: "gain", eqq: "q"};
const TYPE_NAMES = {lowCut: "High pass", lowShelf: "Low shelf", peak: "Bell", notch: "Notch", highShelf: "High shelf", highCut: "Low pass"};
const typeName = (type, compact = false) => compact && type === "highShelf" ? "Hi shelf"
  : Object.hasOwn(TYPE_NAMES, type) ? TYPE_NAMES[type] : type;
const channels = mixer => Array.isArray(mixer?.channels) ? mixer.channels : [];
const nameOf = channel => typeof channel.name === "string" && channel.name ? channel.name : channel.id;
const usesGain = type => ["lowShelf", "peak", "highShelf"].includes(type);
const finite = value => typeof value === "number" && Number.isFinite(value);
const clamp = (value, min, max) => Math.min(max, Math.max(min, value));
const round = value => Math.sign(value) * Math.round(Math.abs(value));

export function isEqTarget(target) {
  return typeof target === "string" && /^(eq|eqband|eqtype|eqfreq|eqgain|eqq):/.test(target);
}

function validRange(range, logarithmic = false, step = "step") {
  return range && finite(range.min) && finite(range.max) && finite(range.max - range.min) && range.min < range.max
    && (!logarithmic || range.min > 0 && finite(Math.log(range.max / range.min)))
    && finite(range[step]) && range[step] > 0;
}

export function eqTarget(mixer, target) {
  if (!isEqTarget(target) || target.length > 128) return null;
  const [kind, id, number, ...extra] = target.split(":");
  if (!id || id.length > 64 || !/^[a-zA-Z0-9_-]+$/.test(id) || extra.length
      || (kind === "eq" ? number !== undefined : !/^[1-9][0-9]?$/.test(number ?? ""))) return null;
  const channel = channels(mixer).find(c => c?.id === id && c.present !== false
    && (c.microphone === true || c.microphone !== false && (id === "xlr1" || id === "xlr2")));
  const eq = channel?.eq, limits = mixer?.eqLimits;
  const min = limits?.minBands ?? limits?.bands, max = limits?.maxBands ?? limits?.bands;
  if (!eq || typeof eq.enabled !== "boolean" || !limits || !Number.isInteger(limits.bands)
      || limits.bands < 1 || limits.bands > 16 || !Number.isInteger(min) || !Number.isInteger(max)
      || min < 1 || max > 16 || min > max || !Array.isArray(eq.bands) || eq.bands.length < min || eq.bands.length > max
      || !Array.isArray(limits.types) || !limits.types.length || limits.types.length > 16
      || limits.types.some(t => typeof t !== "string" || t.length > 64 || !/^[a-zA-Z]+$/.test(t))
      || new Set(limits.types).size !== limits.types.length
      || !validRange(limits.frequency, true) || !validRange(limits.gain) || !validRange(limits.q, true, "log10Step")) return null;
  for (const band of eq.bands) {
    if (!band || typeof band.enabled !== "boolean" || !limits.types.includes(band.type)
        || !["frequency", "gain", "q"].every(field => finite(band[field])
          && band[field] >= limits[field].min && band[field] <= limits[field].max)) return null;
  }
  const band = kind === "eq" ? 0 : Number(number);
  if (band > eq.bands.length) return null;
  const field = FIELDS[kind], state = band ? eq.bands[band - 1] : eq;
  const value = field === "type" ? limits.types.indexOf(state.type) : state[field];
  return {kind, channel, eq, limits, band, field, value,
    usable: field !== "gain" || usesGain(state.type)};
}

export function eqChoices(mixer) {
  const toggleGroups = [], levelGroups = [];
  for (const channel of channels(mixer)) {
    if (typeof channel?.id !== "string") continue;
    const entry = eqTarget(mixer, `eq:${channel.id}`);
    if (!entry) continue;
    const name = nameOf(channel);
    const toggles = [{target: `eq:${channel.id}`, label: `${name} EQ on/off`}];
    const levels = [];
    for (let band = 1; band <= entry.eq.bands.length; band++) {
      toggles.push({target: `eqband:${channel.id}:${band}`, label: `${name} EQ band ${band} on/off`},
        {target: `eqtype:${channel.id}:${band}`, label: `${name} EQ band ${band} next type`});
      for (const [prefix, label] of [["eqfreq", "frequency"], ["eqgain", "gain"], ["eqq", "Q"], ["eqtype", "type"]])
        levels.push({target: `${prefix}:${channel.id}:${band}`, label: `${name} EQ band ${band} ${label}`});
    }
    toggleGroups.push({id: `layout-eq-keys-${channel.id}`, label: `Microphone EQ: ${name}`, items: toggles});
    levelGroups.push({id: `layout-eq-dials-${channel.id}`, label: `Microphone EQ: ${name}`, items: levels});
  }
  return {toggleGroups, levelGroups};
}

export function eqStep(entry, current, ticks) {
  if (!entry?.usable || !finite(current) || !Number.isSafeInteger(ticks) || ticks === 0 || entry.field === "enabled") return null;
  if (entry.field === "type") {
    const count = entry.limits.types.length;
    return ((current + ticks % count) % count + count) % count;
  }
  const range = entry.limits[entry.field];
  const next = entry.field === "frequency" ? current * Math.pow(2, ticks / 24)
    : entry.field === "q" ? current * Math.pow(10, ticks * range.log10Step)
    : current + ticks * range.step;
  const bounded = clamp(next, range.min, range.max);
  const snapped = entry.field === "q"
    ? Math.pow(10, Math.log10(range.min) + round((Math.log10(bounded) - Math.log10(range.min)) / range.log10Step) * range.log10Step)
    : round(bounded / range.step) * range.step;
  return finite(snapped) ? clamp(snapped, range.min, range.max) : null;
}

export function eqCommand(entry, value) {
  if (!entry) return null;
  const {field, band, channel, limits} = entry;
  if (field === "enabled") {
    if (typeof value !== "boolean" || !band && value && entry.eq.available === false) return null;
    return band ? {cmd: "setEqBand", channel: channel.id, band, enabled: value}
      : {cmd: "setEqEnabled", channel: channel.id, value};
  }
  if (field === "type") {
    if (!Number.isInteger(value) || value < 0 || value >= limits.types.length) return null;
    value = limits.types[value];
  } else if (!entry.usable || !finite(value) || value < limits[field].min || value > limits[field].max) return null;
  return {cmd: "setEqBand", channel: channel.id, band, [field]: value};
}

export function eqLabel(entry, value = entry?.value) {
  if (!entry) return "Microphone EQ";
  const name = nameOf(entry.channel);
  return !entry.band ? `${name}\nEQ` : entry.field === "type"
    ? `${name}\n${entry.band}: ${typeName(entry.limits.types[value])}`
    : `${name}\nEQ band ${entry.band}`;
}

export function eqDial(entry, value) {
  if (!entry || entry.field === "enabled" || !finite(value)) return null;
  if (entry.field === "type" && (!Number.isInteger(value) || value < 0 || value >= entry.limits.types.length)) return null;
  const range = entry.limits[entry.field];
  if (range) value = clamp(value, range.min, range.max);
  const pct = entry.field === "type" ? value / Math.max(1, entry.limits.types.length - 1) * 100
    : entry.field === "gain" ? (value - range.min) / (range.max - range.min) * 100
    : Math.log(value / range.min) / Math.log(range.max / range.min) * 100;
  const text = !entry.usable ? "n/a" : entry.field === "type" ? typeName(entry.limits.types[value], true)
    : entry.field === "frequency" ? `${Math.round(value)} Hz` : entry.field === "gain" ? `${Number(value.toFixed(2))} dB`
    : `Q ${Number(value.toFixed(4))}`;
  const status = entry.eq.available === false ? " (unavailable)" : !entry.eq.enabled ? " (EQ off)"
    : !entry.eq.bands[entry.band - 1].enabled ? " (band off)" : "";
  return {pin: nameOf(entry.channel), scroll: `EQ ${entry.band} ${entry.field}${status}`, pct: clamp(pct, 0, 100), text, muted: false};
}
