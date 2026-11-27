/* Lucian UX/UI Prompt #015 · MIT License (c) 2026 Seokwoooo · https://lucianlabs.dev/uxui-prompt/#015 */
export function syncFrame(root, instant) {
  if (!root) return;
  const frame = root.querySelector(".u015-frame");
  const active = root.querySelector('.u015-opt[aria-checked="true"]');
  if (!frame || !active) return;
  if (instant) frame.style.transition = "none";
  frame.style.top = active.offsetTop + "px";
  frame.style.height = active.offsetHeight + "px";
  if (instant) {
    void frame.offsetWidth;
    frame.style.transition = "";
  }
}
