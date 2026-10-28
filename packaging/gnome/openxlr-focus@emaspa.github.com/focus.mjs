// No focus history, window titles, application names or polling.
export const interfaceXml = `<node><interface name="org.openxlr.Focus">
  <method name="GetFocusedProcess"><arg type="u" direction="out"/></method>
</interface></node>`;

export function focusedProcess(display, sessionMode, overview) {
  if (sessionMode.isLocked || sessionMode.isGreeter || overview.visible
      || (sessionMode.currentMode !== 'user' && sessionMode.parentMode !== 'user')) return 0;
  const window = display.focus_window;
  if (!window || window.minimized || !window.has_focus()) return 0;
  const pid = window.get_pid();
  return Number.isSafeInteger(pid) && pid > 0 && pid <= 2147483647 ? pid : 0;
}
