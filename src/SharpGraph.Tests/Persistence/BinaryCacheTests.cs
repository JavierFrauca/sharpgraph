using System.Text.Json;
using SharpGraph.Graph;
using SharpGraph.Persistence;
using Xunit;

namespace SharpGraph.Tests.Persistence;

/// <summary>
/// Round-trip de la caché binaria (.sgcache): un fragmento rico (todas las
/// colecciones) se guarda, se carga y debe ser idéntico campo a campo.
/// Cubre además la migración desde cachés .json legacy y el mantenimiento del
/// directorio (limpieza de .tmp huérfanos, eviction LRU).
/// </summary>
public class BinaryCacheTests : IDisposable
{
    private readonly string _dir;
    private readonly string _codeFile;

    // Un snippet que activa TODAS las colecciones del FileFragment: nodos,
    // aristas, endpoints, call-sites, DI, miembros, alias de using.
    private const string RichCode = """
        using System;
        using System.Collections.Generic;
        using Legacy = Corp.Old.Api;

        namespace Corp.App.Billing;

        /// <summary>Calcula el importe de una factura.</summary>
        public interface IInvoiceCalculator
        {
            decimal Compute(int invoiceId);
        }

        public sealed class InvoiceCalculator : IInvoiceCalculator
        {
            private readonly ITaxRepo _repo;

            public InvoiceCalculator(ITaxRepo repo) => _repo = repo;

            public decimal Compute(int invoiceId)
            {
                var tax = _repo.GetRate(invoiceId);
                return tax * 1.21m;
            }
        }

        [ApiController]
        [Route("api/billing")]
        public sealed class BillingController : ControllerBase
        {
            private readonly IInvoiceCalculator _calc;

            public BillingController(IInvoiceCalculator calc) => _calc = calc;

            [HttpPost("compute")]
            public IActionResult Compute(int id) => Ok(_calc.Compute(id));
        }

        public static class Registration
        {
            public static void AddBilling(IServiceCollection services)
                => services.AddScoped<IInvoiceCalculator, InvoiceCalculator>();
        }
        """;

    public BinaryCacheTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "sgtests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        _codeFile = Path.Combine(_dir, "Billing.cs");
        File.WriteAllText(_codeFile, RichCode);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp */ }
    }

    private static FileFragment ParseReal(string path) =>
        GraphTestHarness.ParseSnippet(File.ReadAllText(path), path)
        ?? throw new InvalidOperationException("el snippet de test no debería fallar al parsear");

    [Fact]
    public void RoundTrip_PreservesFragment()
    {
        var scanPath = Path.Combine(_dir, "solution-folder");
        Directory.CreateDirectory(scanPath);
        var store = new GraphStore(_dir);
        var original = ParseReal(_codeFile);

        store.Save(scanPath, [original]);
        // el fichero nuevo es binario y reemplaza a cualquier json
        Assert.True(Directory.EnumerateFiles(_dir, "*.sgcache").Any());
        Assert.False(Directory.EnumerateFiles(_dir, "*.json").Any());

        var ok = store.TryLoad(scanPath, out var loaded);
        Assert.True(ok);
        var frag = Assert.Single(loaded);

        Assert.Equal(original.FilePath, frag.FilePath);
        Assert.Equal(original.Hash, frag.Hash);
        Assert.Equal(original.Usings, frag.Usings);
        Assert.Equal(original.Aliases, frag.Aliases);
        Assert.Equal(original.Nodes, frag.Nodes);
        Assert.Equal(original.Edges, frag.Edges);
        Assert.Equal(original.Endpoints, frag.Endpoints);
        Assert.Equal(original.CallSites, frag.CallSites);
        Assert.Equal(original.DiBindings, frag.DiBindings);
        Assert.Equal(original.Members, frag.Members);
        Assert.Equal(original.ReturnSignatures, frag.ReturnSignatures);
        Assert.Equal(original.Literals, frag.Literals);
        // los records con listas anidadas (Receiver/Initializer) comparan por
        // referencia: desglosamos propiedad a propiedad para ver el contenido real.
        Assert.Equal(original.PendingCallSites.Count, frag.PendingCallSites.Count);
        for (var i = 0; i < original.PendingCallSites.Count; i++)
        {
            var a = original.PendingCallSites[i];
            var b = frag.PendingCallSites[i];
            Assert.Equal((a.CallerType, a.CallerMember, a.CalleeMember, a.Ns, a.Line),
                         (b.CallerType, b.CallerMember, b.CalleeMember, b.Ns, b.Line));
            Assert.Equal(a.Receiver, b.Receiver);
        }
        Assert.Equal(original.PendingLocals.Count, frag.PendingLocals.Count);
        for (var i = 0; i < original.PendingLocals.Count; i++)
        {
            var a = original.PendingLocals[i];
            var b = frag.PendingLocals[i];
            Assert.Equal((a.DeclaringType, a.DeclaringMember, a.LocalName, a.Ns),
                         (b.DeclaringType, b.DeclaringMember, b.LocalName, b.Ns));
            Assert.Equal(a.Initializer, b.Initializer);
        }
    }

    [Fact]
    public void TryLoad_Reads_LegacyJson_And_NextSave_Migrates()
    {
        var scanPath = Path.Combine(_dir, "legacy-solution");
        Directory.CreateDirectory(scanPath);
        var store = new GraphStore(_dir);
        var fragment = ParseReal(_codeFile);

        // pre-historia: una caché v1 JSON con el mismo ParserVersion (9)
        var jsonOpts = new JsonSerializerOptions();
        var envelope = new
        {
            Version = 9,
            Fragments = new[] { fragment }
        };
        var key = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(
            System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(scanPath).ToLowerInvariant())));
        File.WriteAllText(Path.Combine(_dir, key + ".json"),
            JsonSerializer.Serialize(envelope, jsonOpts));

        var ok = store.TryLoad(scanPath, out var loaded);
        Assert.True(ok);
        Assert.Single(loaded);
        Assert.Equal(fragment.Hash, loaded[0].Hash);

        // el siguiente save la reemplaza por binaria
        store.Save(scanPath, loaded);
        Assert.False(File.Exists(Path.Combine(_dir, key + ".json")));
        Assert.True(File.Exists(Path.Combine(_dir, key + ".sgcache")));
    }

    [Fact]
    public void Constructor_Deletes_Old_Orphaned_Tmp()
    {
        var old = Path.Combine(_dir, "deadbeef.sgcache.aaaa1111.tmp");
        var fresh = Path.Combine(_dir, "deadbeef.sgcache.bbbb2222.tmp");
        File.WriteAllText(old, "x");
        File.WriteAllText(fresh, "x");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow - TimeSpan.FromHours(2));
        File.SetLastWriteTimeUtc(fresh, DateTime.UtcNow - TimeSpan.FromSeconds(30));

        _ = new GraphStore(_dir);

        Assert.False(File.Exists(old));   // huérfano de un proceso muerto: fuera
        Assert.True(File.Exists(fresh));  // save en curso de otro proceso: se respeta
    }

    [Fact]
    public void Save_Evicts_Oldest_Caches_Beyond_Limit()
    {
        var store = new GraphStore(_dir);
        var fragment = ParseReal(_codeFile);

        // 12 cachés "antiguas" de otras soluciones
        for (var i = 0; i < 12; i++)
        {
            var f = Path.Combine(_dir, $"cache{i:d2}.sgcache");
            File.WriteAllText(f, "old");
            File.SetLastWriteTimeUtc(f, DateTime.UtcNow - TimeSpan.FromHours(i + 1));
        }

        store.Save(Path.Combine(_dir, "the-new-one"), [fragment]);

        var remaining = Directory.EnumerateFiles(_dir, "*.sgcache")
            .Where(f => new FileInfo(f).Length > 10) // los fake "old" pesan 3 bytes
            .Count();
        var fakes = Directory.EnumerateFiles(_dir, "*.sgcache")
            .Count(f => new FileInfo(f).Length == 3);
        Assert.True(fakes <= 10, $"debería haber evictado hasta 10 cachés, quedan {fakes}");
        Assert.Equal(1, remaining);
    }
}
