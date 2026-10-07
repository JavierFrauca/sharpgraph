import * as crypto from "crypto";
import * as vscode from "vscode";
import { renderFlowHtml, type FlowData } from "./flowHtml";
import type { EndpointItem } from "./endpoints";
import type { SharpGraphClient } from "./mcp";

/** Panel webview del diagrama: uno por endpoint (los demás se revelan, no se
 * duplican). Recibe el JSON de endpoint_flow, pinta el diagrama y devuelve el
 * click-to-open como vscode.open con selección de línea. */
export class FlowPanelManager implements vscode.Disposable {
    private readonly panels = new Map<string, vscode.WebviewPanel>();

    constructor(
        private readonly client: SharpGraphClient,
        private readonly extensionUri: vscode.Uri,
    ) {}

    async open(ep: EndpointItem): Promise<void> {
        const key = `${ep.controller}|${ep.verb} ${ep.route}`;
        const existing = this.panels.get(key);
        if (existing) {
            existing.reveal(undefined, true);
            return;
        }

        const panel = vscode.window.createWebviewPanel(
            "sharpgraphFlow.editorPanel",
            `${ep.verb} ${ep.route}`,
            vscode.ViewColumn.Active,
            { enableScripts: true, retainContextWhenHidden: true },
        );
        panel.iconPath = vscode.Uri.joinPath(this.extensionUri, "media", "activity-icon.svg");
        this.panels.set(key, panel);
        panel.onDidDispose(() => this.panels.delete(key));

        panel.webview.html = loadingHtml(crypto.randomBytes(16).toString("hex"), ep);
        panel.webview.onDidReceiveMessage((msg) => {
            if (msg?.type === "open" && typeof msg.file === "string" && msg.file.length > 0) {
                void openAtLine(vscode.Uri.file(msg.file), Number(msg.line) || 0);
            } else if (msg?.type === "openInEditor" && this.panels.has(key)) {
                // el propio panel ya es el editor: nada que hacer aquí
            } else if (msg?.type === "params") {
                const depth = Math.min(12, Math.max(1, Number(msg.depth) || 4));
                void this.renderFlow(panel, key, ep, depth, !!msg.dtos);
            }
        });

        const cfg = vscode.workspace.getConfiguration("sharpgraphFlow");
        await this.renderFlow(panel, key, ep,
            Math.min(12, Math.max(1, cfg.get<number>("defaultDepth", 2))),
            cfg.get<boolean>("includeDtos", false));
    }

    /** Consulta endpoint_flow con los niveles/contratos dados y pinta el panel. */
    private async renderFlow(
        panel: vscode.WebviewPanel,
        key: string,
        ep: EndpointItem,
        depth: number,
        dtos: boolean,
    ): Promise<void> {
        try {
            const config = vscode.workspace.getConfiguration("sharpgraphFlow");
            const maxNodes = Math.min(200, Math.max(5, config.get<number>("maxNodes", 80)));
            const json = await this.client.callTool("endpoint_flow", {
                endpoint: `${ep.verb} ${ep.route}`,
                maxDepth: depth,
                maxNodes,
                includeDtos: dtos,
            });
            const flow = JSON.parse(json) as FlowData;
            // el panel pudo cerrarse (o cambiar de params) mientras respondía el motor
            if (!this.panels.has(key)) {
                return;
            }
            panel.webview.html = renderFlowHtml(flow, {
                nonce: crypto.randomBytes(16).toString("hex"),
                includeInfra: config.get<boolean>("includeInfra", false),
                depth,
                includeDtos: dtos,
            });
        } catch (err) {
            const message = err instanceof Error ? err.message : String(err);
            if (this.panels.has(key)) {
                panel.webview.html = errorHtml(message);
            }
        }
    }

    dispose(): void {
        for (const p of this.panels.values()) {
            p.dispose();
        }
        this.panels.clear();
    }
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

function loadingHtml(nonce: string, ep: EndpointItem): string {
    return `<!DOCTYPE html><html><head><meta charset="utf-8">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; script-src 'nonce-${nonce}';">
<style>body{background:var(--vscode-editor-background,#1f1f1f);color:var(--vscode-editor-foreground,#ccc);
font-family:"Segoe UI",sans-serif;display:grid;place-items:center;height:100vh;margin:0}
.spin{display:flex;gap:10px;align-items:center;color:var(--vscode-descriptionForeground,#9a9a9a)}</style></head>
<body><div class="spin"><span>⚡</span> Generando flujo de <b>${ep.verb} ${ep.route}</b> con SharpGraph…</div></body></html>`;
}

function errorHtml(message: string): string {
    const safe = message.replace(/&/g, "&amp;").replace(/</g, "&lt;");
    return `<!DOCTYPE html><html><head><meta charset="utf-8">
<style>body{background:#1f1f1f;color:#ccc;font-family:"Segoe UI",sans-serif;display:grid;place-items:center;height:100vh;margin:0}
.err{border:1px solid #5a1d1d;background:#2a1515;padding:18px 22px;border-radius:8px;max-width:520px;line-height:1.5}</style></head>
<body><div class="err"><b>SharpGraph Flow</b><br>${safe}</div></body></html>`;
}
