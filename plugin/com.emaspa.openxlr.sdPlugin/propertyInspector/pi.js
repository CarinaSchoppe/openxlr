"use strict";

let socket = null;
let propertyInspectorUuid = null;
let actionContext = null;
let settings = {};
const kind = document.body.dataset.kind;

function send(event) {
  if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify(event));
}

function save() {
  settings.target = document.getElementById("target").value;
  const icon = document.getElementById("icon");
  if (icon) settings.icon = icon.value;
  const behaviour = document.getElementById("behaviour");
  if (behaviour) {
    settings.behaviour = behaviour.value;
    settings.amount = Number(document.getElementById("amount").value);
  }
  const option = document.getElementById("target").selectedOptions[0];
  if (option?.dataset.meta) {
    settings.meta ??= {};
    settings.meta[settings.target] = JSON.parse(option.dataset.meta);
  }
  send({ event: "setSettings", context: actionContext, payload: settings });
}

function fillGroup(id, label, items) {
  if (!items?.length) return;
  const group = document.createElement("optgroup");
  group.id = id;
  group.className = "live-group";
  group.label = label;
  for (const item of items) {
    const option = document.createElement("option");
    option.value = item.target;
    option.textContent = item.label;
    if (item.meta) option.dataset.meta = JSON.stringify(item.meta);
    group.appendChild(option);
  }
  document.getElementById("target").appendChild(group);
}

function applyConfiguration(payload) {
  const status = document.getElementById("status");
  status.textContent = payload.online ? `Live ${payload.daemonVersion ?? ""}`.trim() : "Daemon offline";
  status.className = `status ${payload.online ? "online" : "offline"}`;
  document.querySelectorAll(".live-group, #stale-option").forEach((node) => node.remove());

  if (kind === "toggle") {
    for (const group of payload.toggleGroups ?? []) fillGroup(group.id, group.label, group.items);
    fillGroup("monitor-group", "Monitor output (toggle independently)", payload.monitorOutputs);
    fillGroup("insert-group", "Audio effect", payload.inserts);
    fillGroup("chain-group", "Whole effect chain", payload.chains);
    fillGroup("profile-group", "Recall profile", payload.profiles);
  } else {
    for (const group of payload.levelGroups ?? []) fillGroup(group.id, group.label, group.items);
  }

  const select = document.getElementById("target");
  select.value = settings.target ?? "";
  if (settings.target && select.value !== settings.target) {
    const stale = document.createElement("option");
    stale.id = "stale-option";
    stale.value = settings.target;
    stale.textContent = `Unavailable: ${settings.target}`;
    select.appendChild(stale);
    select.value = settings.target;
  }
}

function updateBehaviour() {
  const behaviour = document.getElementById("behaviour");
  if (!behaviour) return;
  const row = document.getElementById("amount-row");
  const amount = document.getElementById("amount");
  const hint = document.getElementById("behaviour-hint");
  row.classList.toggle("hidden", behaviour.value === "mute");
  if (behaviour.value === "adjust") {
    amount.min = "-100"; amount.max = "100";
    if (amount.value === "" || !Number.isFinite(Number(amount.value))) amount.value = "10";
    hint.textContent = "Use a negative percentage for a volume-down key. The result is clamped safely.";
  } else if (behaviour.value === "set") {
    amount.min = "0"; amount.max = "100";
    if (amount.value === "" || !Number.isFinite(Number(amount.value))) amount.value = "50";
    hint.textContent = "Set the selected control directly to this percentage of its valid range.";
  } else {
    hint.textContent = "The key follows the live value and toggles mute without changing the saved level.";
  }
}

function connect(inPort, inPropertyInspectorUUID, inRegisterEvent, _inInfo, inActionInfo) {
  propertyInspectorUuid = inPropertyInspectorUUID;
  const actionInfo = JSON.parse(inActionInfo);
  actionContext = actionInfo.context;
  settings = actionInfo.payload?.settings ?? {};
  const target = document.getElementById("target");
  target.value = settings.target ?? "";
  target.addEventListener("change", save);
  const icon = document.getElementById("icon");
  if (icon) { icon.value = settings.icon ?? ""; icon.addEventListener("change", save); }
  const behaviour = document.getElementById("behaviour");
  if (behaviour) {
    behaviour.value = settings.behaviour ?? "mute";
    const amount = document.getElementById("amount");
    amount.value = Number.isFinite(Number(settings.amount)) ? settings.amount : "";
    behaviour.addEventListener("change", () => { updateBehaviour(); save(); });
    amount.addEventListener("change", save);
    updateBehaviour();
  }

  socket = new WebSocket(`ws://localhost:${inPort}`);
  socket.onopen = () => {
    send({ event: inRegisterEvent, uuid: propertyInspectorUuid });
    send({ event: "sendToPlugin", context: actionContext, payload: { request: "configuration" } });
  };
  socket.onmessage = (event) => {
    let message;
    try { message = JSON.parse(event.data); } catch { return; }
    if (message.event === "sendToPropertyInspector") applyConfiguration(message.payload ?? {});
  };
}

window.connectElgatoStreamDeckSocket = connect;
window.connectOpenActionSocket = connect;
