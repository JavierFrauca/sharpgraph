import * as crypto from "crypto";
import * as vscode from "vscode";
import { renderFlowHtml, type FlowData } from "./flowHtml";
import type { EndpointItem } from "./endpoints";
import type { SharpGraphClient } from "./mcp";

/** Petición del panel grande. La 'expression' es la clave (endpoint o
 * controlador); si llega 'flow' — el diagrama lateral ya lo consultó — se
 * pinta directamente SIN volver a hablar con el motor: cero latencia extra y
 * cero riesgo de que una segunda consulta deje el panel en blanco. */
export interface FlowPanelRequest {
    /** "POST /api/orders" o "OrdersController" */
    expression: string;
    title: string;
    /** endpoint concreto (fila del árbol); ausente en modo controlador */
    ep?: EndpointItem;
    /** flujo ya resuelto por el diagrama lateral, si lo hay */
    flow?: FlowData;
    depth: number;
    dtos: boolean;
}

/** Petición del panel para un endpoint del árbol: sin flujo previo, el motor
 * se consulta al abrir (con timeout y errores visibles). */
export function panelRequestFor(ep: EndpointItem, depth: number, dtos: boolean): FlowPanelRequest {
    return {
        expression: `${ep.verb} ${ep.route}`,
        title: `${ep.verb} ${ep.route} · ${ep.controllerName}.${ep.method}`,
        ep,
        depth,
        dtos,
    };
}

/** Panel webview del diagrama: uno por expression (los demás se revelan, no se
 * duplican). Recibe el JSON de endpoint_flow, pinta el diagrama y devuelve el
 * click-to-open como vscode.open con selección de línea. */
export class FlowPanelManager implements vscode.Disposable {
    private readonly panels = new Map<string, vscode.WebviewPanel>();
    /** generación por panel: una respuesta tardía del motor no pisa una nueva */
    private readonly gens = new Map<string, number>();

    constructor(
        private readonly client: SharpGraphClient,
        private readonly extensionUri: vscode.Uri,
        private readonly onLog: (msg: string) => void = () => {},
        private readonly onTraceEndpoint: (name: string) => void = () => {},
        /** texto si hay un indexado en curso (se muestra mientras carga) */
        private readonly isIndexing: () => string | null = () => null,
    ) {}

    async open(req: FlowPanelRequest): Promise<void> {
        const key = req.expression;
        const existing = this.panels.get(key);
        if (existing) {
            existing.reveal(undefined, true);
            if (req.flow) {
                this.paint(existing, key, req.flow, req);
            }
            return;
        }

        const panel = vscode.window.createWebviewPanel(
            "sharpgraphFlow.editorPanel",
            req.title,
            vscode.ViewColumn.Active,
            { enableScripts: true, retainContextWhenHidden: true },
        );
        panel.iconPath = vscode.Uri.joinPath(this.extensionUri, "media", "activity-icon.svg");
        this.panels.set(key, panel);
        panel.onDidDispose(() => {
            this.panels.delete(key);
            this.gens.delete(key);
        });

        if (req.flow) {
            this.paint(panel, key, req.flow, req);
        } else {
            panel.webview.html = loadingHtml(crypto.randomBytes(16).toString("hex"), req.title, this.isIndexing());
        }
        panel.webview.onDidReceiveMessage((msg) => {
            if (msg?.type === "open" && typeof msg.file === "string" && msg.file.length > 0) {
                void openAtLine(vscode.Uri.file(msg.file), Number(msg.line) || 0);
            } else if (msg?.type === "traceEndpoint" && typeof msg.name === "string" && msg.name.length > 0) {
                // clic derecho en un command/query: trazar hasta su endpoint
                this.onTraceEndpoint(msg.name);
            } else if (msg?.type === "params") {
                const depth = Math.min(12, Math.max(1, Number(msg.depth) || req.depth));
                void this.renderFlow(panel, key, { ...req, depth, dtos: !!msg.dtos });
            }
        });

        if (!req.flow) {
            await this.renderFlow(panel, key, req);
        }
    }

    /** Pinta un flujo YA consultado (instantáneo, sin round-trip al motor). */
    private paint(panel: vscode.WebviewPanel, key: string, flow: FlowData, req: FlowPanelRequest): void {
        const gen = (this.gens.get(key) ?? 0) + 1;
        this.gens.set(key, gen);
        const config = vscode.workspace.getConfiguration("sharpgraphFlow");
        panel.webview.html = renderFlowHtml(flow, {
            nonce: crypto.randomBytes(16).toString("hex"),
            includeInfra: config.get<boolean>("includeInfra", false),
            depth: req.depth,
            includeDtos: req.dtos,
            title: req.title,
        });
    }

    /** Consulta endpoint_flow con los niveles/contratos dados y pinta el panel. */
    private async renderFlow(panel: vscode.WebviewPanel, key: string, req: FlowPanelRequest): Promise<void> {
        const config = vscode.workspace.getConfiguration("sharpgraphFlow");
        const maxNodes = Math.min(200, Math.max(5, config.get<number>("maxNodes", 80)));
        const gen = (this.gens.get(key) ?? 0) + 1;
        this.gens.set(key, gen);
        try {
            const json = await withTimeout(
                this.client.callTool("endpoint_flow", {
                    endpoint: req.expression,
                    maxDepth: req.depth,
                    maxNodes,
                    includeDtos: req.dtos,
                }),
                120_000,
                `el motor no respondió en 120 s${this.isIndexing() ? " (indexando la solución)" : ""}`,
            );
            const flow = JSON.parse(json) as FlowData;
            // el panel pudo cerrarse (o cambiar de params) mientras respondía el motor
            if (!this.panels.has(key) || this.gens.get(key) !== gen) {
                return;
            }
            this.paint(panel, key, flow, req);
        } catch (err) {
            const message = err instanceof Error ? err.message : String(err);
            this.onLog(`panel «${req.title}» no cargó: ${message}`);
            if (this.panels.has(key) && this.gens.get(key) === gen) {
                panel.webview.html = errorHtml(message);
            }
        }
    }

    dispose(): void {
        for (const p of this.panels.values()) {
            p.dispose();
        }
        this.panels.clear();
        this.gens.clear();
    }
}

function withTimeout<T>(p: Promise<T>, ms: number, hint: string): Promise<T> {
    return new Promise<T>((resolve, reject) => {
        const timer = setTimeout(
            () => reject(new Error(`${hint}. Vuelve a abrir el diagrama cuando termine el aviso de progreso.`)),
            ms,
        );
        p.then(
            (v) => {
                clearTimeout(timer);
                resolve(v);
            },
            (e) => {
                clearTimeout(timer);
                reject(e);
            },
        );
    });
}

export async function openAtLine(
    uri: vscode.Uri,
    line: number,
    column: vscode.ViewColumn = vscode.ViewColumn.Beside,
): Promise<void> {
    const editor = await vscode.window.showTextDocument(uri, {
        preview: true,
        viewColumn: column,
    });
    if (line > 0 && line <= editor.document.lineCount) {
        const range = new vscode.Range(line - 1, 0, line - 1, 0);
        editor.revealRange(range, vscode.TextEditorRevealType.InCenter);
        editor.selection = new vscode.Selection(range.start, range.start);
    }
}

function loadingHtml(nonce: string, title: string, indexing: string | null): string {
    const extra = indexing ? `<br><span style="font-size:11px">${indexing}</span>` : "";
    return `<!DOCTYPE html><html><head><meta charset="utf-8">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; script-src 'nonce-${nonce}';">
<style>body{background:var(--vscode-editor-background,#1f1f1f);color:var(--vscode-editor-foreground,#ccc);
font-family:"Segoe UI",sans-serif;display:grid;place-items:center;height:100vh;margin:0;text-align:center;line-height:1.6}
.spin{display:flex;gap:10px;align-items:center;color:var(--vscode-descriptionForeground,#9a9a9a)}</style></head>
<body><div class="spin"><span>⚡</span><span> Generando flujo de <b>${title}</b> con SharpGraph…${extra}</span></div></body></html>`;
}

function errorHtml(message: string): string {
    const safe = message.replace(/&/g, "&amp;").replace(/</g, "&lt;");
    return `<!DOCTYPE html><html><head><meta charset="utf-8">
<style>body{background:#1f1f1f;color:#ccc;font-family:"Segoe UI",sans-serif;display:grid;place-items:center;height:100vh;margin:0}
.err{border:1px solid #5a1d1d;background:#2a1515;padding:18px 22px;border-radius:8px;max-width:520px;line-height:1.5}</style></head>
<body><div class="err"><b>SharpGraph Flow</b><br>${safe}</div></body></html>`;
}
