/**
 * Shell HTML de la vista "Endpoints" embebida en la barra lateral (webview):
 * campo de FILTRO FIJO arriba del árbol (la razón de ser de esta vista), árbol
 * de grupos/endpoints en HTML con chips de verbo, y estados loading/error/vacío
 * con acciones. Los datos llegan por postMessage ('data' / 'status'); el
 * filtrado, agrupado y expansión son locales al webview (instantáneos, sin
 * round-trip). Módulo PURO (sin vscode) para poder previsualizarlo en navegador
 * con window.__SEED.
 */

export interface EndpointItem {
    controller: string;
    controllerName: string;
    verb: string;
    route: string;
    method: string;
    file: string | null;
    line: number;
}

export interface RenderEndpointsOptions {
    nonce: string;
    /** datos semilla para la preview standalone (vacío en producción) */
    seed?: EndpointItem[];
}

export function renderEndpointsShell(opts: RenderEndpointsOptions): string {
    return shell(opts.nonce, opts.seed ?? []);
}

function shell(nonce: string, seed: EndpointItem[]): string {
    return `<!DOCTYPE html>
<html lang="es">
<head>
<meta charset="utf-8">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; script-src 'nonce-${nonce}'; img-src data:;">
<style>
  html,body{margin:0;padding:0;background:var(--vscode-sideBar-background,#181818);color:var(--vscode-sideBar-foreground,#ccc);
    font-family:"Segoe WPC","Segoe UI",sans-serif;font-size:13px;height:100%}
  body{display:flex;flex-direction:column}
  .filterbar{padding:6px 8px 4px;display:flex;gap:5px;align-items:center;border-bottom:1px solid var(--vscode-panel-border,#2b2b2b);flex-shrink:0}
  .filterbar input{flex:1;background:var(--vscode-input-background,#313131);color:var(--vscode-input-foreground,#ccc);
    border:1px solid var(--vscode-input-border,transparent);border-radius:3px;padding:4px 8px;font-size:12px;outline:none;font-family:inherit;min-width:0}
  .filterbar input:focus{border-color:var(--vscode-focusBorder,#0078d4)}
  .filterbar .mini{background:transparent;color:var(--vscode-sideBar-foreground,#bbb);border:none;border-radius:3px;
    font-size:12px;padding:2px 6px;cursor:pointer;flex-shrink:0}
  .filterbar .mini:hover{background:var(--vscode-toolbar-hoverBackground,#2a2d2e)}
  .count{padding:2px 10px 4px;color:var(--vscode-descriptionForeground,#8a8a8a);font-size:10.5px;flex-shrink:0}
  .tree{flex:1;overflow-y:auto;padding-bottom:6px}
  .grp-h{display:flex;align-items:center;gap:5px;padding:3px 10px;cursor:pointer;white-space:nowrap;overflow:hidden}
  .grp-h:hover{background:var(--vscode-list-hoverBackground,#2a2d2e)}
  .grp-h .chev{font-size:9px;color:var(--vscode-descriptionForeground,#8a8a8a);width:10px;transition:transform .1s;flex-shrink:0}
  .grp.closed .chev{transform:rotate(-90deg)}
  .grp-h .name{font-weight:600;overflow:hidden;text-overflow:ellipsis}
  .grp-h:hover .name{text-decoration:underline dotted}
  .grp-h .cnt{margin-left:auto;color:var(--vscode-descriptionForeground,#777);font-size:11px}
  .grp.closed .kids{display:none}
  .ep{display:flex;align-items:center;gap:7px;padding:2px 10px 2px 27px;cursor:pointer;white-space:nowrap;overflow:hidden}
  .ep:hover{background:var(--vscode-list-hoverBackground,#2a2d2e)}
  .verb{font-size:9px;font-weight:800;color:#fff;border-radius:3px;padding:1px 5px;width:34px;text-align:center;flex-shrink:0;letter-spacing:.03em}
  .v-GET{background:#61affe}.v-POST{background:#49cc90}.v-PUT{background:#fca130}
  .v-DELETE{background:#f93e3e}.v-PATCH{background:#50e3c2}
  .route{font-family:Consolas,monospace;font-size:12px;overflow:hidden;text-overflow:ellipsis}
  .mname{color:var(--vscode-descriptionForeground,#8a8a8a);font-size:11px;overflow:hidden;text-overflow:ellipsis}
  .ep .pop{margin-left:auto;visibility:hidden;color:var(--vscode-descriptionForeground,#aaa);border:none;background:transparent;
    border-radius:3px;cursor:pointer;flex-shrink:0;padding:1px 5px}
  .ep:hover .pop{visibility:visible}
  .ep .pop:hover{background:var(--vscode-toolbar-hoverBackground,#2a2d2e)}
  .state{padding:14px 16px;color:var(--vscode-descriptionForeground,#9a9a9a);font-size:12px;line-height:1.6}
  .state.err{color:var(--vscode-errorForeground,#f48771)}
  .state a{color:var(--vscode-textLink-foreground,#4daafc);cursor:pointer;text-decoration:none}
  .empty-filter{padding:14px 16px;color:var(--vscode-descriptionForeground,#9a9a9a);font-size:12px}
  .empty-filter a{color:var(--vscode-textLink-foreground,#4daafc);cursor:pointer}
</style>
</head>
<body>
  <div class="filterbar">
    <input id="filter" type="text" placeholder="Filtrar: controlador, ruta, método, verbo…" spellcheck="false">
    <button class="mini" id="expandAll" data-action="expandAll" title="Expandir todo">▾▾</button>
    <button class="mini" id="collapseAll" data-action="collapseAll" title="Contraer todo">▸▸</button>
  </div>
  <div class="count" id="count"></div>
  <div class="tree" id="tree"></div>
<script nonce="${nonce}">
(function(){
  "use strict";
  var state = { data: ${jsonForScript(JSON.stringify(seed))}, status: { kind: ${seed.length ? '"ok"' : '"loading"'} }, filter: "", expanded: {} };
  var vsc = null;
  try { vsc = acquireVsCodeApi(); } catch (e) { /* preview standalone */ }

  function esc(s){ return String(s == null ? "" : s).replace(/&/g,"&amp;").replace(/</g,"&lt;").replace(/>/g,"&gt;").replace(/"/g,"&quot;"); }

  function keyOf(ep) {
    return /^[A-Z]+ \\//.test(ep.controller) ? ep.controller : ep.controllerName;
  }

  function groups() {
    var all = {};
    (state.data || []).forEach(function(ep){
      var key = keyOf(ep);
      (all[key] = all[key] || []).push(ep);
    });
    var q = state.filter.toLowerCase();
    var out = [];
    Object.keys(all).sort(function(a,b){ return a.localeCompare(b); }).forEach(function(key){
      if (!q || key.toLowerCase().indexOf(q) >= 0) { out.push({ key: key, endpoints: all[key] }); return; }
      var matched = all[key].filter(function(ep){
        return ep.route.toLowerCase().indexOf(q) >= 0 || (ep.method || "").toLowerCase().indexOf(q) >= 0 || ep.verb.toLowerCase() === q;
      });
      if (matched.length) out.push({ key: key, endpoints: matched });
    });
    return out;
  }

  function render() {
    var tree = document.getElementById("tree");
    var count = document.getElementById("count");
    if (state.status.kind === "loading") {
      count.textContent = "";
      tree.innerHTML = '<div class="state">Escaneando solución con SharpGraph…</div>';
      return;
    }
    if (state.status.kind === "error") {
      count.textContent = "";
      tree.innerHTML = '<div class="state err">⚠ ' + esc(state.status.message) +
        (state.status.hint ? "<br>" + esc(state.status.hint) : "") +
        '<br><br><a data-cmd="' + (state.status.hint ? "configure" : "scan") + '">' +
        (state.status.hint ? "Configurar ruta del motor" : "Indexar / actualizar repo") + '</a></div>';
      return;
    }
    if (!(state.data || []).length) {
      count.textContent = "";
      tree.innerHTML = '<div class="state">Sin endpoints indexados todavía.<br><br><a data-cmd="scan">Indexar / actualizar este repo</a></div>';
      return;
    }
    var gs = groups();
    if (!gs.length) {
      count.textContent = "";
      tree.innerHTML = '<div class="empty-filter">Sin coincidencias para <b>' + esc(state.filter) + '</b>. <a data-action="clearFilter" id="clearF">Quitar filtro</a></div>';
      return;
    }
    var visible = 0;
    var html = "";
    gs.forEach(function(g){
      var open = state.expanded[g.key] !== false;
      visible += g.endpoints.length;
      html += '<div class="grp' + (open ? "" : " closed") + '" data-k="' + esc(g.key) + '">' +
        '<div class="grp-h"><span class="chev">▼</span><span class="name">' + esc(g.key) + '</span><span class="cnt">' + g.endpoints.length + '</span></div>' +
        '<div class="kids">';
      g.endpoints.forEach(function(ep){
        html += '<div class="ep" data-k="' + esc(ep.controller + "|" + ep.verb + "|" + ep.route) + '">' +
          '<span class="verb v-' + esc(ep.verb) + '">' + esc(ep.verb) + '</span>' +
          '<span class="route">' + esc(ep.route || "/") + '</span>' +
          '<span class="mname">' + esc(ep.method || "") + '</span>' +
          '<button class="pop" title="Abrir diagrama en el editor">⤢</button></div>';
      });
      html += '</div></div>';
    });
    tree.innerHTML = html;
    count.textContent = visible === (state.data || []).length
      ? (state.data || []).length + " endpoints"
      : visible + " de " + (state.data || []).length + " endpoints (filtro)";
  }

  function epFromRow(row) {
    var key = row.getAttribute("data-k");
    for (var i = 0; i < (state.data || []).length; i++) {
      var ep = state.data[i];
      if (ep.controller + "|" + ep.verb + "|" + ep.route === key) return ep;
    }
    return null;
  }

  // ── delegación ÚNICA de clicks en document: inmune a re-render y a órdenes
  // de registro (los botones/estados se resuelven por data-*, no por listener) ──
  document.addEventListener("click", function(ev) {
    var t = ev.target;
    if (!t || !t.closest) return;
    var action = t.closest("[data-action]");
    if (action) {
      var kind = action.getAttribute("data-action");
      if (kind === "expandAll" || kind === "collapseAll") {
        var open = kind === "expandAll";
        groups().forEach(function(g){ state.expanded[g.key] = open; });
        render();
      } else if (kind === "clearFilter") {
        input.value = ""; state.filter = ""; render();
      }
      return;
    }
    var cmdEl = t.closest("[data-cmd]");
    if (cmdEl) { if (vsc) vsc.postMessage({ type: "cmd", cmd: cmdEl.getAttribute("data-cmd") }); return; }
    var pop = t.closest(".pop");
    if (pop) {
      var epPop = epFromRow(pop.closest(".ep"));
      if (epPop && vsc) vsc.postMessage({ type: "openInEditor", ep: epPop });
      return;
    }
    var head = t.closest(".grp-h");
    if (head) {
      var grp = head.parentElement;
      var key = grp.getAttribute("data-k");
      // clic en el CHEVRON: plegar/desplegar (y refrescar el grupo al expandir)
      if (t.closest(".chev")) {
        var wasClosed = grp.classList.contains("closed");
        var nowClosed = grp.classList.toggle("closed");
        state.expanded[key] = !nowClosed;
        if (wasClosed && !nowClosed && vsc) {
          var cnt = head.querySelector(".cnt");
          if (cnt) cnt.textContent = "…";
          vsc.postMessage({ type: "refreshGroup", key: key });
        }
        return;
      }
      // clic en el NOMBRE del controlador: flujo completo del controlador
      if (vsc) vsc.postMessage({ type: "selectController", name: key });
      return;
    }
    var row = t.closest(".ep");
    if (row) {
      var ep = epFromRow(row);
      if (ep && vsc) vsc.postMessage({ type: "select", ep: ep });
    }
  });

  var filterTimer;
  var input = document.getElementById("filter");
  input.addEventListener("input", function() {
    clearTimeout(filterTimer);
    filterTimer = setTimeout(function(){ state.filter = input.value.trim(); render(); }, 120);
  });

  window.addEventListener("message", function(e) {
    var m = e.data;
    if (m.type === "data") {
      var sc = document.getElementById("tree").scrollTop;
      state.data = m.items || [];
      state.status = { kind: "ok" };
      render();
      document.getElementById("tree").scrollTop = sc;
    } else if (m.type === "groupData") {
      // sustituye SOLO los endpoints de ese grupo (refresco al expandir)
      var others = (state.data || []).filter(function(ep){ return keyOf(ep) !== m.key; });
      state.data = others.concat(m.items || []);
      var cntEl = document.querySelector('.grp[data-k="' + m.key.replace(/"/g, '\\"') + '"] .grp-h .cnt');
      if (cntEl) cntEl.textContent = (m.items || []).length;
    } else if (m.type === "status") { state.status = m.status; if (m.status.kind !== "ok") render(); }
  });

  // telemetría: cualquier error JS del webview llega al host (canal de salida)
  window.addEventListener("error", function(e) {
    if (vsc) vsc.postMessage({ type: "webviewError", message: String((e && e.error && e.error.stack) || e.message || e) });
  });

  try {
    render();
  } catch (err) {
    if (vsc) vsc.postMessage({ type: "webviewError", message: String((err && err.stack) || err) });
    document.getElementById("tree").innerHTML = '<div class="state err">Error en la vista: ' + esc(String(err)) + '</div>';
  }
})();
</script>
</body>
</html>`;
}

function jsonForScript(s: string): string {
    return s.replace(/</g, "\\u003c");
}
