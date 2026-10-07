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

export interface TreeNode {
    key?: string;
    controller?: EndpointItem;
    endpoints?: EndpointItem[];
}

interface Group {
    key: string;
    endpoints: EndpointItem[];
}

/** Árbol del ActivityBar: agrupado por controlador (o origen minimal API), con
 * verbos de color, filtrado en vivo y expansión/contracción global. La fuente
 * es list_endpoints() del motor SharpGraph. El estado de expansión es propio:
 * el collapse-all nativo está desactivado para que no se desincronice. */
export class EndpointsProvider implements vscode.TreeDataProvider<TreeNode> {
    private data: EndpointItem[] = [];
    private filterText = "";
    /** clave de grupo → estado de expansión (ausente = defecto, expandido) */
    private readonly expanded = new Map<string, vscode.TreeItemCollapsibleState>();
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

    get filter(): string {
        return this.filterText;
    }

    setFilter(text: string): void {
        this.filterText = text.trim();
        this.invalidate();
    }

    expandAll(): void {
        for (const g of this.groups()) {
            this.expanded.set(g.key, vscode.TreeItemCollapsibleState.Expanded);
        }
        this.invalidate();
    }

    collapseAll(): void {
        for (const g of this.groups()) {
            this.expanded.set(g.key, vscode.TreeItemCollapsibleState.Collapsed);
        }
        this.invalidate();
    }

    /** Reconsulta el catálogo al motor (scan + list_endpoints). Si ya hay datos,
     * no entra en estado "loading": el árbol no se vacía durante los re-escaneos
     * del watcher (solo se vacía la primera vez o tras un error). */
    async refresh(solutionPath: string): Promise<void> {
        if (this.data.length === 0) {
            this.setStatus({ kind: "loading" });
        }
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

    /** Agrupa por controlador (minimal APIs planos van sueltos) y aplica el filtro:
     * si el grupo coincide por nombre se muestran todos sus endpoints; si no, solo
     * los que coinciden por ruta/método/verbo; grupos vacíos fuera. */
    private groups(): Group[] {
        const all = new Map<string, EndpointItem[]>();
        for (const ep of this.data) {
            const key = /^[A-Z]+ \//.test(ep.controller) ? ep.controller : ep.controllerName;
            if (!all.has(key)) {
                all.set(key, []);
            }
            all.get(key)!.push(ep);
        }
        const q = this.filterText.toLowerCase();
        if (!q) {
            return [...all.entries()]
                .sort((a, b) => a[0].localeCompare(b[0]))
                .map(([key, endpoints]) => ({ key, endpoints }));
        }
        const result: Group[] = [];
        for (const [key, endpoints] of [...all.entries()].sort((a, b) => a[0].localeCompare(b[0]))) {
            if (key.toLowerCase().includes(q)) {
                result.push({ key, endpoints });
                continue;
            }
            const matched = endpoints.filter((ep) =>
                ep.route.toLowerCase().includes(q) ||
                ep.method.toLowerCase().includes(q) ||
                ep.verb.toLowerCase() === q);
            if (matched.length > 0) {
                result.push({ key, endpoints: matched });
            }
        }
        return result;
    }

    getTreeItem(element: TreeNode): vscode.TreeItem {
        if (element.endpoints) {
            const key = element.key!;
            const item = new vscode.TreeItem(
                key,
                this.expanded.get(key) ?? vscode.TreeItemCollapsibleState.Expanded,
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
        if (this.status.kind !== "ok") {
            return [];
        }
        if (!element) {
            return this.groups().map((g) => ({ key: g.key, endpoints: g.endpoints }));
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
        if (this.filterText && this.groups().length === 0) {
            const md = new vscode.MarkdownString(
                `Sin coincidencias para \`${this.filterText}\`.\n\n` +
                `[$(clear-all) Quitar filtro](command:sharpgraphFlow.clearFilter "SharpGraph Flow")`);
            md.isTrusted = true;
            return md;
        }
        return undefined;
    }

    get totalCount(): number {
        return this.data.length;
    }

    get visibleCount(): number {
        return this.groups().reduce((n, g) => n + g.endpoints.length, 0);
    }

    /** Ruta corta (para el título del webview). */
    static shortController(controller: string): string {
        return path.basename(controller);
    }
}
