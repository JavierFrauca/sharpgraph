using System.Text.Json;
using SharpGraph.Update;
using Xunit;

namespace SharpGraph.Tests.Update;

/// <summary>
/// `sharpgraph install vscode`: lógica pura del selector del asset .vsix y del
/// chequeo de cobertura de SHA256SUMS.txt. La red y la CLI de VS Code NO se
/// tocan en ningún test.
/// </summary>
public class InstallCommandTests
{
    // ─────────────────────────── PickVsixAsset ───────────────────────────

    [Fact]
    public void PickVsixAsset_FindsFlowVsixAndIgnoresTheRest()
    {
        var assets = JsonDocument.Parse("""
            [
              { "name": "SharpGraph-win-x64.zip", "browser_download_url": "https://x/SharpGraph-win-x64.zip" },
              { "name": "sharpgraph-flow-2.5.6.vsix", "browser_download_url": "https://x/sharpgraph-flow-2.5.6.vsix" },
              { "name": "SHA256SUMS.txt", "browser_download_url": "https://x/SHA256SUMS.txt" }
            ]
            """).RootElement;

        var hit = UpdateChecker.PickVsixAsset(assets);
        Assert.NotNull(hit);
        Assert.Equal("sharpgraph-flow-2.5.6.vsix", hit.Value.Name);
        Assert.Equal("https://x/sharpgraph-flow-2.5.6.vsix", hit.Value.Url);
    }

    [Fact]
    public void PickVsixAsset_ReturnsNull_WhenReleaseHasNoVsix()
    {
        var assets = JsonDocument.Parse("""
            [
              { "name": "SharpGraph-win-x64.zip", "browser_download_url": "https://x/SharpGraph-win-x64.zip" },
              { "name": "SHA256SUMS.txt", "browser_download_url": "https://x/SHA256SUMS.txt" }
            ]
            """).RootElement;

        Assert.Null(UpdateChecker.PickVsixAsset(assets));
    }

    [Theory]
    [InlineData("sharpgraph-flow-2.5.6.vsix", true)]
    [InlineData("sharpgraph-flow-0.1.0.vsix", true)]
    [InlineData("SharpGraph-Flow-2.5.6.VSIX", true)] // case-insensitive
    [InlineData("sharpgraph-flow.vsix", false)]      // sin versión
    [InlineData("other-extension-2.5.6.vsix", false)]
    [InlineData("sharpgraph-flow-2.5.6.zip", false)]
    [InlineData("SHA256SUMS.txt", false)]
    public void IsVsixAsset_MatchesOnlyTheFlowPackage(string name, bool expected)
        => Assert.Equal(expected, UpdateChecker.IsVsixAsset(name));

    // ─────────────────────── cobertura de SHA256SUMS ───────────────────────

    [Fact]
    public void TryGetExpectedSha256_FindsTheVsixLine()
    {
        var sums = "abc123  SharpGraph-win-x64.zip\ndef456  sharpgraph-flow-2.5.6.vsix\n";
        Assert.True(UpdateChecker.TryGetExpectedSha256(sums, Path.Combine("tmp", "sharpgraph-flow-2.5.6.vsix"), out var hash));
        Assert.Equal("def456", hash);
    }

    [Fact]
    public void TryGetExpectedSha256_ReturnsFalse_WhenFileNotInSums()
    {
        // releases antiguos no sumaban el .vsix: hay que distinguir
        // "no está en la lista" de "hash no coincide"
        var sums = "abc123  SharpGraph-win-x64.zip\n";
        Assert.False(UpdateChecker.TryGetExpectedSha256(sums, Path.Combine("tmp", "sharpgraph-flow-2.5.6.vsix"), out _));
    }
}
