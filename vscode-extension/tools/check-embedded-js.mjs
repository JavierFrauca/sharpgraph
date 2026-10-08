// Verificación: extrae los <script> de los HTML generados por renderEndpointsShell
// y renderFlowHtml y valida su sintaxis con node --check (tsc no ve el JS embebido).
import * as esbuild from "esbuild";
import * as fs from "node:fs";
import * as os from "node:os";
import * as path from "node:path";
import { execFileSync } from "node:child_process";

const tmp = fs.mkdtempSync(path.join(os.tmpdir(), "sgjs-"));
const entry = path.join(tmp, "entry.ts");
fs.writeFileSync(entry, `
import { renderEndpointsShell } from "C:/repo/LocalGraph/vscode-extension/src/endpointsHtml";
import { renderFlowHtml } from "C:/repo/LocalGraph/vscode-extension/src/flowHtml";
export { renderEndpointsShell, renderFlowHtml };
`);
const out = path.join(tmp, "bundle.cjs");
await esbuild.build({
    entryPoints: [entry],
    bundle: true,
    platform: "node",
    format: "cjs",
    outfile: out,
});
const { renderEndpointsShell, renderFlowHtml } = await import("file://" + out.replace(/\\/g, "/"));

const htmls = [
    ["endpoints", renderEndpointsShell({ nonce: "n0", seed: [] })],
    ["flow", renderFlowHtml(
        {
            nodes: [
                { id: "N0", kind: "endpoint", name: "POST /api/x", file: "a.cs", line: 1 },
                { id: "N1", kind: "command", name: "DoCommand", fqn: "App.DoCommand", file: "b.cs", line: 2 },
                { id: "N2", kind: "query", name: "GetQuery", file: null },
            ],
            edges: [
                { from: "N0", to: "N1", relation: "sends", line: 3 },
                { from: "N1", to: "N2", relation: "handled-by", back: true },
            ],
            mermaid: "graph TD\nA-->B",
        },
        { nonce: "n1", includeInfra: false, openInEditor: true, depth: 2, includeDtos: false, title: "t" },
    )],
];

let fail = 0;
for (const [name, html] of htmls) {
    const checks = [
        [`${name}: botón Abrir`, name !== "flow" || html.includes('>Abrir</button>')],
        [`${name}: botón Mermaid`, !html.includes('id="mmdBtn"') || html.includes('>Ver Mermaid</button>')],
        [`${name}: data-kind en nodos`, !html.includes('class="node') || html.includes('data-kind="command"')],
        [`${name}: sin rastro del nombre viejo`, !html.includes('⤢ Editor')],
    ];
    for (const [label, ok] of checks) {
        if (!ok) { console.error("FALLO", label); fail++; }
    }
    const scripts = [...html.matchAll(/<script[^>]*>([\s\S]*?)<\/script>/g)].map((m) => m[1]);
    scripts.forEach((s, i) => {
        const f = path.join(tmp, `${name}-${i}.js`);
        fs.writeFileSync(f, s);
        try {
            execFileSync(process.execPath, ["--check", f], { stdio: "pipe" });
            console.log(`OK sintaxis ${name}-${i}.js (${s.length} chars)`);
        } catch (e) {
            console.error(`FALLO sintaxis ${name}-${i}.js:\n${e.stderr}`);
            fail++;
        }
    });
}
console.log(fail === 0 ? "TODO OK" : `${fail} fallos`);
process.exit(fail === 0 ? 0 : 1);
