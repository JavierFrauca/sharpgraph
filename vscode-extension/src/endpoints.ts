import * as path from "path";
import * as vscode from "vscode";
import { SharpGraphClient } from "./mcp";

export interface EndpointItem {
    controller: string;
    controllerName: string;
    verb: string;
    route: string;
    method: string;
    file: string | null;
    line: number;
}

interface TreeNode {
    controller?: EndpointItem;
    endpoints?: EndpointItem[];
}

/** Árbol del ActivityBar: agrupado por controlador (o origen minimal API), con
 * verbos de color. La fuente es list_endpoints() del motor SharpGraph. */
export class EndpointsProvider implements vscode.TreeDataProvider<TreeNode> {
    private data: EndpointItem[] = [];
    private status: { kind: "ok" } | { kind: "error"; message: string; hint?: string } | { kind: "loading" } = { kind: "loading" };

    private readonly _onDidChange = new vscode.EventEmitter<TreeNode[] | undefined>();
    readonly onDidChangeTreeData = this._onDidChange.event;

    constructor(
        private readonly client: SharpGraphClient,
        private readonly verbsDir: () => vscode.Uri,
    ) {}

    invalidate(): void {
        this._onDidChange.fire(undefined);
    }

    setStatus(status: typeof this.status): void {
        this.status = status;
        this.invalidate();
    }

    /** Reconsulta el catálogo al motor (scan + list_endpoints). */
    async refresh(solutionPath: string): Promise<void> {
        this.setStatus({ kind: "loading" });
        try {
            await this.client.start(
                vscode.workspace.getConfiguration("sharpgraphFlow").get<string>("serverPath", "SharpGraph"),
                solutionPath,
            );
            const json = await this.client.callTool("list_endpoints");
            const parsed = JSON.parse(json);
            this.data = (parsed.endpoints ?? []) as EndpointItem[];
            this.setStatus({ kind: "ok" });
        } catch (err) {
            const message = err instanceof Error ? err.message : String(err);
            this.setStatus({
                kind: "error",
                message: message.includes("ENOENT")
                    ? "No encuentro el ejecutable de SharpGraph."
                    : message,
                hint: message.includes("ENOENT")
                    ? "Configura sharpgraphFlow.serverPath (publica el motor con publish.ps1)."
                    : undefined,
            });
        }
        this.invalidate();
    }

    getTreeItem(element: TreeNode): vscode.TreeItem {
        if (element.endpoints) {
            const item = new vscode.TreeItem(
                element.endpoints[0].controllerName,
                vscode.TreeItemCollapsibleState.Expanded,
            );
            item.description = `${element.endpoints.length} endpoint(s)`;
            item.contextValue = "controller";
            item.tooltip = element.endpoints[0].controller;
            return item;
        }
        const ep = element.controller!;
        const item = new vscode.TreeItem(ep.route || "/", vscode.TreeItemCollapsibleState.None);
        item.description = ep.method;
        item.tooltip = new vscode.MarkdownString(
            `**${ep.verb} ${ep.route}**\n\n${ep.controllerName}.${ep.method}` +
            (ep.file ? `\n\n\`${ep.file}:${ep.line}\`` : ""),
        );
        item.iconPath = {
            light: vscode.Uri.joinPath(this.verbsDir(), `${ep.verb.toLowerCase()}.svg`),
            dark: vscode.Uri.joinPath(this.verbsDir(), `${ep.verb.toLowerCase()}.svg`),
        };
        item.contextValue = "endpoint";
        item.command = {
            command: "sharpgraphFlow.openEndpoint",
            title: "Ver flujo",
            arguments: [ep],
        };
        return item;
    }

    getChildren(element?: TreeNode): TreeNode[] {
        if (this.status.kind === "error") {
            return [];
        }
        if (this.status.kind === "loading") {
            return [];
        }
        if (!element) {
            // agrupa por controlador; los minimal APIs planos (clave "GET /users") van sueltos
            const groups = new Map<string, EndpointItem[]>();
            for (const ep of this.data) {
                const key = /^[A-Z]+ \//.test(ep.controller) ? ep.controller : ep.controllerName;
                if (!groups.has(key)) {
                    groups.set(key, []);
                }
                groups.get(key)!.push(ep);
            }
            return [...groups.entries()]
                .sort((a, b) => a[0].localeCompare(b[0]))
                .map(([_, endpoints]) => ({ endpoints }));
        }
        return (element.endpoints ?? []).map((ep) => ({ controller: ep }));
    }

    /** Mensaje vacío/error para el content del tree view, con botón de acción
     * (command links) según el estado. */
    get message(): string | vscode.MarkdownString | undefined {
        if (this.status.kind === "loading") {
            return "$(sync~spinner) Escaneando solución con SharpGraph…";
        }
        if (this.status.kind === "error") {
            const md = new vscode.MarkdownString(`$(error) ${this.status.message}\n\n`);
            if (this.status.hint) {
                md.appendMarkdown(`${this.status.hint}\n\n`);
                md.appendMarkdown(`[$(gear) Configurar ruta del motor](command:sharpgraphFlow.configureServer "SharpGraph Flow")`);
            } else {
                md.appendMarkdown(`[$(database) Indexar / actualizar repo](command:sharpgraphFlow.scanRepo "SharpGraph Flow")`);
            }
            md.isTrusted = true;
            return md;
        }
        if (this.data.length === 0) {
            const md = new vscode.MarkdownString(
                "Sin endpoints indexados todavía.\n\n" +
                `[$(database) Indexar / actualizar este repo](command:sharpgraphFlow.scanRepo "SharpGraph Flow")`);
            md.isTrusted = true;
            return md;
        }
        return undefined;
    }

    get totalCount(): number {
        return this.data.length;
    }

    /** Ruta corta (para el título del webview). */
    static shortController(controller: string): string {
        return path.basename(controller);
    }
}
