import * as vscode from "vscode";
import { SharpGraphClient } from "./mcp";
import { EndpointsProvider, type EndpointItem } from "./endpoints";
import { FlowPanelManager } from "./flowPanel";

export function activate(context: vscode.ExtensionContext): void {
    const client = new SharpGraphClient();
    const provider = new EndpointsProvider(client, () =>
        vscode.Uri.joinPath(context.extensionUri, "media", "verbs"));
    const panels = new FlowPanelManager(client, context.extensionUri);

    const treeView = vscode.window.createTreeView("sharpgraphFlow.endpoints", {
        treeDataProvider: provider,
        showCollapseAll: true,
    });
    treeView.badge = undefined;

    context.subscriptions.push(client, panels, treeView);

    const output = vscode.window.createOutputChannel("SharpGraph Flow");
    context.subscriptions.push(output);
    client.onStderr((text) => output.append(text));
    client.onExit((code) => {
        if (code !== null && code !== 0) {
            provider.setStatus({ kind: "error", message: `El servidor SharpGraph terminó (código ${code}).` });
        }
    });

    // ── resuelve la solución a indexar: config > primer .sln/.csproj del workspace > carpeta ──
    async function resolveSolution(): Promise<string | undefined> {
        const configured = vscode.workspace.getConfiguration("sharpgraphFlow").get<string>("solutionPath", "");
        if (configured.trim().length > 0) {
            return configured;
        }
        const root = vscode.workspace.workspaceFolders?.[0];
        if (!root) {
            return undefined;
        }
        for (const pattern of ["*.sln", "*.slnx", "*.csproj", "src/*.sln", "src/*.slnx"]) {
            const found = await vscode.workspace.findFiles(pattern, "**/node_modules/**", 1);
            if (found.length > 0) {
                return found[0].fsPath;
            }
        }
        return root.uri.fsPath;
    }

    async function refreshTree(): Promise<void> {
        const solution = await resolveSolution();
        if (!solution) {
            provider.setStatus({ kind: "error", message: "No hay ningún workspace con .sln/.csproj.", hint: "Configura sharpgraphFlow.solutionPath." });
            provider.invalidate();
            return;
        }
        await provider.refresh(solution);
        treeView.badge = provider.totalCount > 0
            ? { value: provider.totalCount, tooltip: `${provider.totalCount} endpoints indexados` }
            : undefined;
    }

    // arranque perezoso: al abrir la vista (el propio onView ya activa la extensión)
    void refreshTree();

    // re-scan con el watcher del propio VS Code: debounce 2 s por cambios .cs
    let rescanTimer: NodeJS.Timeout | undefined;
    const codeWatcher = vscode.workspace.createFileSystemWatcher("**/*.cs");
    const scheduleRescan = () => {
        if (rescanTimer) {
            clearTimeout(rescanTimer);
        }
        rescanTimer = setTimeout(() => void refreshTree(), 2000);
    };
    codeWatcher.onDidChange(scheduleRescan);
    codeWatcher.onDidCreate(scheduleRescan);
    codeWatcher.onDidDelete(scheduleRescan);
    context.subscriptions.push(codeWatcher);

    context.subscriptions.push(
        vscode.commands.registerCommand("sharpgraphFlow.refresh", () => {
            client.stop();
            void refreshTree();
        }),
        vscode.commands.registerCommand("sharpgraphFlow.openEndpoint", (ep: EndpointItem) => {
            void panels.open(ep);
        }),
        vscode.commands.registerCommand("sharpgraphFlow.configureServer", async () => {
            const exe = await vscode.window.showInputBox({
                prompt: "Ruta al ejecutable SharpGraph (publicado con publish.ps1)",
                value: vscode.workspace.getConfiguration("sharpgraphFlow").get<string>("serverPath", "SharpGraph"),
            });
            if (exe) {
                await vscode.workspace.getConfiguration("sharpgraphFlow").update(
                    "serverPath", exe, vscode.ConfigurationTarget.Workspace);
                client.stop();
                void refreshTree();
            }
        }),
    );
}
