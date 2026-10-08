// Probe: replica el cliente MCP de la extensión (mcp.ts) contra el motor
// publicado y ejercita exactamente las llamadas del panel grande + trace.
import * as cp from "node:child_process";
import * as readline from "node:readline";

const exe = process.argv[2] ?? "C:\\repo\\LocalGraph\\publish\\SharpGraph.exe";
const solution = process.argv[3] ?? process.env.TEMP + "\\sgbench\\sgbench.csproj";

const proc = cp.spawn(exe, [], { stdio: ["pipe", "pipe", "pipe"], windowsHide: true });
let nextId = 1;
const pending = new Map();
const t = {};

proc.stderr.on("data", (d) => process.stderr.write("[srv] " + d));
const rl = readline.createInterface({ input: proc.stdout, crlfDelay: Infinity });
rl.on("line", (line) => {
    const s = line.trim();
    if (!s.startsWith("{")) return;
    let msg;
    try { msg = JSON.parse(s); } catch { return; }
    if (msg.id !== undefined && pending.has(msg.id)) {
        const p = pending.get(msg.id);
        pending.delete(msg.id);
        msg.error ? p.reject(new Error(msg.error.message)) : p.resolve(msg.result);
    }
});

function request(method, params) {
    return new Promise((resolve, reject) => {
        const id = nextId++;
        pending.set(id, { resolve, reject });
        proc.stdin.write(JSON.stringify({ jsonrpc: "2.0", id, method, params }) + "\n");
    });
}

async function callTool(name, args = {}) {
    const r = await request("tools/call", { name, arguments: args });
    if (r?.isError) throw new Error(r.content?.map((c) => c.text).join("\n"));
    return (r?.content ?? []).map((c) => c.text ?? "").join("\n");
}

const timed = async (label, fn) => {
    const t0 = Date.now();
    try {
        const out = await fn();
        console.log(`${label}: OK ${Date.now() - t0} ms`);
        return out;
    } catch (e) {
        console.log(`${label}: FALLO ${Date.now() - t0} ms → ${e.message}`);
        return null;
    }
};

await timed("initialize", () => request("initialize", {
    protocolVersion: "2024-11-05", capabilities: {},
    clientInfo: { name: "probe-panel", version: "0" },
}));
proc.stdin.write(JSON.stringify({ jsonrpc: "2.0", method: "notifications/initialized" }) + "\n");

const stats = await timed("stats", () => callTool("stats"));
console.log(stats?.split("\n").slice(0, 3).join(" | "));

await timed("scan", () => callTool("scan", { path: solution }));

const listJson = await timed("list_endpoints", () => callTool("list_endpoints"));
const eps = listJson ? JSON.parse(listJson).endpoints ?? [] : [];
console.log(`endpoints: ${eps.length}`);
if (eps.length > 0) {
    console.log("primero:", JSON.stringify(eps[0]));
    // llamada EXACTA del panel grande (flowPanel.renderFlow)
    const ep = eps[0];
    const flow = await timed("endpoint_flow (panel grande)", () => callTool("endpoint_flow", {
        endpoint: `${ep.verb} ${ep.route}`,
        maxDepth: 2, maxNodes: 80, includeDtos: false,
    }));
    if (flow) {
        const data = JSON.parse(flow);
        console.log(`flow: ${data.nodes?.length} nodos, ${data.edges?.length} aristas, mode=${data.mode ?? "?"}, error=${data.error ?? "no"}`);
    }
    // trace_to_endpoints desde un command/query del flujo (botón derecho)
    if (flow) {
        const data = JSON.parse(flow);
        const cq = data.nodes?.find((n) => n.kind === "command" || n.kind === "query");
        if (cq) {
            const trace = await timed(`trace_to_endpoints (${cq.name})`, () => callTool("trace_to_endpoints", { typeName: cq.name }));
            console.log(trace?.split("\n").slice(0, 6).join("\n"));
        } else {
            console.log("(el flujo no tiene nodos command/query — probe con otro corpus)");
        }
    }
}
proc.kill();
process.exit(0);
