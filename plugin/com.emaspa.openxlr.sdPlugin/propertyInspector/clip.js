// The clip key's inspector: picks a clip from the daemon's library by id,
// showing its name, or the key that stops every clip, and optionally a press
// mode for this key only. The plugin sends the library again whenever it
// changes, so a rename or an import shows here while the inspector is open.
"use strict";

let ws = null, piUuid = null, actionContext = null;
// The last list from the plugin; known is false until the daemon's own
// library arrives, and while the daemon is offline.
let library = { known: false, clips: [], notice: null };

function connect(inPort, inPropertyInspectorUUID, inRegisterEvent, inInfo, inActionInfo) {
  piUuid = inPropertyInspectorUUID;
  const actionInfo = JSON.parse(inActionInfo);
  actionContext = actionInfo.context;
  const settings = actionInfo.payload?.settings ?? {};

  ws = new WebSocket("ws://localhost:" + inPort);
  ws.onopen = () => {
    ws.send(JSON.stringify({ event: inRegisterEvent, uuid: piUuid }));
    const clipSel = document.getElementById("clip");
    const modeSel = document.getElementById("mode");
    clipSel.value = settings.target ?? "";
    modeSel.value = settings.mode ?? "";
    showChoice(settings);
    const save = () => {
      // Before the first list the saved clip has no option yet and the
      // select reads empty: a change of mode then keeps the clip.
      settings.target = clipSel.value || (library.known ? "" : settings.target ?? "");
      settings.mode = modeSel.value;
      // The name goes with the key, so a clip deleted later still reads as itself.
      const name = clipSel.selectedOptions[0]?.dataset.name;
      if (name) settings.name = name;
      else if (!settings.target.startsWith("clip:")) delete settings.name;
      showChoice(settings);
      ws.send(JSON.stringify({ event: "setSettings", context: piUuid, payload: settings }));
    };
    clipSel.addEventListener("change", save);
    modeSel.addEventListener("change", save);
    ws.send(JSON.stringify({ event: "sendToPlugin", context: actionContext, payload: { request: "soundboard" } }));
  };

  ws.onmessage = (e) => {
    let m;
    try { m = JSON.parse(e.data); } catch { return; }
    if (m.event !== "sendToPropertyInspector" || !m.payload?.soundboard) return;
    library = m.payload.soundboard;
    fillClips(library, settings);
    showChoice(settings);
  };
}

// Replace the listed clips, keeping the key's choice selected. A clip that
// is no longer in the library stays selected as missing, under the name the
// key saved, and comes back as itself once a clip with its id returns. While
// the daemon is offline nothing is known to be missing.
function fillClips(board, settings) {
  const sel = document.getElementById("clip");
  const wanted = sel.value || settings.target || "";
  document.getElementById("clip-group")?.remove();
  document.getElementById("missing-clip")?.remove();
  const group = document.createElement("optgroup");
  group.id = "clip-group";
  group.label = "Clips";
  for (const clip of board.clips ?? []) {
    const opt = document.createElement("option");
    opt.value = "clip:" + clip.id;
    opt.textContent = clip.name;
    opt.dataset.name = clip.name;
    opt.dataset.mode = clip.mode ?? "";
    group.appendChild(opt);
  }
  if (group.children.length) sel.appendChild(group);
  if (wanted.startsWith("clip:") && !Array.from(sel.options).some(o => o.value === wanted)) {
    const missing = document.createElement("option");
    missing.id = "missing-clip";
    missing.value = wanted;
    const name = settings.name || wanted.slice(5);
    missing.textContent = board.known ? "Missing: " + name : name + " (unknown while OpenXLR is offline)";
    if (settings.name) missing.dataset.name = settings.name;
    missing.disabled = true;
    sel.appendChild(missing);
  }
  sel.value = wanted;
}

// The mode list names the clip's own mode, has no use on the stop key, and
// the notice says why the board or the clip cannot play.
function showChoice(settings) {
  const sel = document.getElementById("clip");
  const mode = document.getElementById("mode");
  const target = sel.value || settings.target || "";
  const option = Array.from(sel.options).find(o => o.value === target);
  const own = option?.dataset?.mode;
  mode.options[0].textContent = own ? "The clip's own mode (" + own + ")" : "The clip's own mode";
  mode.disabled = target === "stopClips";
  const clip = (library.clips ?? []).find(c => "clip:" + c.id === target);
  const lines = [library.notice, clip?.error].filter(Boolean);
  if (target.startsWith("clip:") && !clip && library.known && library.notice === null)
    lines.push("This clip is no longer in the soundboard. Choose another.");
  document.getElementById("notice").textContent = lines.join(" ");
}

window.connectElgatoStreamDeckSocket = connect;
window.connectOpenActionSocket = connect;
