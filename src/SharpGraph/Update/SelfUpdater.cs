using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace SharpGraph.Update;

/// <summary>
/// Actualización explícita del binario (`sharpgraph update`): descarga el release
/// de GitHub para el RID actual, verifica SHA-256 si el release publica
/// SHA256SUMS.txt, y sustituye el ejecutable IN SITU con rollback.
///
/// Nunca se ejecuta sola: solo la invoca el usuario desde la CLI. Si la sesión
/// MCP está viva, sigue con el binario viejo hasta que el cliente rearranque el
/// servidor (el fichero renombrado mantiene el handle del proceso actual).
///
/// El truco Windows: un exe en ejecución no puede sobrescribirse, pero SÍ puede
/// renombrarse — target → target.old, escribir el nuevo en target, y al siguiente
/// arranque se borra el .old (ver CleanupStaleOldBinary).
/// </summary>
public static class SelfUpdater
{
    /// <summary>Flujo completo del comando. Devuelve el exit code de la CLI.</summary>
    public static async Task<int> Run(bool checkOnly)
    {
        var exePath = Environment.ProcessPath;
        if (exePath is null || !File.Exists(exePath))
        {
            Console.Error.WriteLine("No se localiza el ejecutable en ejecución (¿dotnet run en dev?). Usa publish.ps1.");
            return 1;
        }
        if (IsSourceTree(exePath))
        {
            Console.Error.WriteLine("Parece que corres SharpGraph desde un árbol fuente (hay .git cerca). " +
                                    "Aquí la actualización es: git pull + publish.ps1.");
            return 1;
        }

        Console.WriteLine($"SharpGraph {VersionInfo.Current} — comprobando https://github.com/JavierFrauca/sharpgraph/releases …");
        string? tag;
        JsonElement assets;
        try
        {
            (tag, assets) = await UpdateChecker.FetchReleaseAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"No se pudo consultar el último release ({ex.Message}). ¿Sin conexión?");
            return 1;
        }
        if (tag is null)
        {
            Console.Error.WriteLine("No se pudo leer el último release (rate-limit de la API o release inexistente).");
            return 1;
        }

        if (!UpdateChecker.IsNewer(tag))
        {
            Console.WriteLine($"Ya tienes la última versión ({VersionInfo.Current} ≥ {tag}). Nada que hacer.");
            return 0;
        }

        if (checkOnly)
        {
            Console.WriteLine($"Hay una versión nueva: {tag} (tienes {VersionInfo.Current}).");
            Console.WriteLine("Ejecuta 'sharpgraph update' (sin --check) para actualizarte.");
            return 0;
        }

        string rid;
        try
        {
            rid = UpdateChecker.MapRid(
                isWindows: RuntimeInformation.IsOSPlatform(OSPlatform.Windows),
                isLinux: RuntimeInformation.IsOSPlatform(OSPlatform.Linux),
                isMac: RuntimeInformation.IsOSPlatform(OSPlatform.OSX),
                arch: RuntimeInformation.ProcessArchitecture.ToString());
        }
        catch (PlatformNotSupportedException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        var asset = UpdateChecker.PickAsset(assets, rid);
        if (asset is null)
        {
            Console.Error.WriteLine($"El release {tag} no trae SharpGraph-{rid}.zip. Descárgalo a mano: " +
                                    "https://github.com/JavierFrauca/sharpgraph/releases");
            return 1;
        }

        // descarga a temp + verificación + swap
        var tempDir = Path.Combine(Path.GetTempPath(), "sharpgraph-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            Console.WriteLine($"Descargando {asset.Value.Name} …");
            var archivePath = Path.Combine(tempDir, asset.Value.Name);
            await DownloadToAsync(asset.Value.Url, archivePath);

            var sums = await TryDownloadSumsAsync(asset.Value.Url);
            if (sums is not null)
            {
                if (UpdateChecker.VerifySha256(archivePath, sums))
                    Console.WriteLine("SHA-256 verificado.");
                else
                {
                    Console.Error.WriteLine("El SHA-256 del paquete descargado NO coincide con SHA256SUMS.txt: actualización abortada.");
                    return 1;
                }
            }
            else
                Console.WriteLine("Aviso: este release no publica SHA256SUMS.txt; verificación solo por HTTPS.");

            ApplyUpdate(archivePath, exePath);
            Console.WriteLine($"Actualizado a {tag}: {exePath}");
            Console.WriteLine("Reinicia tu cliente MCP (Claude Code, Cursor, …) para que recargue el servidor con la versión nueva.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Actualización fallida: {ex.Message} (el binario actual no se ha tocado).");
            return 1;
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>Núcleo testeable del swap: extrae el zip, localiza el binario con el
    /// mismo nombre que el target y lo sustituye con rollback si algo falla.</summary>
    public static void ApplyUpdate(string archivePath, string targetExePath)
    {
        var extractDir = Path.Combine(Path.GetDirectoryName(archivePath)!, "extracted");
        if (Directory.Exists(extractDir)) Directory.Delete(extractDir, recursive: true);
        ZipFile.ExtractToDirectory(archivePath, extractDir, overwriteFiles: true);

        var exeName = Path.GetFileName(targetExePath);
        var newBin = Directory.EnumerateFiles(extractDir, exeName, SearchOption.AllDirectories).FirstOrDefault()
            ?? throw new FileNotFoundException($"El paquete no contiene {exeName}.");

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            File.SetUnixFileMode(newBin, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                                       | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                                       | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        var oldPath = targetExePath + ".old";
        if (File.Exists(oldPath)) File.Delete(oldPath);
        File.Move(targetExePath, oldPath); // renombrar el exe vivo es legal en Windows
        try
        {
            File.Move(newBin, targetExePath);
        }
        catch
        {
            File.Move(oldPath, targetExePath); // rollback: deja el sistema como estaba
            throw;
        }
    }

    /// <summary>Borra el .old que dejó la actualización anterior. Se llama al arranque
    /// (CLI y MCP): el .old del proceso vivo ya no está bloqueado porque el proceso
    /// actual nació del fichero nuevo.</summary>
    public static void CleanupStaleOldBinary()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (exePath is null) return;
            var oldPath = exePath + ".old";
            if (File.Exists(oldPath)) File.Delete(oldPath);
        }
        catch { /* best effort: que no moleste nunca al arranque */ }
    }

    /// <summary>Un .git en el exe, su carpeta o sus dos niveles superiores = árbol
    /// fuente: ahí actualizar es git pull + publish.ps1, no machacar el binario.</summary>
    private static bool IsSourceTree(string exePath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(exePath))!;
        for (var i = 0; i < 3 && dir is not null; i++)
        {
            if (Directory.Exists(Path.Combine(dir, ".git"))) return true;
            dir = Path.GetDirectoryName(dir);
        }
        return false;
    }

    internal static async Task DownloadToAsync(string url, string destination)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"SharpGraph/{VersionInfo.Current}");
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync();
        await using var target = File.Create(destination);
        await source.CopyToAsync(target);
    }

    /// <summary>SHA256SUMS.txt del mismo release, si existe (null si no).</summary>
    internal static async Task<string?> TryDownloadSumsAsync(string assetUrl)
    {
        var sumsUrl = assetUrl.Replace(
            "/" + assetUrl.Split('/')[^1],
            "/SHA256SUMS.txt", StringComparison.Ordinal);
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"SharpGraph/{VersionInfo.Current}");
            using var response = await http.GetAsync(sumsUrl);
            return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync() : null;
        }
        catch { return null; }
    }
}
