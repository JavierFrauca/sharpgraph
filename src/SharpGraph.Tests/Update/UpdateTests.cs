using System.IO.Compression;
using System.Text.Json;
using SharpGraph.Update;
using Xunit;

namespace SharpGraph.Tests.Update;

/// <summary>
/// Auto-actualización: lógica pura del checker (comparador de tags, RID, selección
/// de asset, SHA256SUMS, frescura de caché) y el núcleo del swap con un zip real.
/// La red NO se toca en ningún test.
/// </summary>
public class UpdateTests : IDisposable
{
    private readonly string _dir;

    public UpdateTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sgupdate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    // ─────────────────────────── comparador de tags ───────────────────────────

    [Theory]
    [InlineData("v2.4.0", true)]
    [InlineData("2.4.0", true)]
    [InlineData("v2.3.0", false)]   // igual
    [InlineData("v2.2.9", false)]   // anterior
    [InlineData("v2.10.0", true)]   // comparación numérica, no léxica
    [InlineData("v2.3.0-beta", false)] // prerelease no parsea → mejor no avisar
    [InlineData("", false)]
    [InlineData("garbage", false)]
    [InlineData(null, false)]
    public void IsNewer_ComparesVersionsNumerically(string? tag, bool expected)
        => Assert.Equal(expected, UpdateChecker.IsNewer(tag));

    // ─────────────────────────── RID ───────────────────────────

    [Theory]
    [InlineData(true, false, false, "X64", "win-x64")]
    [InlineData(true, false, false, "Arm64", "win-x64")] // emulación
    [InlineData(false, true, false, "X64", "linux-x64")]
    [InlineData(false, false, true, "X64", "osx-arm64")] // Rosetta
    [InlineData(false, false, true, "Arm64", "osx-arm64")]
    public void MapRid_MapsSupportedPlatforms(bool win, bool linux, bool mac, string arch, string rid)
        => Assert.Equal(rid, UpdateChecker.MapRid(win, linux, mac, arch));

    [Fact]
    public void MapRid_RejectsLinuxArm64()
        => Assert.Throws<PlatformNotSupportedException>(() => UpdateChecker.MapRid(false, true, false, "Arm64"));

    // ─────────────────────────── assets ───────────────────────────

    [Fact]
    public void PickAsset_FindsRidZipAndIgnoresTheRest()
    {
        var assets = JsonDocument.Parse("""
            [
              { "name": "SharpGraph-win-x64.zip",   "browser_download_url": "https://x/SharpGraph-win-x64.zip" },
              { "name": "SharpGraph-linux-x64.zip",  "browser_download_url": "https://x/SharpGraph-linux-x64.zip" },
              { "name": "SharpGraph-linux-x64.tar.gz", "browser_download_url": "https://x/SharpGraph-linux-x64.tar.gz" },
              { "name": "SHA256SUMS.txt", "browser_download_url": "https://x/SHA256SUMS.txt" }
            ]
            """).RootElement;

        var hit = UpdateChecker.PickAsset(assets, "win-x64");
        Assert.NotNull(hit);
        Assert.Equal("https://x/SharpGraph-win-x64.zip", hit.Value.Url);

        Assert.Null(UpdateChecker.PickAsset(assets, "linux-arm64"));
    }

    // ─────────────────────────── caché ───────────────────────────

    [Fact]
    public void IsCacheFresh_RequiresFreshTimestampAndParsableTag()
    {
        Assert.True(UpdateChecker.IsCacheFresh(DateTimeOffset.UtcNow.AddHours(-1), "v2.4.0"));
        Assert.False(UpdateChecker.IsCacheFresh(DateTimeOffset.UtcNow.AddHours(-25), "v2.4.0"));
        Assert.False(UpdateChecker.IsCacheFresh(DateTimeOffset.UtcNow, null));
        Assert.False(UpdateChecker.IsCacheFresh(DateTimeOffset.UtcNow, "?"));
    }

    // ─────────────────────────── SHA256SUMS ───────────────────────────

    [Fact]
    public void VerifySha256_ValidatesAgainstStandardSumsFile()
    {
        var file = Path.Combine(_dir, "pkg.zip");
        File.WriteAllBytes(file, [1, 2, 3, 4, 5]);

        using var sha = System.Security.Cryptography.SHA256.Create();
        using var stream = File.OpenRead(file);
        var hex = Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
        var sums = $"deadbeef  otro.zip\n{hex}  pkg.zip\n";

        Assert.True(UpdateChecker.VerifySha256(file, sums));
        Assert.False(UpdateChecker.VerifySha256(file, $"0000  pkg.zip\n"));
        // nombre ausente de la lista → no verificable → false (aborta)
        Assert.False(UpdateChecker.VerifySha256(file, "deadbeef  otro.zip\n"));
    }

    // ─────────────────────────── ApplyUpdate (swap real) ───────────────────────────

    [Fact]
    public void ApplyUpdate_ReplacesBinaryAndKeepsOldForRollback()
    {
        // paquete falso con el layout real: carpeta SharpGraph-<rid>/ con el binario
        // (staging separado: el zip no puede escribirse dentro del árbol que comprime)
        var staging = Path.Combine(_dir, "pkg");
        var pkgDir = Path.Combine(staging, "SharpGraph-win-x64");
        Directory.CreateDirectory(pkgDir);
        File.WriteAllText(Path.Combine(pkgDir, "SharpGraph.exe"), "NEW BINARY");

        var archive = Path.Combine(_dir, "SharpGraph-win-x64.zip");
        ZipFile.CreateFromDirectory(staging, archive);

        // target "instalado" con contenido viejo
        var installDir = Path.Combine(_dir, "install");
        Directory.CreateDirectory(installDir);
        var target = Path.Combine(installDir, "SharpGraph.exe");
        File.WriteAllText(target, "OLD BINARY");

        SelfUpdater.ApplyUpdate(archive, target);

        Assert.Equal("NEW BINARY", File.ReadAllText(target));
        Assert.Equal("OLD BINARY", File.ReadAllText(target + ".old"));
    }

    [Fact]
    public void ApplyUpdate_ThrowsAndLeavesTargetIntact_WhenPackageHasNoBinary()
    {
        var staging = Path.Combine(_dir, "pkg");
        var pkgDir = Path.Combine(staging, "SharpGraph-win-x64");
        Directory.CreateDirectory(pkgDir);
        File.WriteAllText(Path.Combine(pkgDir, "install.ps1"), "whatever");

        var archive = Path.Combine(_dir, "empty.zip");
        ZipFile.CreateFromDirectory(staging, archive);

        var target = Path.Combine(_dir, "SharpGraph.exe");
        File.WriteAllText(target, "OLD BINARY");

        Assert.ThrowsAny<Exception>(() => SelfUpdater.ApplyUpdate(archive, target));
        // el rollback deja el target como estaba
        Assert.Equal("OLD BINARY", File.ReadAllText(target));
    }
}
