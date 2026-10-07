using System.Diagnostics;
using SharpGraph.Update;

namespace SharpGraph.Cli;

/// <summary>
/// `sharpgraph install vscode`: instala la extensión SharpGraph Flow en VS Code.
/// Descarga el .vsix del último release de GitHub (misma maquinaria que
/// `sharpgraph update`: API de releases + SHA-256 contra SHA256SUMS.txt) y
/// ejecuta `code --install-extension`. Con --vsix &lt;ruta&gt; instala un fichero
/// local (offline / desarrollo). Localiza la CLI de VS Code ('code', y como
/// alternativa 'code-insiders' y 'codium') en el PATH y en las rutas de
/// instalación habituales de cada SO.
/// </summary>
internal static class InstallCommand
{
    /// <summary>Objetivos reconocidos tras 'install'. Hoy solo la extensión VS Code.</summary>
    private static readonly HashSet<string> Targets = new(StringComparer.OrdinalIgnoreCase)
        { "vscode", "vscode-extension", "vscodeextension", "vsix" };

    /// <summary>CLIs de editores aceptadas, por orden de preferencia.</summary>
    private static readonly string[] CliNames = ["code", "code-insiders", "codium"];

    public static async Task<int> Run(string[] args)
    {
        var target = args.FirstOrDefault();
        if (target is null || !Targets.Contains(target))
        {
            if (target is not null)
                Console.Error.WriteLine($"Objetivo desconocido: '{target}'. Hoy solo hay: vscode (extensión SharpGraph Flow).");
            Console.Error.WriteLine("USO: sharpgraph install vscode [--vsix <ruta>]");
            return 1;
        }

        var vsixPath = GetFlagValue(args, "--vsix");

        var codeCli = LocateCodeCli();
        if (codeCli is null)
        {
            Console.Error.WriteLine("No encuentro la CLI de VS Code ('code') en el PATH ni en las rutas de instalación habituales.");
            Console.Error.WriteLine("Instala VS Code o añade su carpeta 'bin' al PATH. También puedes instalar a mano:");
            Console.Error.WriteLine("  code --install-extension <sharpgraph-flow-x.y.z.vsix>");
            return 1;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "sharpgraph-install-" + Guid.NewGuid().ToString("N"));
        try
        {
            if (vsixPath is null)
            {
                vsixPath = await DownloadVsixFromRelease(tempDir);
                if (vsixPath is null) return 1;
            }
            else if (!File.Exists(vsixPath))
            {
                Console.Error.WriteLine($"No existe el fichero: {vsixPath}");
                return 1;
            }

            return await RunCodeInstall(codeCli, vsixPath);
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>Descarga el .vsix del último release a tempDir. null = fallo (ya informado).</summary>
    private static async Task<string?> DownloadVsixFromRelease(string tempDir)
    {
        Console.WriteLine($"SharpGraph {VersionInfo.Current} — buscando el .vsix de la extensión en GitHub Releases …");
        string? tag;
        System.Text.Json.JsonElement assets;
        try
        {
            (tag, assets) = await UpdateChecker.FetchReleaseAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"No se pudo consultar el último release ({ex.Message}). ¿Sin conexión?");
            return null;
        }
        if (tag is null)
        {
            Console.Error.WriteLine("No se pudo leer el último release (rate-limit de la API o release inexistente).");
            return null;
        }

        var vsix = UpdateChecker.PickVsixAsset(assets);
        if (vsix is null)
        {
            Console.Error.WriteLine($"El release {tag} no trae sharpgraph-flow-*.vsix.");
            Console.Error.WriteLine("Descárgalo a mano: https://github.com/JavierFrauca/sharpgraph/releases");
            return null;
        }

        WarnIfVersionMismatch(vsix.Value.Name);

        Directory.CreateDirectory(tempDir);
        var downloaded = Path.Combine(tempDir, vsix.Value.Name);
        Console.WriteLine($"Descargando {vsix.Value.Name} …");
        await SelfUpdater.DownloadToAsync(vsix.Value.Url, downloaded);

        var sums = await SelfUpdater.TryDownloadSumsAsync(vsix.Value.Url);
        if (sums is null || !UpdateChecker.TryGetExpectedSha256(sums, downloaded, out _))
            Console.WriteLine("Aviso: este release no suma el .vsix en SHA256SUMS.txt; verificación solo por HTTPS.");
        else if (UpdateChecker.VerifySha256(downloaded, sums))
            Console.WriteLine("SHA-256 verificado.");
        else
        {
            Console.Error.WriteLine("El SHA-256 del .vsix descargado NO coincide con SHA256SUMS.txt: instalación abortada.");
            return null;
        }

        return downloaded;
    }

    /// <summary>Motor y extensión evolucionan en pareja: avisa (sin bloquear) si el
    /// .vsix del release no coincide con la versión del binario en ejecución.</summary>
    private static void WarnIfVersionMismatch(string vsixName)
    {
        var stem = vsixName["sharpgraph-flow-".Length..^".vsix".Length];
        if (!Version.TryParse(stem, out var vsix)) return;
        if (!Version.TryParse(VersionInfo.Current, out var engine)) return;

        if (vsix > engine)
            Console.WriteLine($"Nota: la extensión ({vsix}) es más nueva que tu binario ({VersionInfo.Current}). " +
                              "Considera 'sharpgraph update' para alinear motor y extensión.");
        else if (vsix < engine)
            Console.WriteLine($"Nota: el release aún no trae la extensión al nivel de tu binario ({VersionInfo.Current} > {vsix}). " +
                              "Si compilas desde fuente: cd vscode-extension && npm run package.");
    }

    /// <summary>`code --install-extension <vsix> --force`. En Windows los .cmd no son
    /// ejecutables PE: hay que pasar por cmd.exe /c.</summary>
    private static async Task<int> RunCodeInstall(string codeCli, string vsixPath)
    {
        Console.WriteLine($"Instalando en VS Code vía {Path.GetFileName(codeCli)} …");
        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        if (OperatingSystem.IsWindows())
        {
            // ArgumentList cita cada ruta por separado y cmd.exe /c, con más de dos
            // comillas en la línea, se carga la primera y la última (regla de cmd /?):
            // con VS Code en "Program Files" la línea sale troceada. Un único
            // Arguments envuelto en un par extra de comillas lo desmonta bien.
            psi.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            psi.Arguments = BuildWindowsCmdLine(codeCli, Path.GetFullPath(vsixPath));
        }
        else
        {
            psi.FileName = codeCli;
            psi.ArgumentList.Add("--install-extension");
            psi.ArgumentList.Add(vsixPath);
            psi.ArgumentList.Add("--force");
        }

        using var process = Process.Start(psi)!;
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (stdout.Trim().Length > 0) Console.WriteLine(stdout.TrimEnd());
        if (stderr.Trim().Length > 0) Console.Error.WriteLine(stderr.TrimEnd());

        if (process.ExitCode != 0)
        {
            Console.Error.WriteLine("La instalación de la extensión devolvió un error.");
            return process.ExitCode;
        }

        Console.WriteLine();
        Console.WriteLine("Extensión SharpGraph Flow instalada. Abre (o recarga) VS Code para verla.");
        return 0;
    }

    /// <summary>Línea de comandos para cmd.exe /c en Windows: las rutas van citadas
    /// y TODO va envuelto en un par extra de comillas, que es lo que cmd espera para
    /// no trocear la orden cuando las rutas tienen espacios (p.ej. "Program Files").
    /// Internal + puro para poder testear la forma exacta.</summary>
    internal static string BuildWindowsCmdLine(string codeCli, string vsixPath)
        => $"/c \"\"{codeCli}\" --install-extension \"{vsixPath}\" --force\"";

    internal static string? LocateCodeCli()
    {
        foreach (var name in CliNames)
        {
            if (FindOnPath(name) is { } onPath) return onPath;
            if (KnownLocations(name).FirstOrDefault(File.Exists) is { } known) return known;
        }
        return null;
    }

    private static string? FindOnPath(string name)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv)) return null;

        // En Windows la CLI es un .cmd (bin\code.cmd); el .exe del editor no instala extensiones.
        var exeNames = OperatingSystem.IsWindows()
            ? new[] { name + ".cmd", name + ".bat" }
            : new[] { name };

        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            foreach (var exe in exeNames)
            {
                var candidate = Path.Combine(dir, exe);
                if (File.Exists(candidate)) return candidate;
            }
        return null;
    }

    /// <summary>Rutas de instalación habituales de la CLI, por SO.</summary>
    private static IEnumerable<string> KnownLocations(string name)
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
        {
            var sub = name switch
            {
                "code-insiders" => "Microsoft VS Code Insiders",
                "codium" => "VSCodium",
                _ => "Microsoft VS Code",
            };
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", sub, "bin", name + ".cmd");
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), sub, "bin", name + ".cmd");
        }
        else if (OperatingSystem.IsMacOS())
        {
            var app = name switch
            {
                "code-insiders" => "Visual Studio Code - Insiders.app",
                "codium" => "VSCodium.app",
                _ => "Visual Studio Code.app",
            };
            var bin = Path.Combine(app, "Contents", "Resources", "app", "bin", name);
            yield return Path.Combine("/Applications", bin);
            yield return Path.Combine(userProfile, "Applications", bin);
        }
        else if (OperatingSystem.IsLinux())
        {
            yield return "/usr/bin/" + name;
            yield return "/usr/local/bin/" + name;
            yield return "/snap/bin/" + name;
        }
    }

    private static string? GetFlagValue(string[] args, string flag)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i].Equals(flag, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }
}
