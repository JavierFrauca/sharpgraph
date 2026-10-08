// Probe: verifica las ServerInstructions del motor recién publicado.
import * as cp from "node:child_process";
import * as readline from "node:readline";
import * as path from "node:path";

const exe = process.argv[2] ?? path.join("C:", "repo", "LocalGraph", "publish", "SharpGraph.exe");
const proc = cp.spawn(exe, [], { stdio: ["pipe", "pipe", "pipe"], windowsHide: true });
let id = 1;
const pending = new Map();
const rl = readline.createInterface({ input: proc.stdout, crlfDelay: Infinity });
rl.on("line", (l) => {
    const s = l.trim();
    if (!s.startsWith("{")) return;
    const m = JSON.parse(s);
    if (m.id !== undefined && pending.has(m.id)) {
        pending.get(m.id)(m.result);
        pending.delete(m.id);
    }
});
const req = (method, params) =>
    new Promise((res) => {
        const i = id++;
        pending.set(i, res);
        proc.stdin.write(JSON.stringify({ jsonrpc: "2.0", id: i, method, params }) + "\n");
    });

const init = await req("initialize", {
    protocolVersion: "2024-11-05",
    capabilities: {},
    clientInfo: { name: "probe", version: "0" },
});
const instr = init?.instructions ?? "";
console.log("serverInfo.version:", init?.serverInfo?.version);
console.log("PRIMERA VEZ presente:", instr.includes("PRIMERA VEZ"));
console.log("auto-init presente:", instr.includes("DE FORMA AUTOMÁTICA"));
const i = instr.indexOf("== PRIMERA VEZ ==");
console.log(instr.slice(i, i + 340));
proc.kill();
process.exit(0);
