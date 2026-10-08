import * as path from "path";
import * as vscode from "vscode";
import { SharpGraphClient } from "./mcp";
import { EndpointsWebviewProvider, groupKeyOf, type EndpointItem } from "./endpoints";
import { FlowPanelManager, panelRequestFor } from "./flowPanel";
import { DiagramViewProvider } from "./diagramView";
import { parseTraceEndpoint } from "./trace";

export function activate(context: vscode.ExtensionContext): void {
    const client = new SharpGraphClient();

    const output = vscode.window.createOutputChannel("SharpGraph Flow");
    context.subscriptions.push(output);
    const log = (msg: string) => output.appendLine(`[${new Date().toLocaleTimeString()}] ${msg}`);
    client.onStderr((text) => output.append(text));

    /** true mientras haya un scan en curso (los clics durante el indexado
     * reciben un mensaje honesto en vez de un error de catálogo vacío) */
    let indexing = false;
    const indexingHint = () =>
        indexing ? "Estoy indexando la solución — espera a que acabe la notificación de progreso y vuelve a clicar." : null;

    const panels = new FlowPanelManager(
        client,
        context.extensionUri,
        (msg) => log(msg),
        // clic derecho en un command/query del panel grande → traza al endpoint
        (name) => void traceToEndpoint(name),
        indexingHint,
    );
    const diagram = new DiagramViewProvider(
        client,
        // "Abrir" en el diagrama lateral → panel grande (con el flujo YA
        // consultado: pinta sin re-llamar al motor)
        (req) => {
            void panels.open(req);
        },
        (msg) => log(msg),
        indexingHint,
        // clic derecho en un command/query del diagrama lateral
        (name) => void traceToEndpoint(name),
    );
    const provider = new EndpointsWebviewProvider(
        // clic en una fila → SOLO ese endpoint (del controller sale su método)
        (ep) => void diagram.show(ep),
        // clic en el NOMBRE del controlador → flujo completo del controlador
        (name) => void diagram.showController(name),
        // ⤢ en la fila → panel grande del editor (consulta propia con timeout)
        (ep) => {
            const cfg = vscode.workspace.getConfiguration("sharpgraphFlow");
            void panels.open(
                panelRequestFor(
                    ep,
                    Math.min(12, Math.max(1, cfg.get<number>("defaultDepth", 2))),
                    cfg.get<boolean>("includeDtos", false),
                ),
            );
        },
        (cmd) => void (cmd === "scan" ? scanRepo() : configureServer()),
        // expandir un grupo → refresco parcial de sus endpoints
        (key) => void refreshGroup(key),
        // errores JS del webview → canal de salida (diagnóstico)
        (message) => log(`error en la vista Endpoints: ${message}`),
    );

    context.subscriptions.push(
        client,
        panels,
        diagram,
        provider,
        vscode.window.registerWebviewViewProvider(DiagramViewProvider.viewId, diagram, {
            webviewOptions: { retainContextWhenHidden: true },
        }),
        vscode.window.registerWebviewViewProvider(EndpointsWebviewProvider.viewId, provider, {
            webviewOptions: { retainContextWhenHidden: true },
        }),
    );

    client.onExit((code) => {
        log(`el servidor terminó (código ${code})`);
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

    /** mutex de refresco: el watcher, el arranque y "Recargar" no pueden pisarse;
     * si llega una petición durante un refresco, se encola. */
    let refreshing = false;
    let refreshQueued = false;
    async function refreshTree(): Promise<void> {
        if (refreshing) {
            refreshQueued = true;
            return;
        }
        refreshing = true;
        const t0 = Date.now();
        try {
            const solution = await resolveSolution();
            if (!solution) {
                log("sin solución que indexar (ni .sln/.csproj ni solutionPath)");
                provider.setStatus({
                    kind: "error",
                    message: "No hay ningún workspace con .sln/.csproj.",
                    hint: "Configura sharpgraphFlow.solutionPath.",
                });
                return;
            }
            // 1) siembra instantánea desde la caché local de la última vez:
            //    la lista pinta YA, antes de hablar con el motor
            const cacheKey = "endpoints:" + solution;
            const cached = context.workspaceState.get<EndpointItem[] | undefined>(cacheKey);
            if (cached?.length && !provider.hasData) {
                provider.setData(cached);
                log(`sembrado desde caché local: ${cached.length} endpoints`);
            }
            // 2) servidor vivo: solo se arranca si no lo hay — y SIN path, para que
            //    initialize sea instantáneo (nada de recargar la caché del motor aquí).
            //    Si el exe del disco es MÁS NUEVO que el proceso vivo, se reinicia:
            //    las actualizaciones del motor aplican sin recargar la ventana.
            const serverPath = vscode.workspace.getConfiguration("sharpgraphFlow").get<string>("serverPath", "SharpGraph");
            if (client.isRunning && client.isStale(serverPath)) {
                log("el ejecutable de SharpGraph cambió: reiniciando el servidor");
                client.stop();
            }
            if (!client.isRunning || currentSolution !== solution) {
                await client.start(serverPath);
                currentSolution = solution;
            }
            // 3) scan incremental (SOLO aquí y en el botón: re-hashea toda la
            //    solución y puede tardar minutos — el motor se auto-vigila después)
            //    + catálogo. CON PROGRESO VISIBLE: el indexado completo tras una
            //    invalidación de caché tarda minutos y no puede ser silencioso.
            indexing = true;
            try {
                await vscode.window.withProgress(
                    {
                        location: vscode.ProgressLocation.Notification,
                        title: `SharpGraph: indexando ${path.basename(solution)}…`,
                        cancellable: false,
                    },
                    async (progress) => {
                        progress.report({ message: "scan incremental (puede tardar en la primera carga)" });
                        await client.callTool("scan", { path: solution });
                        progress.report({ message: "catalogando endpoints…" });
                        await listAndApply(solution);
                    },
                );
            } finally {
                indexing = false;
            }
            log(`refresco de ${path.basename(solution)}: ${provider.totalCount} endpoints en ${Date.now() - t0} ms`);
        } catch (err) {
            const message = err instanceof Error ? err.message : String(err);
            log(`refresco falló: ${message}`);
            provider.setStatus({
                kind: "error",
                message: message.includes("ENOENT")
                    ? "No encuentro el ejecutable de SharpGraph."
                    : message,
                hint: message.includes("ENOENT")
                    ? "Configura sharpgraphFlow.serverPath (publica el motor con publish.ps1)."
                    : undefined,
            });
        } finally {
            refreshing = false;
            if (refreshQueued) {
                refreshQueued = false;
                void refreshTree();
            }
        }
    }

    /** lista del servidor vivo → árbol + caché local. Ignora resultados vacíos:
     * no borra lo sembrado si el motor aún no ha escaneado. */
    async function listAndApply(solution: string): Promise<void> {
        const json = await client.callTool("list_endpoints");
        const items = (JSON.parse(json).endpoints ?? []) as EndpointItem[];
        if (items.length === 0) {
            return;
        }
        provider.setData(items);
        await context.workspaceState.update("endpoints:" + solution, items);
    }

    /** refresco LIGERO del watcher: SOLO lista del servidor vivo — el motor se
     * auto-vigila tras su primer scan; aquí NUNCA se lanza un scan (re-hashear
     * 5.000 ficheros bloqueaba el servidor y todos los clics detrás). */
    async function quickRefresh(): Promise<void> {
        if (!client.isRunning || refreshing) {
            return;
        }
        try {
            const solution = currentSolution ?? await resolveSolution();
            if (!solution) {
                return;
            }
            const t0 = Date.now();
            await listAndApply(solution);
            log(`catálogo actualizado (sin scan): ${provider.totalCount} endpoints en ${Date.now() - t0} ms`);
        } catch {
            // silencioso: el siguiente evento del watcher lo reintenta
        }
    }

    /** refresco parcial al expandir un grupo: lista fresca de ese controlador
     * (contraer+expandir = actualizar, sin recargar el resto) */
    async function refreshGroup(key: string): Promise<void> {
        if (!client.isRunning) {
            return;
        }
        try {
            const json = await client.callTool("list_endpoints");
            const items = (JSON.parse(json).endpoints ?? []) as EndpointItem[];
            provider.updateGroup(key, items.filter((ep) => groupKeyOf(ep) === key));
        } catch {
            // silencioso: el grupo se queda con lo que tenía
        }
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

        const name = path.basename(target);
        try {
            await vscode.window.withProgress(
                {
                    location: vscode.ProgressLocation.Notification,
                    title: `SharpGraph: indexando ${name}…`,
                    cancellable: false,
                },
                async () => {
                    // el servidor corre sin path (init instantáneo); el scan
                    // incremental va como tool — si ya está caliente, casi gratis
                    if (!client.isRunning || currentSolution !== target) {
                        await client.start(
                            vscode.workspace.getConfiguration("sharpgraphFlow").get<string>("serverPath", "SharpGraph"));
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
            return;
        }
        vscode.window.showInformationMessage(`SharpGraph: índice actualizado (${name}).`);
        await refreshTree();
    }

    /** Clic derecho en un command/query del diagrama: traza (hacia atrás) el
     * endpoint HTTP que lo invoca, selecciona su fila en el árbol de la barra
     * lateral y carga su flujo en el diagrama. */
    async function traceToEndpoint(name: string): Promise<void> {
        if (!client.isRunning) {
            void vscode.window.showWarningMessage("SharpGraph Flow: el motor no está arrancado todavía.");
            return;
        }
        let text: string;
        try {
            text = await client.callTool("trace_to_endpoints", { typeName: name });
        } catch (err) {
            const message = err instanceof Error ? err.message : String(err);
            void vscode.window.showErrorMessage(`SharpGraph Flow: ${message}`);
            return;
        }
        const hit = parseTraceEndpoint(text);
        if (!hit) {
            void vscode.window.showWarningMessage(
                `SharpGraph Flow: «${name}» no desemboca en ningún endpoint HTTP indexado.`,
            );
            return;
        }
        log(`clic derecho en ${name} → ${hit.verb} ${hit.route} (${hit.controller}.${hit.method}, ${hit.label})`);
        const ep: EndpointItem =
            provider.findByRoute(hit.verb, hit.route) ?? {
                controller: hit.controller,
                controllerName: hit.controller.split(".").pop() ?? hit.controller,
                verb: hit.verb,
                route: hit.route,
                method: hit.method,
                file: null,
                line: 0,
            };
        provider.selectEndpoint(ep);
        await diagram.show(ep);
    }

    async function configureServer(): Promise<void> {        const exe = await vscode.window.showInputBox({
            prompt: "Ruta al ejecutable SharpGraph (publicado con publish.ps1)",
            value: vscode.workspace.getConfiguration("sharpgraphFlow").get<string>("serverPath", "SharpGraph"),
        });
        if (exe) {
            await vscode.workspace.getConfiguration("sharpgraphFlow").update(
                "serverPath", exe, vscode.ConfigurationTarget.Workspace);
            client.stop();
            void refreshTree();
        }
    }

    // arranque perezoso: al abrir la vista (el propio onView ya activa la extensión)
    void refreshTree();

    // re-catálogo ligero con el watcher del propio VS Code (SIN scan — el motor
    // se auto-vigila tras su primer scan): debounce 2 s por cambios .cs
    let rescanTimer: NodeJS.Timeout | undefined;
    const codeWatcher = vscode.workspace.createFileSystemWatcher("**/*.cs");
    const scheduleRescan = () => {
        if (rescanTimer) {
            clearTimeout(rescanTimer);
        }
        rescanTimer = setTimeout(() => void quickRefresh(), 2000);
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
        vscode.commands.registerCommand("sharpgraphFlow.configureServer", () => {
            void configureServer();
        }),
    );
}
