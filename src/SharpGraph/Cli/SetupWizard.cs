using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SharpGraph.Cli;

/// <summary>
/// Menú interactivo ASCII para registrar SharpGraph en cualquier cliente MCP.
/// Reemplaza funcionalmente a install.ps1 / install.sh con una experiencia
/// multiplataforma nativa dentro del propio binario.
/// </summary>
internal static class SetupWizard
{
    private static readonly string[] ClientNames =
        ["Claude Code", "Cursor", "Cline", "Continue", "Zed", "OpenCode", "Crush", "Generic (JSON)"];

    private static readonly string[] ClientKeys =
        ["claude", "cursor", "cline", "continue", "zed", "opencode", "crush", "generic"];

    public static async Task<int> Run(string[] args)
    {
        // Parsear flags no interactivos
        var client = GetFlag(args, "--client");
        var installPath = GetFlag(args, "--install-path");
        var noHook = args.Any(a => a.Equals("--no-hook", StringComparison.OrdinalIgnoreCase));

        if (client is not null)
        {
            // Modo no interactivo
            var key = client.ToLowerInvariant();
            if (!ClientKeys.Contains(key))
            {
                Console.Error.WriteLine($"Cliente desconocido: {client}");
                Console.Error.WriteLine($"Disponibles: {string.Join(", ", ClientKeys)}");
                return 1;
            }
            return await InstallForClient(key, installPath, noHook);
        }

        // Modo interactivo: menú ASCII
        return await InteractiveMenu(installPath, noHook);
    }

    private static async Task<int> InteractiveMenu(string? installPath, bool noHook)
    {
        Console.WriteLine();
        Console.WriteLine("  ╔══════════════════════════════════════════╗");
        Console.WriteLine("  ║            SharpGraph Setup              ║");
        Console.WriteLine("  ╠══════════════════════════════════════════╣");
        Console.WriteLine("  ║  Selecciona tu cliente MCP:              ║");
        Console.WriteLine("  ║                                          ║");
        for (var i = 0; i < ClientNames.Length; i++)
            Console.WriteLine($"  ║  {i + 1}. {ClientNames[i],-37} ║");
        Console.WriteLine("  ║                                          ║");
        Console.WriteLine("  ║  q. Salir                                ║");
        Console.WriteLine("  ╚══════════════════════════════════════════╝");
        Console.Write("  Opción: ");

        var input = Console.ReadLine()?.Trim().ToLowerInvariant();
        Console.WriteLine();

        if (input is "q" or "quit" or "exit") return 0;
        if (!int.TryParse(input, out var choice) || choice < 1 || choice > ClientKeys.Length)
        {
            Console.Error.WriteLine($"Opción inválida: {input}");
            return 1;
        }

        return await InstallForClient(ClientKeys[choice - 1], installPath, noHook);
    }

    private static async Task<int> InstallForClient(string clientKey, string? installPath, bool noHook)
    {
        // 1. Resolver ruta del binario actual
        var exePath = Environment.ProcessPath!;
        var exeName = Path.GetFileName(exePath);

        // 2. Resolver ruta de instalación
        installPath ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "tools", "SharpGraph");
        var target = Path.Combine(installPath, exeName);

        // 3. Copiar binario
        Console.WriteLine($"Instalando en {installPath} ...");
        Directory.CreateDirectory(installPath);
        File.Copy(exePath, target, overwrite: true);
        // En Unix, asegurar permiso de ejecución
        if (!OperatingSystem.IsWindows())
        {
            try { File.SetUnixFileMode(target, UnixFileMode.UserExecute | UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  --  No se pudo setear permiso Unix: {ex.Message}");
            }
        }
        Console.WriteLine($"  OK  {exeName} copiado a {target}");

        // 4. Registrar en el cliente
        await RegisterClient(clientKey, target, noHook);

        // 5. Resumen
        Console.WriteLine();
        Console.WriteLine("Instalación completada. Reinicia tu cliente MCP para aplicar los cambios.");
        Console.WriteLine();
        if (clientKey == "claude" && !noHook)
        {
            Console.WriteLine("A partir de entonces, al abrir cualquier proyecto C# en Claude Code");
            Console.WriteLine("el grafo se construirá automáticamente en segundo plano.");
        }
        else
        {
            Console.WriteLine($"Recuerda pedir al LLM scan(\"ruta/a/tu/proyecto\") la primera vez.");
        }
        Console.WriteLine();
        Console.WriteLine("Para verificar: sharpgraph stats");
        Console.WriteLine("Para ayuda completa: sharpgraph help");

        return 0;
    }

    private static async Task RegisterClient(string clientKey, string exePath, bool noHook)
    {
        var escapedPath = exePath.Replace("\\", "\\\\");

        switch (clientKey)
        {
            case "claude":
                await RegisterClaude(exePath, noHook);
                break;

            case "cursor":
                RegisterJsonConfig(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cursor", "mcp.json"),
                    BuildMcpJson(exePath, rootKey: "mcpServers"),
                    "Cursor");
                break;

            case "cline":
                var clineBase = OperatingSystem.IsMacOS()
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "Code", "User", "globalStorage", "saoudrizwan.claude-dev", "settings")
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Code", "User", "globalStorage", "saoudrizwan.claude-dev", "settings");
                RegisterJsonConfig(
                    Path.Combine(clineBase, "cline_mcp_settings.json"),
                    BuildMcpJson(exePath, rootKey: "mcpServers", withEnv: true),
                    "Cline");
                break;

            case "continue":
                RegisterJsonConfig(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".continue", "config.json"),
                    BuildMcpJson(exePath, rootKey: "mcpServers"),
                    "Continue");
                break;

            case "zed":
                var zedBase = OperatingSystem.IsMacOS()
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "Zed")
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "zed");
                RegisterJsonConfig(
                    Path.Combine(zedBase, "settings.json"),
                    BuildMcpJson(exePath, rootKey: "context_servers"),
                    "Zed");
                break;

            case "opencode":
                RegisterJsonConfig(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".opencode", "config.json"),
                    BuildMcpJson(exePath, rootKey: "mcpServers"),
                    "OpenCode");
                break;

            case "crush":
                RegisterJsonConfig(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".crush", "config.json"),
                    BuildMcpJson(exePath, rootKey: "mcpServers"),
                    "Crush");
                break;

            case "generic":
                Console.WriteLine();
                Console.WriteLine("Configuración MCP para tu cliente (añade esto a tu fichero de config):");
                Console.WriteLine();
                Console.WriteLine(BuildMcpJson(exePath, rootKey: "mcpServers"));
                Console.WriteLine();
                Console.WriteLine("Ver docs/CLIENTS.md para ejemplos por cliente específico.");
                break;
        }
    }

    private static async Task RegisterClaude(string exePath, bool noHook)
    {
        // Intentar usar `claude mcp add` si está disponible
        if (await TryClaudeCli(exePath)) return;

        // Fallback: escribir en ~/.claude/settings.json directamente
        var settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude", "settings.json");
        var claudeDir = Path.GetDirectoryName(settingsPath);
        if (claudeDir is not null && !Directory.Exists(claudeDir))
        {
            Console.WriteLine("  --  No se encontró Claude Code (~/.claude no existe).");
            Console.WriteLine("      Instálalo desde https://claude.ai/code y re-ejecuta setup.");
            return;
        }

        Console.WriteLine("  OK  Claude Code detectado");
        if (!noHook)
            ConfigureHook(settingsPath, exePath);
    }

    private static async Task<bool> TryClaudeCli(string exePath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? "claude" : "claude",
                Arguments = $"mcp add -s user sharpgraph \"{exePath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            // remove primero (idempotente)
            try
            {
                var rm = Process.Start(new ProcessStartInfo
                {
                    FileName = "claude",
                    Arguments = "mcp remove sharpgraph -s user",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                });
                if (rm is not null) await rm.WaitForExitAsync();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  --  No se pudo setear permiso Unix: {ex.Message}");
            }

            var p = Process.Start(psi);
            if (p is null) return false;
            await p.WaitForExitAsync();
            if (p.ExitCode == 0)
            {
                Console.WriteLine("  OK  MCP registrado para Claude Code vía CLI");
                return true;
            }
        }
        catch
        {
            // `claude` CLI not available or failed; fall back to manual config
        }
        return false;
    }

    private static void ConfigureHook(string settingsPath, string exePath)
    {
        Console.WriteLine("  Configurando hook CwdChanged (auto-scan)...");

        try
        {
            var raw = File.Exists(settingsPath) ? File.ReadAllText(settingsPath) : "{}";
            var root = (JsonNode.Parse(raw) as JsonObject) ?? new JsonObject();

            if (root["hooks"] is not JsonObject hooksObj)
            {
                hooksObj = new JsonObject();
                root["hooks"] = hooksObj;
            }
            if (hooksObj["CwdChanged"] is not JsonArray cwdArray)
            {
                cwdArray = new JsonArray();
                hooksObj["CwdChanged"] = cwdArray;
            }

            // Verificar si ya existe
            foreach (var item in cwdArray)
                if (item?["hooks"] is JsonArray inner)
                    foreach (var h in inner)
                        if (h?["server"]?.GetValue<string>() == "sharpgraph")
                        {
                            Console.WriteLine("  --  Hook CwdChanged ya configurado, sin cambios");
                            return;
                        }

            cwdArray.Add(new JsonObject
            {
                ["hooks"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "mcp_tool",
                        ["server"] = "sharpgraph",
                        ["tool"] = "Scan",
                        ["input"] = new JsonObject { ["path"] = "${cwd}" },
                        ["async"] = true,
                        ["statusMessage"] = "SharpGraph indexing...",
                    }
                }
            });

            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            File.WriteAllText(settingsPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"  OK  Hook CwdChanged configurado en {settingsPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  --  No se pudo configurar el hook: {ex.Message}");
        }
    }

    private static void RegisterJsonConfig(string configPath, string defaultJson, string clientName)
    {
        try
        {
            var dir = Path.GetDirectoryName(configPath)!;
            if (File.Exists(configPath))
            {
                Console.WriteLine($"  --  {configPath} ya existe. Edítalo a mano y añade el servidor sharpgraph.");
                Console.WriteLine("      (ver docs/CLIENTS.md para el snippet)");
                return;
            }
            Directory.CreateDirectory(dir);
            File.WriteAllText(configPath, defaultJson);
            Console.WriteLine($"  OK  MCP registrado para {clientName} en {configPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  --  No se pudo escribir {configPath}: {ex.Message}");
        }
    }

    /// <summary>Genera el JSON de configuración MCP usando System.Text.Json (sin raw strings).</summary>
    private static string BuildMcpJson(string exePath, string rootKey, bool withEnv = false)
    {
        var server = new JsonObject
        {
            ["command"] = exePath,
            ["args"] = new JsonArray(),
        };
        if (withEnv)
            server["env"] = new JsonObject();

        var root = new JsonObject
        {
            [rootKey] = new JsonObject { ["sharpgraph"] = server }
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    // ────────────────────────── helpers ──────────────────────────

    private static string? GetFlag(string[] args, string flag)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i].Equals(flag, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }
}
