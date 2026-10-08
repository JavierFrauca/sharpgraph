import * as crypto from "crypto";
import * as vscode from "vscode";
import { renderEndpointsShell } from "./endpointsHtml";

export type { EndpointItem } from "./endpointsHtml";
import type { EndpointItem } from "./endpointsHtml";

/** Clave de agrupación de un endpoint: controlador, o el endpoint suelto para
 * minimal APIs planos (clave sintética "GET /users"). Debe coincidir con la
 * misma regla en el JS del webview. */
export function groupKeyOf(ep: EndpointItem): string {
    return /^[A-Z]+ \//.test(ep.controller) ? ep.controller : ep.controllerName;
}

type Status =
    | { kind: "ok" }
    | { kind: "error"; message: string; hint?: string }
    | { kind: "loading" };

/**
 * Vista "Endpoints" de la barra lateral, como WEBVIEW (no árbol nativo): la
 * razón es el campo de FILTRO FIJO encima de la lista, imposible en un
 * TreeView nativo. El filtrado, agrupado y expansión viven DENTRO del webview
 * (instantáneos); el host solo empuja datos/estado y recibe selecciones.
 * Trade-off asumido: sin TreeView no hay API de badge en el ActivityBar — el
 * contador vive en la propia vista.
 */
export class EndpointsWebviewProvider implements vscode.WebviewViewProvider {
    static readonly viewId = "sharpgraphFlow.endpoints";

    private view?: vscode.WebviewView;
    private data: EndpointItem[] = [];
    private status: Status = { kind: "loading" };

    constructor(
        private readonly onSelect: (ep: EndpointItem) => void,
        private readonly onSelectController: (controllerName: string) => void,
        private readonly onOpenInEditor: (ep: EndpointItem) => void,
        private readonly onCommand: (cmd: "scan" | "configure") => void,
        private readonly onRefreshGroup: (key: string) => void,
        private readonly onWebviewError: (message: string) => void,
    ) {}

    resolveWebviewView(view: vscode.WebviewView): void {
        this.view = view;
        view.webview.options = { enableScripts: true };
        // si ya hay datos, se siembran en el HTML (sin flash de "Escaneando…");
        // si no, el shell arranca en loading y el estado llega por postMessage
        view.webview.html = renderEndpointsShell({
            nonce: crypto.randomBytes(16).toString("hex"),
            seed: this.status.kind === "ok" ? this.data : [],
        });
        view.webview.onDidReceiveMessage((msg) => {
            if (msg?.type === "select" && msg.ep) {
                this.onSelect(msg.ep as EndpointItem);
            } else if (msg?.type === "selectController" && typeof msg.name === "string") {
                this.onSelectController(msg.name);
            } else if (msg?.type === "openInEditor" && msg.ep) {
                this.onOpenInEditor(msg.ep as EndpointItem);
            } else if (msg?.type === "cmd") {
                this.onCommand(msg.cmd === "configure" ? "configure" : "scan");
            } else if (msg?.type === "refreshGroup" && typeof msg.key === "string") {
                this.onRefreshGroup(msg.key);
            } else if (msg?.type === "webviewError" && typeof msg.message === "string") {
                this.onWebviewError(msg.message);
            }
        });
        if (this.status.kind !== "loading") {
            this.pushStatus();
        }
        if (this.status.kind === "ok") {
            this.pushData();
        }
        this.flushPendingSelect();
    }

    /** Catálogo nuevo del motor: estado ok + datos. */
    setData(items: EndpointItem[]): void {
        this.data = items;
        this.status = { kind: "ok" };
        if (this.view) {
            this.pushData();
        }
    }

    /** Refresco parcial: sustituye solo los endpoints de un grupo (al expandir). */
    updateGroup(key: string, items: EndpointItem[]): void {
        void this.view?.webview.postMessage({ type: "groupData", key, items });
    }

    get hasData(): boolean {
        return this.data.length > 0;
    }

    /** Busca un endpoint del catálogo por verbo+ruta (normalizando barras
     * finales y mayúsculas): la ruta puede venir del trace del motor. */
    findByRoute(verb: string, route: string): EndpointItem | undefined {
        const norm = (s: string) => s.toLowerCase().replace(/\/+$/, "") || "/";
        return this.data.find(
            (ep) => ep.verb.toUpperCase() === verb.toUpperCase() && norm(ep.route) === norm(route),
        );
    }

    /** Resalta un endpoint del árbol (fila + grupo expandido + scroll) y
     * muestra la vista. Lo usa el clic derecho en command/query del diagrama. */
    selectEndpoint(ep: EndpointItem): void {
        this.pendingSelect = ep;
        if (this.view) {
            this.flushPendingSelect();
        } else {
            // la vista nunca se resolvió: forzarla y volcar la selección al abrir
            void vscode.commands
                .executeCommand(`${EndpointsWebviewProvider.viewId}.focus`)
                .then(() => this.flushPendingSelect());
        }
    }

    private pendingSelect?: EndpointItem;

    private flushPendingSelect(): void {
        if (this.view && this.pendingSelect) {
            const ep = this.pendingSelect;
            this.pendingSelect = undefined;
            void this.view.show(true);
            void this.view.webview.postMessage({ type: "selectEndpoint", ep });
        }
    }

    setStatus(status: Status): void {
        this.status = status;
        this.pushStatus();
    }

    get totalCount(): number {
        return this.data.length;
    }

    private pushData(): void {
        void this.view?.webview.postMessage({ type: "data", items: this.data });
    }

    private pushStatus(): void {
        void this.view?.webview.postMessage({ type: "status", status: this.status });
    }

    dispose(): void {
        // la vista la gestiona VS Code
    }
}
