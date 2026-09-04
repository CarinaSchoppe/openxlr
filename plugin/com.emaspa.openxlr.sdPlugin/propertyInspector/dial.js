"use strict";

let socket = null;
let propertyInspectorUuid = null;
let actionContext = null;
let settings = {};
let labels = new Map();

function send(event) {
  if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify(event));
}

function save() {
  settings.target = settings.targets[0] ?? "";
  const active = Number.isInteger(settings.activeIndex) ? settings.activeIndex : 0;
  settings.activeIndex = Math.max(0, Math.min(active, Math.max(0, settings.targets.length - 1)));
  send({ event: "setSettings", context: actionContext, payload: settings });
}

function fillGroup(id, label, items) {
  if (!items?.length) return;
  const group = document.createElement("optgroup");
  group.id = id; group.className = "live-group"; group.label = label;
  for (const item of items) {
    labels.set(item.target, item.label);
    const option = document.createElement("option");
    option.value = item.target; option.textContent = item.label;
    if (item.meta) option.dataset.meta = JSON.stringify(item.meta);
    group.appendChild(option);
  }
  document.getElementById("target").appendChild(group);
}

function move(index, offset) {
  const next = index + offset;
  if (next < 0 || next >= settings.targets.length) return;
  [settings.targets[index], settings.targets[next]] = [settings.targets[next], settings.targets[index]];
  save(); render();
}

function render() {
  const list = document.getElementById("stack");
  list.replaceChildren();
  settings.targets.forEach((target, index) => {
    const row = document.createElement("li");
    row.style.setProperty("--accent", accent(target));
    const name = document.createElement("span"); name.textContent = labels.get(target) ?? target;
    const up = document.createElement("button"); up.textContent = "↑"; up.title = "Move up"; up.disabled = index === 0; up.onclick = () => move(index, -1);
    const down = document.createElement("button"); down.textContent = "↓"; down.title = "Move down"; down.disabled = index === settings.targets.length - 1; down.onclick = () => move(index, 1);
    const remove = document.createElement("button"); remove.className = "remove"; remove.textContent = "×"; remove.title = "Remove";
    remove.onclick = () => { settings.targets.splice(index, 1); save(); render(); };
    row.append(name, up, down, remove); list.appendChild(row);
  });
  document.getElementById("stack-count").textContent = String(settings.targets.length);
  document.getElementById("empty-stack").classList.toggle("hidden", settings.targets.length > 0);
}

function accent(target) {
  const palette = ["#39D98A", "#55A7FF", "#A879FF", "#FFB547", "#3DD6D0", "#FF6B8A"];
  let hash = 2166136261;
  for (const character of target) { hash ^= character.codePointAt(0); hash = Math.imul(hash, 16777619); }
  return palette[(hash >>> 0) % palette.length];
}

function applyConfiguration(payload) {
  const status = document.getElementById("status");
  status.textContent = payload.online ? `Live ${payload.daemonVersion ?? ""}`.trim() : "Daemon offline";
  status.className = `status ${payload.online ? "online" : "offline"}`;
  document.querySelectorAll(".live-group").forEach((node) => node.remove());
  labels = new Map([...document.querySelectorAll("#target option")].map((option) => [option.value, option.textContent]));
  for (const group of payload.levelGroups ?? []) fillGroup(group.id, group.label, group.items);
  fillGroup("param-group", "Audio effect control", payload.params);
  render();
}

function connect(inPort, inPropertyInspectorUUID, inRegisterEvent, _inInfo, inActionInfo) {
  propertyInspectorUuid = inPropertyInspectorUUID;
  const actionInfo = JSON.parse(inActionInfo);
  actionContext = actionInfo.context;
  settings = actionInfo.payload?.settings ?? {};
  if (!Array.isArray(settings.targets)) settings.targets = settings.target ? [settings.target] : [];
  const gesture = settings.cycleGesture ?? "tap";
  document.querySelector(`input[name="gesture"][value="${gesture}"]`).checked = true;
  for (const input of document.querySelectorAll('input[name="gesture"]'))
    input.addEventListener("change", () => { settings.cycleGesture = input.value; save(); });

  socket = new WebSocket(`ws://localhost:${inPort}`);
  socket.onopen = () => {
    send({ event: inRegisterEvent, uuid: propertyInspectorUuid });
    send({ event: "sendToPlugin", context: actionInfo.context, payload: { request: "configuration" } });
  };
  socket.onmessage = (event) => {
    let message;
    try { message = JSON.parse(event.data); } catch { return; }
    if (message.event === "sendToPropertyInspector") applyConfiguration(message.payload ?? {});
  };
  render();
}

document.getElementById("add").onclick = () => {
  const select = document.getElementById("target");
  const target = select.value;
  if (!target || !socket || settings.targets.includes(target)) return;
  const meta = select.selectedOptions[0]?.dataset.meta;
  if (meta) { settings.meta ??= {}; settings.meta[target] = JSON.parse(meta); }
  settings.targets.push(target); save(); render();
};

window.connectElgatoStreamDeckSocket = connect;
window.connectOpenActionSocket = connect;
