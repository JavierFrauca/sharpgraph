using System.Text.Json;

namespace SharpGraph.Update;

/// <summary>
/// Comprobación de versión nueva contra los releases públicos de GitHub
/// (https://api.github.com/repos/JavierFrauca/sharpgraph/releases/latest).
///
/// Política deliberada: SOLO AVISA, nunca descarga ni sustituye nada por su
/// cuenta — la actualización es siempre un acto explícito (`sharpgraph update`),
/// el estándar de las CLIs de desarrollo. Salvaguardas:
///   - caché de 24 h: la API sin autenticar admite 60 req/h por IP, así que el
///     coste de red es como mucho una petición al día entre todas las invocaciones;
///   - fail-silent: cualquier fallo (sin conexión, rate-limit, JSON raro) se
///     traga y no avisa — nunca bloquea ni estorba;
///   - opt-out con SHARPGRAPH_NO_UPDATE_CHECK=1;
///   - en modo MCP, stats() refresca la caché en background (no bloquea la
///     respuesta) y lee el aviso SOLO de la caché: la latencia de las tools no
///     paga nunca la red.
/// </summary>
public static class UpdateChecker
{
    private const string ReleasesApi = "https://api.github.com/repos/JavierFrauca/sharpgraph/releases/latest";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);

    private static readonly HttpClient Api = new() { Timeout = TimeSpan.FromSeconds(3) };

    private static int _refreshing; // 0 = parado, 1 = en curso (evita refrescos concurrentes)

    static UpdateChecker()
        => Api.DefaultRequestHeaders.UserAgent.ParseAdd(
            $"SharpGraph/{VersionInfo.Current} (+https://github.com/JavierFrauca/sharpgraph)");

    public static bool IsDisabled()
        => Environment.GetEnvironmentVariable("SHARPGRAPH_NO_UPDATE_CHECK") is not null;

    // ─────────────────────────── entradas ───────────────────────────

    /// <summary>Check síncrono para la CLI (cacheado: solo toca la red si la caché
    /// tiene >24 h). Devuelve el último tag o null si no se pudo saber.</summary>
    public static string? CheckInteractive(bool force = false)
    {
        if (IsDisabled()) return null;
        try
        {
            if (!force)
            {
                var (cached, age) = ReadCache();
                if (cached is not null && age < CacheTtl) return cached;
            }
            var tag = FetchLatestTag();
            if (tag is not null) WriteCache(tag);
            return tag;
        }
        catch { return null; }
    }

    /// <summary>Refresco en background para el modo MCP: lanza la comprobación si la
    /// caché está vencida y no espera el resultado (stats() responde al instante).</summary>
    public static void RefreshInBackground()
    {
        if (IsDisabled() || Interlocked.Exchange(ref _refreshing, 1) == 1) return;
        Task.Run(async () =>
        {
            try
            {
                var (cached, age) = ReadCache();
                if (cached is not null && age < CacheTtl) return;
                var tag = await FetchLatestTagAsync();
                if (tag is not null) WriteCache(tag);
            }
            catch { /* fail-silent */ }
            finally { Interlocked.Exchange(ref _refreshing, 0); }
        });
    }

    /// <summary>Aviso para modo MCP/CLI a partir de SOLO la caché (cero red).
    /// Cadena vacía si no hay nada que anunciar.</summary>
    public static string CachedNotice()
    {
        try
        {
            var (tag, age) = ReadCache();
            if (tag is null || age >= CacheTtl || !IsNewer(tag)) return "";
            return $"\nNueva versión disponible: {tag} (tienes {VersionInfo.Current}) — ejecuta 'sharpgraph update'";
        }
        catch { return ""; }
    }

    /// <summary>Aviso de una línea a stderr para los comandos CLI. Fail-silent:
    /// un problema de red o de caché jamás estorba al comando que se pidió.</summary>
    public static void PrintNoticeToConsole()
    {
        try
        {
            var tag = CheckInteractive();
            if (tag is not null && IsNewer(tag))
                Console.Error.WriteLine($"Nueva versión disponible: {tag} (tienes {VersionInfo.Current}) — " +
                                        "ejecuta 'sharpgraph update'. Silencia con SHARPGRAPH_NO_UPDATE_CHECK=1.");
        }
        catch { /* fail-silent */ }
    }

    // ─────────────────────────── helpers puros (testeables) ───────────────────────────

    /// <summary>"v2.3.0" → 2.3.0. Tags con sufijo prerelease o basura no parsean:
    /// sin aviso antes que un falso positivo.</summary>
    public static bool TryParseTag(string? tag, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(tag)) return false;
        var clean = tag.TrimStart('v', 'V');
        return Version.TryParse(clean, out version!);
    }

    /// <summary>¿El tag remoto es estrictamente posterior a la versión local?</summary>
    public static bool IsNewer(string? remoteTag)
        => TryParseTag(remoteTag, out var remote)
           && Version.TryParse(VersionInfo.Current, out var local)
           && remote > local;

    /// <summary>RID de los artefactos que publica release.yml, a partir del SO y la
    /// arquitectura del proceso. win/linux arm64 no tienen build propio: win cae a
    /// win-x64 (emulación), linux-arm64 se rechaza con mensaje.</summary>
    public static string MapRid(bool isWindows, bool isLinux, bool isMac, string arch) => (isWindows, isLinux, isMac, arch) switch
    {
        (_, true, _, "Arm64") => throw new PlatformNotSupportedException(
            "No hay build linux-arm64 de SharpGraph. Descarga el código y compila con: dotnet publish -r linux-arm64"),
        (_, true, _, _) => "linux-x64",
        (_, _, true, _) => "osx-arm64", // los mac x64 lo ejecutan vía Rosetta
        (true, _, _, _) => "win-x64",
        _ => throw new PlatformNotSupportedException($"Plataforma no soportada: {arch}"),
    };

    /// <summary>Localiza el asset del release para un RID. Devuelve (nombre, url)
    /// o null si el release no lo trae (p.ej. releases anteriores a los zip unix).</summary>
    public static (string Name, string Url)? PickAsset(JsonElement assets, string rid)
    {
        foreach (var a in assets.EnumerateArray())
        {
            var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
            var url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
            if (name is not null && url is not null &&
                name.Equals($"SharpGraph-{rid}.zip", StringComparison.OrdinalIgnoreCase))
                return (name, url);
        }
        return null;
    }

    /// <summary>Localiza el asset .vsix de la extensión SharpGraph Flow en un release.
    /// Devuelve (nombre, url) o null si el release no lo trae (anteriores a la extensión).</summary>
    public static (string Name, string Url)? PickVsixAsset(JsonElement assets)
    {
        foreach (var a in assets.EnumerateArray())
        {
            var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
            var url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
            if (name is not null && url is not null && IsVsixAsset(name))
                return (name, url);
        }
        return null;
    }

    /// <summary>vsce nombra el paquete "sharpgraph-flow-&lt;versión&gt;.vsix" (name de package.json).</summary>
    public static bool IsVsixAsset(string name)
        => name.StartsWith("sharpgraph-flow-", StringComparison.OrdinalIgnoreCase)
           && name.EndsWith(".vsix", StringComparison.OrdinalIgnoreCase);

    /// <summary>¿El par (marca temporal, tag) de la caché sirve para avisar?</summary>
    public static bool IsCacheFresh(DateTimeOffset checkedUtc, string? tag)
        => tag is not null
           && DateTimeOffset.UtcNow - checkedUtc < CacheTtl
           && TryParseTag(tag, out _);

    /// <summary>Verifica el hash SHA-256 del fichero contra la línea correspondiente
    /// de un SHA256SUMS.txt estándar ("<hex>  <nombre>"). Devuelve false si el nombre
    /// no está en la lista (mejor no verificar que verificar mal).</summary>
    public static bool VerifySha256(string filePath, string sha256SumsContent)
    {
        if (!TryGetExpectedSha256(sha256SumsContent, filePath, out var expected)) return false;

        using var sha = System.Security.Cryptography.SHA256.Create();
        using var stream = File.OpenRead(filePath);
        var actual = Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
        return actual == expected;
    }

    /// <summary>Extrae de un SHA256SUMS.txt el hash esperado para un fichero.
    /// false si el nombre no tiene línea (p.ej. releases que no sumaban el .vsix).</summary>
    public static bool TryGetExpectedSha256(string sha256SumsContent, string filePath, out string expected)
    {
        var name = Path.GetFileName(filePath);
        expected = "";
        foreach (var line in sha256SumsContent.Split('\n'))
        {
            var parts = line.Split(' ', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && (parts[1] == name || parts[1].EndsWith("/" + name, StringComparison.Ordinal)))
                expected = parts[0].ToLowerInvariant();
        }
        return expected.Length > 0;
    }

    // ─────────────────────────── red y caché ───────────────────────────

    private static string? FetchLatestTag() => FetchLatestTagAsync().GetAwaiter().GetResult();

    private static async Task<string?> FetchLatestTagAsync()
    {
        using var response = await Api.GetAsync(ReleasesApi);
        if (!response.IsSuccessStatusCode) return null; // 403 rate-limit, offline, etc.
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.TryGetProperty("tag_name", out var tag) ? tag.GetString() : null;
    }

    /// <summary>JSON del último release completo (tag + assets), sin usar la caché.</summary>
    internal static async Task<(string? Tag, JsonElement Assets)> FetchReleaseAsync()
    {
        using var response = await Api.GetAsync(ReleasesApi);
        if (!response.IsSuccessStatusCode) return (null, default);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var tag = doc.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
        var assets = doc.RootElement.TryGetProperty("assets", out var a) ? a.Clone() : default;
        return (tag, assets);
    }

    private static (string? Tag, TimeSpan Age) ReadCache()
    {
        var path = CachePath();
        if (!File.Exists(path)) return (null, TimeSpan.MaxValue);
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var tag = doc.RootElement.TryGetProperty("latestTag", out var t) ? t.GetString() : null;
        var checkedUtc = doc.RootElement.TryGetProperty("checkedUtc", out var c) && DateTimeOffset.TryParse(c.GetString(), out var dt)
            ? dt : DateTimeOffset.MinValue;
        return (tag, DateTimeOffset.UtcNow - checkedUtc);
    }

    private static void WriteCache(string tag)
    {
        var path = CachePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var payload = JsonSerializer.Serialize(new
        {
            checkedUtc = DateTimeOffset.UtcNow.ToString("O"),
            latestTag = tag,
        });
        File.WriteAllText(path, payload);
    }

    private static string CachePath()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SharpGraph", "update-check.json");
}
