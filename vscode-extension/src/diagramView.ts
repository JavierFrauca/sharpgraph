import * as crypto from "crypto";
import * as vscode from "vscode";
import { renderFlowHtml, type FlowData } from "./flowHtml";
import type { EndpointItem } from "./endpoints";
import type { SharpGraphClient } from "./mcp";
import { openAtLine } from "./flowPanel";

/**
 * Zona inferior del contenedor en la barra lateral: webview embebido con el
 * diagrama del endpoint seleccionado en el árbol (mismo render que el panel
 * del editor). Clic en un endpoint → se actualiza aquí, sin abrir paneles;
 * "Editor" lo saca al panel grande. Guarda el último endpoint para renderizar
 * en cuanto VS Code resuelva la vista (lazy).
 */
export class DiagramViewProvider implements vscode.WebviewViewProvider {
    static readonly viewId = "sharpgraphFlow.diagram";

    private view?: vscode.WebviewView;
    private lastEndpoint?: EndpointItem;
    /** niveles/contratos vigentes (los cambia el slider del propio diagrama) */
    private lastDepth = 4;
    private lastDtos = false;
    /** genera del lado del host cuando el usuario pide el panel grande */
    private gen = 0;

    constructor(
        private readonly client: SharpGraphClient,
        private readonly onOpenInEditor: (ep: EndpointItem) => void,
    ) {}

    get current(): EndpointItem | undefined {
        return this.lastEndpoint;
    }

    resolveWebviewView(view: vscode.WebviewView): void {
        this.view = view;
        view.webview.options = { enableScripts: true };
        view.webview.html = this.emptyHtml();
        view.webview.onDidReceiveMessage((msg) => {
            if (msg?.type === "open" && typeof msg.file === "string" && msg.file.length > 0) {
                // desde la barra lateral: columna ACTIVA, sin partir la pantalla
                void openAtLine(vscode.Uri.file(msg.file), Number(msg.line) || 0, vscode.ViewColumn.Active);
            } else if (msg?.type === "openInEditor" && this.lastEndpoint) {
                this.onOpenInEditor(this.lastEndpoint);
            } else if (msg?.type === "params") {
                this.lastDepth = Math.min(12, Math.max(1, Number(msg.depth) || 4));
                this.lastDtos = !!msg.dtos;
                if (this.lastEndpoint) {
                    void this.show(this.lastEndpoint, false, true);
                }
            } else if (msg?.type === "cmd") {
                // no aplicable en esta vista (placeholder por simetría)
            }
        });
        if (this.lastEndpoint) {
            void this.show(this.lastEndpoint, false);
        }
    }

    /** Renderiza el flujo del endpoint en la vista embebida. reveal=false cuando
     * ya es visible (evita robar el foco mientras se navega el árbol con teclado);
     * keepParams=true cuando el origen es el slider (conserva nivel/contratos). */
    async show(ep: EndpointItem, reveal = true, keepParams = false): Promise<void> {
        this.lastEndpoint = ep;
        this.gen++;
        const myGen = this.gen;
        if (reveal) {
            if (this.view) {
                // preserveFocus: la lista no pierde el foco (se puede navegar con flechas)
                void this.view.show(true);
            } else {
                // primera vez: forzar la resolución de la vista
                void vscode.commands.executeCommand(`${DiagramViewProvider.viewId}.focus`);
            }
        }
        const view = this.view;
        if (!view) {
            return; // resolveWebviewView renderizará lastEndpoint
        }

        const config = vscode.workspace.getConfiguration("sharpgraphFlow");
        view.webview.html = this.loadingHtml(ep);
        try {
            if (!keepParams) {
                this.lastDepth = Math.min(12, Math.max(1, config.get<number>("defaultDepth", 4)));
                this.lastDtos = config.get<boolean>("includeDtos", false);
            }
            const maxNodes = Math.min(200, Math.max(5, config.get<number>("maxNodes", 80)));
            const json = await this.client.callTool("endpoint_flow", {
                endpoint: `${ep.verb} ${ep.route}`,
                maxDepth: this.lastDepth,
                maxNodes,
                includeDtos: this.lastDtos,
            });
            const flow = JSON.parse(json) as FlowData;
            // mientras esperábamos al motor cambió la selección o la vista murió
            if (this.gen !== myGen || this.view !== view) {
                return;
            }
            view.webview.html = renderFlowHtml(flow, {
                nonce: crypto.randomBytes(16).toString("hex"),
                includeInfra: config.get<boolean>("includeInfra", false),
                openInEditor: true,
                depth: this.lastDepth,
                includeDtos: this.lastDtos,
            });
        } catch (err) {
            const message = err instanceof Error ? err.message : String(err);
            if (this.gen === myGen && this.view === view) {
                view.webview.html = this.errorHtml(message);
            }
        }
    }

    private emptyHtml(): string {
        return `<!DOCTYPE html><html><head><meta charset="utf-8"><style>
            body{background:var(--vscode-sideBar-background,#181818);color:var(--vscode-sideBar-foreground,#bbb);
            font-family:"Segoe UI",sans-serif;font-size:12px;display:grid;place-items:center;height:100vh;margin:0;
            text-align:center;line-height:1.6;padding:0 18px;box-sizing:border-box}</style></head>
            <body><div>⚡ Clic en un endpoint del árbol<br>para ver su flujo aquí.</div></body></html>`;
    }

    private loadingHtml(ep: EndpointItem): string {
        return `<!DOCTYPE html><html><head><meta charset="utf-8"><style>
            body{background:var(--vscode-sideBar-background,#181818);color:var(--vscode-sideBar-foreground,#bbb);
            font-family:"Segoe UI",sans-serif;font-size:12px;display:grid;place-items:center;height:100vh;margin:0}</style></head>
            <body><div>⚡ Generando ${ep.verb} ${ep.route}…</div></body></html>`;
    }

    private errorHtml(message: string): string {
        const safe = message.replace(/&/g, "&amp;").replace(/</g, "&lt;");
        return `<!DOCTYPE html><html><head><meta charset="utf-8"><style>
            body{background:var(--vscode-sideBar-background,#181818);color:var(--vscode-sideBar-foreground,#ccc);
            font-family:"Segoe UI",sans-serif;font-size:12px;padding:14px;line-height:1.5;margin:0}
            .err{border:1px solid #5a1d1d;background:rgba(90,29,29,.25);padding:10px 12px;border-radius:6px}</style></head>
            <body><div class="err"><b>SharpGraph Flow</b><br>${safe}</div></body></html>`;
    }

    dispose(): void {
        // la vista la gestiona VS Code (WebviewView no es disposable)
    }
}
