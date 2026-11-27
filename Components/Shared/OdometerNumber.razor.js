/* Lucian UX/UI Prompt #098 · MIT License (c) 2026 Seokwoooo · https://lucianlabs.dev/uxui-prompt/#098 */
const state = new WeakMap();

export function render(root, value, fresh) {
  if (!root) return;
  const str = String(value ?? "0");
  const prev = state.get(root) || "";
  const rm = window.matchMedia && window.matchMedia("(prefers-reduced-motion: reduce)").matches;
  const sameShape = !fresh && prev.replace(/\d/g, "0") === str.replace(/\d/g, "0");

  if (!sameShape) {
    root.innerHTML = "";
    str.split("").forEach((ch, i) => {
      const s = document.createElement("span");
      if (/\d/.test(ch)) {
        s.className = "u098-col";
        s.style.setProperty("--d", rm ? ch : "0");
        s.style.setProperty("--i", String(i));
        s.innerHTML = "<span>0</span><span>1</span><span>2</span><span>3</span><span>4</span><span>5</span><span>6</span><span>7</span><span>8</span><span>9</span>";
      } else {
        s.className = "u098-sym" + (ch === "%" ? " sm" : "");
        s.textContent = ch;
      }
      root.appendChild(s);
    });
    void root.offsetWidth;
  }

  const cols = root.querySelectorAll(".u098-col");
  let j = 0;
  str.split("").forEach((ch) => {
    if (/\d/.test(ch) && cols[j]) {
      cols[j++].style.setProperty("--d", ch);
    }
  });
  state.set(root, str);
}
