// Sonda MCP standalone: replica EXACTAMENTE lo que hace la extensión
// (spawn exe con la solución → initialize → list_endpoints) y añade
// el escenario de re-arranque (stop + spawn de nuevo) que dispara el watcher.
const { spawn } = require("child_process");
const readline = require("readline");

const EXE = process.env.USERPROFILE + "\\tools\\SharpGraph\\SharpGraph.exe";
const SOLUTION = process.argv[2] || ".";

function client(label) {
    return new Promise((resolve, reject) => {
        const t0 = Date.now();
        const proc = spawn(EXE, [SOLUTION], { stdio: ["pipe", "pipe", "pipe"], windowsHide: true });
        proc.stderr.on("data", (d) => process.stderr.write(`[${label} stderr] ${d}`));
        const rl = readline.createInterface({ input: proc.stdout, crlfDelay: Infinity });
        let nextId = 1;
        const pending = new Map();
        rl.on("line", (line) => {
            if (!line.trim().startsWith("{")) return;
            let msg; try { msg = JSON.parse(line); } catch { return; }
            if (msg.id && pending.has(msg.id)) {
                const p = pending.get(msg.id); pending.delete(msg.id); p(msg);
            }
        });
        const request = (method, params) => new Promise((res, rej) => {
            const id = nextId++;
            pending.set(id, res);
            proc.stdin.write(JSON.stringify({ jsonrpc: "2.0", id, method, params }) + "\n");
            setTimeout(() => pending.has(id) && (pending.delete(id), rej(new Error("timeout " + method))), 60000);
        });
        (async () => {
            await request("initialize", { protocolVersion: "2024-11-05", capabilities: {}, clientInfo: { name: "probe", version: "0" } });
            proc.stdin.write(JSON.stringify({ jsonrpc: "2.0", method: "notifications/initialized" }) + "\n");
            const res = await request("tools/call", { name: "list_endpoints", arguments: {} });
            const text = res.result?.content?.[0]?.text ?? "";
            const parsed = JSON.parse(text);
            console.log(`[${label}] initialize+list_endpoints OK en ${Date.now() - t0}ms → count=${parsed.count}`);
            resolve({ proc, request });
        })().catch(reject);
    });
}

(async () => {
    const a = await client("A-1º arranque");
    const b = await client("B-2º arranque en paralelo");
    await new Promise(r => setTimeout(r, 1000));
    const res = await b.request("tools/call", { name: "list_endpoints", arguments: {} });
    console.log(`[B re-test] count=${JSON.parse(res.result.content[0].text).count}`);
    a.proc.kill(); b.proc.kill();
    process.exit(0);
})().catch(e => { console.error("FALLO:", e); process.exit(1); });
