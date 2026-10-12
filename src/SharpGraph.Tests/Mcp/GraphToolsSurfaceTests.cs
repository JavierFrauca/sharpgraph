using System.Reflection;
using ModelContextProtocol.Server;
using SharpGraph.Mcp;
using Xunit;

namespace SharpGraph.Tests.Mcp;

/// <summary>
/// La superficie de tools MCP es contrato público: renombrar un método de
/// GraphTools cambia su nombre en el wire (InstallVscodeExtension →
/// install_vscode_extension) y rompe a los agentes que ya la usan. Este test
/// fija la tool de instalación de la extensión sin arrancar el servidor.
/// </summary>
public class GraphToolsSurfaceTests
{
    [Fact]
    public void InstallVscodeExtension_IsAnIdempotentToolWithOptionalVsixPath()
    {
        var method = typeof(GraphTools).GetMethod(nameof(GraphTools.InstallVscodeExtension));
        Assert.NotNull(method);

        var attr = method!.GetCustomAttribute<McpServerToolAttribute>();
        Assert.NotNull(attr);
        Assert.True(attr!.Idempotent, "reinstalar la misma versión es seguro: idempotente");

        // vsixPath es opcional: la tool debe poder llamarse sin argumentos
        // (descarga del último release)
        var param = method.GetParameters().Single(p => p.Name == "vsixPath");
        Assert.True(param.IsOptional);
        Assert.Null(param.DefaultValue);
    }
}
