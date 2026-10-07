/**
 * HTML del webview del diagrama. Módulo PURO (sin importar vscode) para poder
 * reutilizarlo desde tools/preview.js y verificar el render en navegador real.
 *
 * Layout: árbol jerárquico top-down. El motor (endpoint_flow) garantiza que las
 * aristas no-back forman un árbol de expansión BFS (la primera arista que
 * descubre un nodo lo cuelga de su padre; las segundas llegan marcadas
 * back=true), así que un tidy-tree clásico (hojas en slots secuenciales, padres
 * centrados sobre sus hijos) cubre el 100% del grafo; los back-edges se dibujan
 * rodeando por el lateral, sin re-expandir.
 */

export interface FlowNode {
    id: string;
    kind: string;
    name: string;
    fqn?: string | null;
    file?: string | null;
    line?: number;
    infra?: boolean;
}

export interface FlowEdge {
    from: string;
    to: string;
    relation: string;
    line?: number;
    back?: boolean;
}

export interface FlowData {
    endpoint?: string;
    root?: string;
    nodeCount?: number;
    edgeCount?: number;
    backEdges?: number;
    omitted?: number;
    truncated?: boolean;
    nodes: FlowNode[];
    edges: FlowEdge[];
    mermaid?: string;
    error?: string;
}

export interface RenderOptions {
    nonce: string;
    includeInfra: boolean;
    /** muestra el botón "Abrir en editor" (modo embebido en la barra lateral) */
    openInEditor?: boolean;
    /** nivel de profundidad inicial del slider (re-consulta por postMessage) */
    depth: number;
    /** contratos (DTOs) visibles inicialmente */
    includeDtos: boolean;
    /** título completo del flujo (verb + ruta + controller.metodo); si no,
     * el header usa data.endpoint */
    title?: string;
}

// geometría del layout (px de diseño; el canvas se escala para caber)
const NODE_W = 196;
const NODE_H = 54;
const GAP_X = 30;
const GAP_Y = 82;
/** columnas máximas por nivel: lo que excede ENVUELVE a filas extra (vertical) */
const MAX_COLS = 4;

const KIND_CLASS: Record<string, string> = {
    endpoint: "k-endpoint",
    controller: "k-controller",
    command: "k-command",
    query: "k-command",
    handler: "k-handler",
    interface: "k-interface",
    implementation: "k-impl",
    validator: "k-validator",
    dto: "k-dto",
    class: "k-class",
    external: "k-infra",
    infra: "k-infra",
};

const EDGE_COLOR: Record<string, string> = {
    call: "#569cd6",
    sends: "#c586c0",
    "handled-by": "#c586c0",
    "di-bound": "#6a9955",
    inherits: "#808080",
    implements: "#808080",
};

function esc(s: string): string {
    return s.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;")
        .replace(/"/g, "&quot;").replace(/'/g, "&#39;");
}

function jsonForScript(s: string): string {
    return s.replace(/</g, "\\u003c").replace(/\u2028/g, "\\u2028").replace(/\u2029/g, "\\u2029");
}

interface Placed {
    x: number;
    y: number;
}

/**
 * Layout por NIVELES EN CUADRÍCULA: cada nivel BFS baja una "banda" y, si tiene
 * más de MAX_COLS nodos, ENVUELVE en filas extra — el grafo es siempre más alto
 * que ancho (vertical), aunque un nivel tenga 15 hermanos. Los niveles estrechos
 * se centran (columna del prototipo). Los huérfanos (infra oculta) van a una
 * banda final.
 */
function layout(data: FlowData): { pos: Map<string, Placed>; width: number; height: number } {
    const root = data.root ?? data.nodes[0]?.id;
    const byId = new Map(data.nodes.map((n) => [n.id, n]));
    const levelOf = new Map<string, number>();

    // niveles por BFS sobre el árbol de expansión (primera arista no-back)
    const queue = root !== undefined ? [root] : [];
    if (root !== undefined) {
        levelOf.set(root, 0);
    }
    while (queue.length > 0) {
        const cur = queue.shift()!;
        const lvl = levelOf.get(cur) ?? 0;
        for (const e of data.edges) {
            if (e.back || e.from !== cur || byId.get(e.to)?.infra) {
                continue;
            }
            if (!levelOf.has(e.to)) {
                levelOf.set(e.to, lvl + 1);
                queue.push(e.to);
            }
        }
    }

    // nodos por nivel en orden de descubrimiento (los hermanos quedan juntos);
    // los que quedan fuera del árbol (infra oculta u huérfanos) van a una banda final
    const maxLevel = levelOf.size > 0 ? Math.max(...levelOf.values()) : 0;
    const byLevel = new Map<number, FlowNode[]>();
    for (const n of data.nodes) {
        const l = levelOf.get(n.id) ?? maxLevel + 1;
        if (!byLevel.has(l)) {
            byLevel.set(l, []);
        }
        byLevel.get(l)!.push(n);
    }

    const pos = new Map<string, Placed>();
    const colW = NODE_W + GAP_X;
    const rowH = NODE_H + GAP_Y;
    let y = 0;
    let maxColsUsed = 1;
    for (const l of [...byLevel.keys()].sort((a, b) => a - b)) {
        const list = byLevel.get(l)!;
        const cols = Math.min(list.length, MAX_COLS);
        maxColsUsed = Math.max(maxColsUsed, cols);
        // centra los niveles estrechos dentro del ancho máximo
        const offset = ((MAX_COLS - cols) * colW) / 2;
        list.forEach((n, i) => {
            pos.set(n.id, {
                x: offset + (i % MAX_COLS) * colW,
                y: y + Math.floor(i / MAX_COLS) * rowH,
            });
        });
        y += Math.ceil(list.length / MAX_COLS) * rowH;
    }

    return {
        pos,
        width: Math.max(maxColsUsed * colW - GAP_X + 4, NODE_W),
        height: y + 4,
    };
}

function edgePath(a: Placed, b: Placed, back: boolean): string {
    const ax = a.x + NODE_W / 2;
    const ay = a.y + NODE_H;
    if (!back) {
        const bx = b.x + NODE_W / 2;
        const by = b.y - 2;
        const dy = (by - ay) * 0.45;
        return `M${ax},${ay} C${ax},${ay + dy} ${bx},${by - dy} ${bx},${by}`;
    }
    // back-edge: sale por abajo, rodea y entra por el lateral del destino
    const fromRight = a.x >= b.x;
    const bx = fromRight ? b.x + NODE_W + 2 : b.x - 2;
    const by = b.y + NODE_H / 2;
    const sx = a.x + (fromRight ? NODE_W * 0.8 : NODE_W * 0.2);
    const mx = Math.max(ax, bx) + 46;
    return `M${sx},${ay} C${sx},${ay + 44} ${mx},${by} ${bx},${by}`;
}

export function renderFlowHtml(data: FlowData, opts: RenderOptions): string {
    if (data.error) {
        return `<!DOCTYPE html><html><head><meta charset="utf-8"><style>
            body{background:#1f1f1f;color:#ccc;font-family:"Segoe UI",sans-serif;display:grid;place-items:center;height:100vh}
            .err{border:1px solid #5a1d1d;background:#2a1515;padding:18px 22px;border-radius:8px;max-width:520px;line-height:1.5}
            code{color:#4ec9b0}</style></head>
            <body><div class="err"><b>SharpGraph Flow</b><br>${esc(data.error)}<br><br>
            Formato: <code>"POST /api/orders"</code>, <code>"/api/orders"</code> o <code>OrdersController</code>.</div></body></html>`;
    }

    const { pos, width, height } = layout(data);
    const byId = new Map(data.nodes.map((n) => [n.id, n]));

    // ── SVG de aristas ──
    const edgesSvg = data.edges.map((e) => {
        const a = pos.get(e.from);
        const b = pos.get(e.to);
        if (!a || !b) {
            return "";
        }
        const color = e.back ? "#f14c4c" : EDGE_COLOR[e.relation] ?? "#7a8a99";
        const dashed = e.back || ["sends", "handled-by", "di-bound"].includes(e.relation);
        const label = e.back ? "↺" : ["sends", "handled-by", "di-bound"].includes(e.relation) ? e.relation : "";
        const marker = e.back ? "arr-back" : `arr-${EDGE_COLOR[e.relation] ? e.relation : "other"}`;
        const mx = (a.x + b.x) / 2 + (e.back ? 46 : 0);
        const my = (a.y + b.y) / 2 + (e.back ? 20 : NODE_H / 2 + 18);
        return `<g class="edge" data-a="${esc(e.from)}" data-b="${esc(e.to)}" data-infra="${byId.get(e.from)?.infra || byId.get(e.to)?.infra ? 1 : 0}">
            <path d="${edgePath(a, b, !!e.back)}" fill="none" stroke="${color}" stroke-width="1.5"
                ${dashed ? 'stroke-dasharray="5,4"' : ""} marker-end="url(#${marker})"></path>
            ${label ? `<text class="edge-label" x="${mx}" y="${my}" text-anchor="middle" fill="${color}">${esc(label)}</text>` : ""}
        </g>`;
    }).join("\n");

    // ── nodos ──
    const nodesHtml = data.nodes.map((n) => {
        const p = pos.get(n.id);
        if (!p) {
            return "";
        }
        const cls = KIND_CLASS[n.kind] ?? "k-class";
        const fileLine = n.file ? `${shortFile(n.file)} : ${n.line ?? 0}` : "(externo)";
        const title = `${n.fqn ?? n.name}\n${n.file ? `${n.file}:${n.line} — clic para abrir` : "sin fichero local"}`;
        return `<div class="node ${cls}" id="node-${esc(n.id)}" data-id="${esc(n.id)}"
            data-infra="${n.infra ? 1 : 0}" data-file="${esc(n.file ?? "")}" data-line="${n.line ?? 0}"
            style="left:${Math.round(p.x)}px;top:${Math.round(p.y)}px" title="${esc(title)}">
            <div class="kind">${esc(n.kind.toUpperCase())}</div>
            <div class="name">${esc(n.name)}</div>
            <div class="file">${esc(fileLine)}</div>
        </div>`;
    }).join("\n");

    const defs = ["call", "sends", "handled-by", "di-bound", "inherits", "implements", "other", "back"].map((k) => {
        const c = k === "back" ? "#f14c4c" : EDGE_COLOR[k] ?? "#7a8a99";
        return `<marker id="arr-${k}${k === "back" ? "" : ""}" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="7" markerHeight="7" orient="auto-start-reverse"><path d="M0,0 L10,5 L0,10 z" fill="${c}"></path></marker>`;
    }).join("");

    const stats = `${data.nodes.length} nodos · ${data.edges.length} aristas · ${data.backEdges ?? 0} back-edge(s)` +
        (data.truncated ? ` · TRUNCADO (+${data.omitted} sin dibujar: sube sharpgraphFlow.maxNodes)` : "");

    const mermaid = data.mermaid ?? "";

    return `<!DOCTYPE html>
<html lang="es">
<head>
<meta charset="utf-8">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; script-src 'nonce-${opts.nonce}'; img-src data:;">
<style>
  html,body{margin:0;padding:0;background:var(--vscode-editor-background,#1f1f1f);color:var(--vscode-editor-foreground,#ccc);
    font-family:"Segoe WPC","Segoe UI",sans-serif;font-size:13px;height:100%}
  .wrap{display:flex;flex-direction:column;height:100vh}
  .hdr{display:flex;align-items:center;gap:10px;padding:10px 14px 4px;flex-wrap:wrap}
  .hdr .t{font-weight:600}
  .hdr .mono{font-family:Consolas,monospace}
  .hdr .stats{color:var(--vscode-descriptionForeground,#9a9a9a);font-size:11px}
  .bar{display:flex;align-items:center;gap:14px;padding:2px 14px 9px;border-bottom:1px solid var(--vscode-panel-border,#2b2b2b);flex-wrap:wrap}
  .legend{display:flex;gap:9px;flex-wrap:wrap;font-size:10.5px;color:var(--vscode-descriptionForeground,#9a9a9a)}
  .chip{display:inline-flex;align-items:center;gap:4px}
  .chip i{width:10px;height:10px;border-radius:3px;border:1.5px solid;display:inline-block}
  label.tgl{display:flex;gap:5px;align-items:center;font-size:11.5px;cursor:pointer}
  button{background:var(--vscode-button-secondaryBackground,#313131);color:var(--vscode-button-secondaryForeground,#ccc);
    border:1px solid var(--vscode-panel-border,#2b2b2b);border-radius:3px;font-size:11px;padding:3px 9px;cursor:pointer}
  button:hover{background:var(--vscode-button-secondaryHoverBackground,#3c3c3c)}
  #mmd{display:none;margin:8px 14px;padding:10px;background:var(--vscode-textCodeBlock-background,#1b1b1b);
    border:1px solid var(--vscode-panel-border,#2b2b2b);border-radius:5px;overflow:auto;max-height:40vh;
    font-family:Consolas,monospace;font-size:11.5px;white-space:pre}
  .canvasOuter{flex:1;overflow:auto;position:relative;background:var(--vscode-editor-background,#1f1f1f);cursor:grab}
  .canvasOuter:active{cursor:grabbing}
  #sizer{position:relative;margin:0 auto}
  #canvas{position:absolute;left:0;top:0;transform-origin:0 0}
  .zoom{display:flex;gap:4px;align-items:center;margin-left:auto}
  .zoom button{min-width:28px;padding:3px 7px}
  #pct{font-size:11px}
  .lvl{font-size:11.5px;display:flex;align-items:center;gap:5px;color:var(--vscode-descriptionForeground,#9a9a9a)}
  .lvl b{color:var(--vscode-sideBar-foreground,#ccc);min-width:12px;text-align:center}
  input[type=range]{accent-color:var(--vscode-focusBorder,#0078d4);width:90px;cursor:pointer}
  svg.edges{position:absolute;left:0;top:0;pointer-events:none}
  .node{position:absolute;border-radius:8px;padding:5px 11px 6px;cursor:pointer;border:1.5px solid;
    background:rgba(30,30,30,.94);box-sizing:border-box;height:${NODE_H}px;width:${NODE_W}px;overflow:hidden}
  .node:hover{box-shadow:0 0 0 2px var(--vscode-focusBorder,#0078d4),0 4px 14px rgba(0,0,0,.5);z-index:30}
  .node .kind{font-size:8.5px;letter-spacing:.1em;color:var(--vscode-descriptionForeground,#8a8a8a);font-weight:700}
  .node .name{font-family:Consolas,monospace;font-size:12.5px;font-weight:600;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
  .node .file{font-size:9.5px;color:var(--vscode-descriptionForeground,#8a8a8a);white-space:nowrap;overflow:hidden}
  .k-endpoint{border-color:#61affe;background:rgba(97,175,238,.10)} .k-endpoint .kind{color:#79b8ff}
  .k-controller{border-color:#569cd6;background:rgba(86,156,214,.08)} .k-controller .kind{color:#569cd6}
  .k-command{border-color:#c586c0;background:rgba(197,134,192,.08)} .k-command .kind{color:#c586c0}
  .k-handler{border-color:#d7ba7d;background:rgba(215,186,125,.08)} .k-handler .kind{color:#d7ba7d}
  .k-interface{border-color:#4ec9b0;background:rgba(78,201,176,.08)} .k-interface .kind{color:#4ec9b0}
  .k-impl{border-color:#6a9955;background:rgba(106,153,85,.08)} .k-impl .kind{color:#6a9955}
  .k-validator{border-color:#9cdcfe;background:rgba(156,220,254,.07)} .k-validator .kind{color:#9cdcfe}
  .k-dto{border-color:#4fc1ff;background:rgba(79,193,255,.08)} .k-dto .kind{color:#4fc1ff}
  .k-class{border-color:#9aa4af;background:rgba(154,164,175,.07)} .k-class .kind{color:#9aa4af}
  .k-infra{border-color:#6e7a8a;border-style:dashed;background:rgba(110,122,138,.06)}
  .k-infra .name{color:#9aa4af} .k-infra .kind{color:#6e7a8a}
  body.no-infra .node[data-infra="1"]{display:none}
  body.no-infra .edge[data-infra="1"]{display:none}
  .edge-label{font-size:10px;paint-order:stroke;stroke:var(--vscode-editor-background,#1f1f1f);stroke-width:4px}
</style>
</head>
<body class="${opts.includeInfra ? "" : "no-infra"}">
<div class="wrap">
  <div class="hdr">
    <span class="t">⚡ Endpoint Flow — <span class="mono">${esc(opts.title ?? data.endpoint ?? "")}</span></span>
    <span class="stats">${esc(stats)}</span>
  </div>
  <div class="bar">
    <div class="legend">
      <span class="chip"><i style="border-color:#61affe"></i>endpoint</span>
      <span class="chip"><i style="border-color:#569cd6"></i>controller</span>
      <span class="chip"><i style="border-color:#c586c0"></i>command / query</span>
      <span class="chip"><i style="border-color:#d7ba7d"></i>handler</span>
      <span class="chip"><i style="border-color:#4ec9b0"></i>interfaz (DI)</span>
      <span class="chip"><i style="border-color:#6a9955"></i>implementación</span>
      <span class="chip"><i style="border-color:#4fc1ff"></i>dto</span>
      <span class="chip"><i style="border-color:#6e7a8a;border-style:dashed"></i>infra / externo</span>
      <span class="chip"><i style="border-color:#f14c4c;border-style:dashed"></i>↺ back-edge</span>
    </div>
    <label class="tgl"><input type="checkbox" id="infraChk" ${opts.includeInfra ? "checked" : ""}> infraestructura</label>
    <span class="lvl">Nivel
      <input type="range" id="depth" min="1" max="8" step="1" value="${opts.depth}" title="1 = mediator · 2 = dependencias 1er nivel · 3 = dependencias de las dependencias · con contratos si está marcado">
      <b id="depthVal">${opts.depth}</b>
    </span>
    <label class="tgl"><input type="checkbox" id="dtosChk" ${opts.includeDtos ? "checked" : ""} title="Nivel 4: contratos de entrada/salida (DTOs)"> contratos</label>
    ${mermaid ? '<button id="mmdBtn" title="Copiar/ver el Mermaid equivalente">Ver Mermaid</button>' : ""}
    ${opts.openInEditor ? '<button id="popOut" title="Abrir el diagrama grande en el editor">⤢ Editor</button>' : ""}
    <div class="zoom">
      <button id="zOut" title="Alejar (también con la ruleta del ratón)">−</button>
      <button id="zReset" title="Tamaño real"><span id="pct">100%</span></button>
      <button id="zIn" title="Acercar (también con la ruleta del ratón)">+</button>
      <button id="zFit" title="Ajustar al panel">Ajustar</button>
    </div>
  </div>
  <pre id="mmd">${esc(mermaid)}</pre>
  <div class="canvasOuter" id="outer">
    <div id="sizer">
      <div id="canvas">
        <svg class="edges" width="${width}" height="${height}"><defs>${defs}</defs>${edgesSvg}</svg>
        ${nodesHtml}
      </div>
    </div>
  </div>
</div>
<script nonce="${opts.nonce}">
(function(){
  "use strict";
  var DATA = ${jsonForScript(JSON.stringify({ width: width, height: height }))};
  var vsc = null;
  try { vsc = acquireVsCodeApi(); } catch (e) { /* preview standalone */ }
  var outer = document.getElementById("outer");
  var canvas = document.getElementById("canvas");
  var sizer = document.getElementById("sizer");
  var scale = 1;
  var MIN = 0.2, MAX = 3;

  function apply() {
    canvas.style.transform = "scale(" + scale + ")";
    sizer.style.width = Math.round(DATA.width * scale) + "px";
    sizer.style.height = Math.round(DATA.height * scale) + "px";
    var pct = document.getElementById("pct");
    if (pct) { pct.textContent = Math.round(scale * 100) + "%"; }
  }
  function setScale(ns, cx, cy) {
    ns = Math.max(MIN, Math.min(MAX, ns));
    var rect = outer.getBoundingClientRect();
    if (cx === undefined) { cx = rect.width / 2; }
    if (cy === undefined) { cy = rect.height / 2; }
    var px = (outer.scrollLeft + cx) / scale;   // punto anclado, en coords de diseño
    var py = (outer.scrollTop + cy) / scale;
    scale = ns;
    apply();
    outer.scrollLeft = px * scale - cx;
    outer.scrollTop = py * scale - cy;
  }
  function fitAll() {
    scale = Math.min(1, outer.clientWidth / DATA.width, outer.clientHeight / DATA.height);
    apply();
    outer.scrollLeft = Math.max(0, (DATA.width * scale - outer.clientWidth) / 2);
    outer.scrollTop = 0; // el flujo se lee de arriba a abajo
  }
  // ruleta = zoom anclado al cursor; Shift+ruleta = desplazamiento horizontal
  outer.addEventListener("wheel", function(e) {
    e.preventDefault();
    if (e.shiftKey) { outer.scrollLeft += (e.deltaY || e.deltaX); return; }
    var rect = outer.getBoundingClientRect();
    var f = e.deltaY < 0 ? 1.12 : 1 / 1.12;
    setScale(scale * f, e.clientX - rect.left, e.clientY - rect.top);
  }, { passive: false });
  document.getElementById("zIn").addEventListener("click", function() { setScale(scale * 1.25); });
  document.getElementById("zOut").addEventListener("click", function() { setScale(scale / 1.25); });
  document.getElementById("zReset").addEventListener("click", function() { setScale(1); });
  document.getElementById("zFit").addEventListener("click", function() { fitAll(); });
  window.addEventListener("resize", apply);

  // defecto: LEGIBLE antes que completo. Si el fit encoge demasiado (árboles
  // anchos o muy altos), arrancamos al 90% sobre la raíz y se recorre con la
  // ruleta/scroll: el diagrama se ve vertical, no como una tira aplastada.
  var fitScale = Math.min(1, outer.clientWidth / DATA.width, outer.clientHeight / DATA.height);
  if (fitScale >= 0.65) {
    fitAll();
  } else {
    setScale(0.9, 0, 0);
    outer.scrollTop = 0;
    outer.scrollLeft = 0;
  }

  // ── arrastre para desplazarse (grab) ──
  var dragStart = null, dragged = false;
  outer.addEventListener("mousedown", function(e) {
    if (e.button !== 0) return;
    dragStart = { x: e.clientX, y: e.clientY, sl: outer.scrollLeft, st: outer.scrollTop };
    dragged = false;
  });
  window.addEventListener("mousemove", function(e) {
    if (!dragStart) return;
    var dx = e.clientX - dragStart.x, dy = e.clientY - dragStart.y;
    if (Math.abs(dx) + Math.abs(dy) > 4) dragged = true;
    if (dragged) {
      outer.scrollLeft = dragStart.sl - dx;
      outer.scrollTop = dragStart.st - dy;
    }
  });
  window.addEventListener("mouseup", function() { dragStart = null; });

  // ── niveles y contratos: re-consulta al host (con debounce) ──
  var paramsTimer;
  function postParams() {
    if (!vsc) return;
    clearTimeout(paramsTimer);
    paramsTimer = setTimeout(function() {
      vsc.postMessage({
        type: "params",
        depth: parseInt(document.getElementById("depth").value, 10),
        dtos: document.getElementById("dtosChk").checked
      });
    }, 350);
  }
  var depthInput = document.getElementById("depth");
  depthInput.addEventListener("input", function() {
    document.getElementById("depthVal").textContent = this.value;
  });
  depthInput.addEventListener("change", postParams);
  document.getElementById("dtosChk").addEventListener("change", postParams);
  document.body.addEventListener("click", function(ev) {
    if (dragged) { dragged = false; return; } // era un arrastre, no un clic
    var node = ev.target.closest ? ev.target.closest(".node") : null;
    if (node) {
      var file = node.getAttribute("data-file");
      var line = parseInt(node.getAttribute("data-line") || "0", 10);
      if (file && vsc) { vsc.postMessage({ type: "open", file: file, line: line }); }
      return;
    }
    if (ev.target.id === "mmdBtn") {
      var pre = document.getElementById("mmd");
      pre.style.display = pre.style.display === "block" ? "none" : "block";
      return;
    }
    if (ev.target.id === "popOut" && vsc) {
      vsc.postMessage({ type: "openInEditor" });
    }
  });
  var chk = document.getElementById("infraChk");
  if (chk) {
    chk.addEventListener("change", function() {
      document.body.classList.toggle("no-infra", !chk.checked);
      document.querySelectorAll(".edge").forEach(function(g) {
        var hide = g.getAttribute("data-infra") === "1" && !chk.checked;
        g.style.display = hide ? "none" : "";
      });
    });
  }
})();
</script>
</body>
</html>`;
}

function shortFile(file: string): string {
    const sep = Math.max(file.lastIndexOf("\\"), file.lastIndexOf("/"));
    const name = sep >= 0 ? file.substring(sep + 1) : file;
    return "…/" + (name.length > 34 ? name.substring(0, 33) + "…" : name);
}
