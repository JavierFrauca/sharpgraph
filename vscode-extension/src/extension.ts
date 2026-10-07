import * as path from "path";
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

    // solución usada por el proceso vivo del motor (para no re-arrancar si coincide)
    let currentSolution: string | undefined;

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

    /** Carpeta → .sln/.slnx/.csproj (quick-pick si hay varios; la carpeta si no hay ninguno). */
    async function pickTargetFor(folder: vscode.WorkspaceFolder): Promise<string | undefined> {
        const found = await vscode.workspace.findFiles(
            new vscode.RelativePattern(folder, "**/*.{sln,slnx,csproj}"),
            new vscode.RelativePattern(folder, "**/{bin,obj,node_modules,.git}/**"),
            25,
        );
        const candidates = [...new Map(found.map((f) => [f.fsPath, f])).values()];
        if (candidates.length === 0) {
            return folder.uri.fsPath; // el motor acepta indexar una carpeta suelta
        }
        if (candidates.length === 1) {
            return candidates[0].fsPath;
        }
        const picked = await vscode.window.showQuickPick(
            candidates.map((c) => ({
                label: path.basename(c.fsPath),
                description: path.dirname(path.relative(folder.uri.fsPath, c.fsPath)),
                uri: c,
            })),
            { placeHolder: "SharpGraph Flow: ¿qué solución/proyecto indexo?" },
        );
        return picked?.uri.fsPath;
    }

    async function refreshTree(): Promise<void> {
        const solution = await resolveSolution();
        if (!solution) {
            provider.setStatus({ kind: "error", message: "No hay ningún workspace con .sln/.csproj.", hint: "Configura sharpgraphFlow.solutionPath." });
            provider.invalidate();
            return;
        }
        await provider.refresh(solution);
        currentSolution = solution;
        treeView.badge = provider.totalCount > 0
            ? { value: provider.totalCount, tooltip: `${provider.totalCount} endpoints indexados` }
            : undefined;
        treeView.description = provider.totalCount > 0 ? path.basename(solution) : undefined;
    }

    /** Acción del ActivityBar: indexar (primera vez) o actualizar (scan incremental)
     * el repo ABIERTO ahora mismo, con progreso visible. */
    async function scanRepo(): Promise<void> {
        const folders = vscode.workspace.workspaceFolders ?? [];
        if (folders.length === 0) {
            vscode.window.showErrorMessage("SharpGraph Flow: abre una carpeta con tu solución .NET primero.");
            return;
        }
        let folder = folders[0];
        if (folders.length > 1) {
            const picked = await vscode.window.showQuickPick(
                folders.map((f) => ({ label: f.name, folder: f })),
                { placeHolder: "SharpGraph Flow: ¿qué workspace indexo?" },
            );
            if (!picked) {
                return;
            }
            folder = picked.folder;
        }
        const target = await pickTargetFor(folder);
        if (!target) {
            return;
        }

        const serverPath = vscode.workspace.getConfiguration("sharpgraphFlow").get<string>("serverPath", "SharpGraph");
        const name = path.basename(target);
        try {
            await vscode.window.withProgress(
                {
                    location: vscode.ProgressLocation.Notification,
                    title: `SharpGraph: indexando ${name}…`,
                    cancellable: false,
                },
                async () => {
                    // el exe pre-escanea al arrancar con un path; si ya corre con el
                    // mismo repo basta el scan incremental explícito
                    if (!client.isRunning || currentSolution !== target) {
                        await client.start(serverPath, target);
                        currentSolution = target;
                    }
                    const stats = await client.callTool("scan", { path: target });
                    output.append(`scan ${target}\n${stats}\n`);
                },
            );
        } catch (err) {
            const message = err instanceof Error ? err.message : String(err);
            vscode.window.showErrorMessage(`SharpGraph Flow: no pude indexar ${name}. ${message}`);
            provider.setStatus({ kind: "error", message });
            provider.invalidate();
            return;
        }
        vscode.window.showInformationMessage(`SharpGraph: índice actualizado (${name}).`);
        await refreshTree();
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
        vscode.commands.registerCommand("sharpgraphFlow.scanRepo", () => {
            void scanRepo();
        }),
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
