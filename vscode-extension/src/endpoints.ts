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
        private readonly onOpenInEditor: (ep: EndpointItem) => void,
        private readonly onCommand: (cmd: "scan" | "configure") => void,
        private readonly onRefreshGroup: (key: string) => void,
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
            } else if (msg?.type === "openInEditor" && msg.ep) {
                this.onOpenInEditor(msg.ep as EndpointItem);
            } else if (msg?.type === "cmd") {
                this.onCommand(msg.cmd === "configure" ? "configure" : "scan");
            } else if (msg?.type === "refreshGroup" && typeof msg.key === "string") {
                this.onRefreshGroup(msg.key);
            }
        });
        if (this.status.kind !== "loading") {
            this.pushStatus();
        }
        if (this.status.kind === "ok") {
            this.pushData();
        }
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
