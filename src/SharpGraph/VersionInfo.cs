namespace SharpGraph;

/// <summary>
/// Única fuente de verdad de la versión del producto. La referencian
/// ServerInfo (Program.cs), CliHelp (banner y help) y el update-checker
/// (compara contra el tag del último release de GitHub). Antes del
/// update-checker estaba duplicada como literal en dos sitios y cada
/// release había que recordarla dos veces.
/// Pública: los tests del comparador de tags y la extensión VS Code la leen.
/// </summary>
public static class VersionInfo
{
    public const string Current = "2.5.2";
}
