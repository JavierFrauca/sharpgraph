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

        const config = vscode.workspace.getConfiguration("sharpgraphFlow");
        const panel = vscode.window.createWebviewPanel(
            "sharpgraphFlow.diagram",
            `${ep.verb} ${ep.route}`,
            vscode.ViewColumn.Active,
            { enableScripts: true, retainContextWhenHidden: true },
        );
        panel.iconPath = vscode.Uri.joinPath(this.extensionUri, "media", "activity-icon.svg");
        this.panels.set(key, panel);
        panel.onDidDispose(() => this.panels.delete(key));

        panel.webview.html = loadingHtml(crypto.randomBytes(16).toString("hex"), ep);

        try {
            const maxNodes = Math.min(200, Math.max(5, config.get<number>("maxNodes", 80)));
            const json = await this.client.callTool("endpoint_flow", {
                endpoint: `${ep.verb} ${ep.route}`,
                maxNodes,
            });
            const flow = JSON.parse(json) as FlowData;
            // el panel pudo cerrarse mientras el motor respondía
            if (!this.panels.has(key)) {
                return;
            }
            const nonce = crypto.randomBytes(16).toString("hex");
            panel.webview.html = renderFlowHtml(flow, {
                nonce,
                includeInfra: config.get<boolean>("includeInfra", false),
            });
            panel.webview.onDidReceiveMessage((msg) => {
                if (msg?.type === "open" && typeof msg.file === "string" && msg.file.length > 0) {
                    openAtLine(vscode.Uri.file(msg.file), Number(msg.line) || 0);
                }
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

export async function openAtLine(uri: vscode.Uri, line: number): Promise<void> {
    const editor = await vscode.window.showTextDocument(uri, {
        preview: false,
        viewColumn: vscode.ViewColumn.Beside,
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
