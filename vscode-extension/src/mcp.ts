import * as cp from "child_process";
import * as readline from "readline";
import * as vscode from "vscode";

/**
 * Cliente MCP mínimo sobre stdio para el servidor SharpGraph: JSON-RPC 2.0 con
 * un mensaje por línea (transporte stdio de MCP). Suficiente para initialize +
 * tools/call; sin dependencias npm de runtime. El servidor se arranca con la
 * solución como argumento: el pre-escaneo (caché en frío incluida) ocurre ANTES
 * de responder a initialize, así que el primer await ya deja el grafo caliente.
 */
export class SharpGraphClient implements vscode.Disposable {
    private proc?: cp.ChildProcess;
    private nextId = 1;
    private readonly pending = new Map<number, { resolve: (v: any) => void; reject: (e: Error) => void }>();
    private startedAt = 0;

    private readonly _onStderr = new vscode.EventEmitter<string>();
    readonly onStderr = this._onStderr.event;

    private readonly _onExit = new vscode.EventEmitter<number | null>();
    readonly onExit = this._onExit.event;

    get isRunning(): boolean {
        return this.proc !== undefined && this.proc.exitCode === null;
    }

    /** Arranca el servidor. SIN solutionPath el initialize es instantáneo (el
     * motor arranca en modo "llama a scan()"): el scan incremental se pide
     * después como tool, en background, sin bloquear la vista. */
    async start(serverPath: string, solutionPath?: string): Promise<void> {
        const old = this.proc;
        this.stop();
        // espera a que el proceso anterior muera del todo antes de spawnear:
        // evita contención con la caché en disco y stdout entremezclado
        if (old && old.exitCode === null) {
            await new Promise<void>((resolve) => {
                const timer = setTimeout(resolve, 1500);
                old.once("exit", () => {
                    clearTimeout(timer);
                    resolve();
                });
            });
        }
        this.startedAt = Date.now();
        const proc = cp.spawn(serverPath, solutionPath ? [solutionPath] : [], {
            stdio: ["pipe", "pipe", "pipe"],
            windowsHide: true,
        });
        this.proc = proc;

        proc.on("exit", (code) => {
            for (const p of this.pending.values()) {
                p.reject(new Error(`SharpGraph terminó inesperadamente (código ${code}).`));
            }
            this.pending.clear();
            this._onExit.fire(code);
        });

        proc.stderr?.on("data", (d: Buffer) => this._onStderr.fire(d.toString()));

        const rl = readline.createInterface({ input: proc.stdout!, crlfDelay: Infinity });
        rl.on("line", (line) => {
            const trimmed = line.trim();
            if (!trimmed.startsWith("{")) {
                return;
            }
            let msg: any;
            try {
                msg = JSON.parse(trimmed);
            } catch {
                return;
            }
            if (msg.id !== undefined && this.pending.has(msg.id)) {
                const p = this.pending.get(msg.id)!;
                this.pending.delete(msg.id);
                if (msg.error) {
                    p.reject(new Error(msg.error.message ?? JSON.stringify(msg.error)));
                } else {
                    p.resolve(msg.result);
                }
            }
        });

        await this.request("initialize", {
            protocolVersion: "2024-11-05",
            capabilities: {},
            clientInfo: { name: "sharpgraph-flow", version: "2.5.1" },
        });
        this.notify("notifications/initialized");
    }

    /** Llama a una tool y devuelve su primer contenido de texto. */
    async callTool(name: string, args: Record<string, unknown> = {}): Promise<string> {
        if (!this.isRunning) {
            throw new Error("El servidor SharpGraph no está arrancado.");
        }
        const result = await this.request("tools/call", { name, arguments: args });
        if (result?.isError) {
            const text = result?.content?.map((c: any) => c.text).join("\n") ?? "error";
            throw new Error(text);
        }
        const content = result?.content;
        if (!Array.isArray(content) || content.length === 0) {
            return "";
        }
        return content.map((c: any) => c.text ?? "").join("\n");
    }

    private request(method: string, params: unknown): Promise<any> {
        return new Promise((resolve, reject) => {
            if (!this.proc || !this.proc.stdin || !this.isRunning) {
                reject(new Error("Sin proceso SharpGraph."));
                return;
            }
            const id = this.nextId++;
            this.pending.set(id, { resolve, reject });
            const msg = JSON.stringify({ jsonrpc: "2.0", id, method, params }) + "\n";
            this.proc.stdin.write(msg, (err) => {
                if (err) {
                    this.pending.delete(id);
                    reject(err);
                }
            });
        });
    }

    private notify(method: string, params?: unknown): void {
        this.proc?.stdin?.write(JSON.stringify({ jsonrpc: "2.0", method, params }) + "\n");
    }

    /** Segundos desde el arranque (para diagnósticos de escaneo lento). */
    get uptimeSeconds(): number {
        return Math.round((Date.now() - this.startedAt) / 1000);
    }

    dispose(): void {
        this.stop();
        this._onStderr.dispose();
        this._onExit.dispose();
    }

    stop(): void {
        const proc = this.proc;
        this.proc = undefined;
        if (proc && proc.exitCode === null) {
            try {
                proc.kill();
            } catch {
                // ya estaba muerto: nada que hacer
            }
        }
        // rechazar las llamadas pendientes ANTES de limpiar: si alguna queda en
        // el mapa sin rechazar, su await se cuelga para siempre (bug del árbol
        // "Escaneando…" eterno cuando el watcher pisaba un refresh en curso)
        for (const p of this.pending.values()) {
            p.reject(new Error("El servidor SharpGraph se ha reiniciado."));
        }
        this.pending.clear();
    }
}
