(() => {
  // contador da sessão no celular de exemplo
  const tick = document.getElementById("tick");
  let s = 2 * 3600 + 14 * 60 + 7;
  const pad = (n) => String(n).padStart(2, "0");
  const draw = () => { tick.textContent = `${Math.floor(s / 3600)}h ${pad(Math.floor(s % 3600 / 60))}m ${pad(s % 60)}s`; };
  draw();
  setInterval(() => { s += 1; draw(); }, 1000);

  // abas
  const tabs = [["tab-srv", "p-srv"], ["tab-app", "p-app"]];
  const show = (id) => {
    for (const [t, p] of tabs) {
      const on = t === id;
      document.getElementById(t).setAttribute("aria-selected", String(on));
      document.getElementById(p).hidden = !on;
    }
  };
  for (const [t] of tabs) document.getElementById(t).addEventListener("click", () => show(t));

  // copiar comandos
  document.querySelectorAll(".code").forEach((box) => {
    const btn = box.querySelector(".copy");
    const code = box.querySelector("code");
    btn.addEventListener("click", async () => {
      try {
        await navigator.clipboard.writeText(code.textContent);
        btn.textContent = "Copiado";
      } catch {
        const range = document.createRange();
        range.selectNodeContents(code);
        const sel = getSelection();
        sel.removeAllRanges();
        sel.addRange(range);
        btn.textContent = "Selecionado";
      }
      setTimeout(() => { btn.textContent = "Copiar"; }, 1600);
    });
  });
})();
