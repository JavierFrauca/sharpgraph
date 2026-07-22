namespace SharpGraph.Cli;

/// <summary>
/// Help estático de la CLI. Texto completo con banner, lista de comandos
/// y ayuda detallada por comando. Generado a partir de las descripciones
/// de GraphTools.cs pero mantenido aquí como constantes para máximo control
/// del formato.
/// </summary>
internal static class CliHelp
{
    public const string Version = "2.1.0";

    // ────────────────────────── HELP GENERAL ──────────────────────────

    public static string General()
    {
        return $"""
        ╔═══════════════════════════════════════════════════════════╗
        ║                     SharpGraph v{Version}                      ║
        ║     C# code-graph MCP server + CLI for AI agents           ║
        ╚═══════════════════════════════════════════════════════════╝

          SharpGraph indexa proyectos C# (.NET) en un grafo de dependencias
          y permite navegarlos SIN leer ficheros enteros. Modela MediatR/CQRS,
          inyección de dependencias, routing ASP.NET Core y call-sites reales.

        ── ESCANEO Y ESTADO ──────────────────────────────────────────
          scan [path]         Indexa un proyecto C# (.sln, .csproj o carpeta).
                              Defecto: directorio actual (.). Incremental y
                              persistente (caché en disco).
          stats               Tipos, aristas, endpoints, call-sites, DI bindings.

        ── NAVEGACIÓN DEL GRAFO ──────────────────────────────────────
          search <patrón>     Busca tipos por nombre parcial.
          callers <tipo>      Árbol de quién depende de un tipo.
                              Flags: -d <profundidad> (defecto 3).
          usages <tipo>       De qué depende un tipo (deps salientes).
          callsites <tipo>    Dónde se invoca de VERDAD (file:line).
                              Flags: -m <miembro>, -l <límite> (defecto 50).
          trace <tipo>        Camino hacia atrás hasta endpoints HTTP.
                              Flags: -d <profundidad> (defecto 8).
          flow <tipo>         Árbol de llamadas SALIENTES (sin código).
                              Flags: -m <miembro>, -d <profundidad> (defecto 2).
          hubs                Tipos más centrales (PageRank).
                              Flags: -n <topK> (defecto 15).
          di <tipo>           Resuelve inyección de dependencias (IFoo → Foo).

        ── CÓDIGO FUENTE ─────────────────────────────────────────────
          source <tipo>       Ver código de un tipo o método concreto.
                              Flags: -m <miembro>, -l <líneas> (defecto 60).
          understand <tipo>   Clase completa + contexto del grafo en 1 llamada.
                              Flags: -l <budget> (defecto 200).
          read-file <fichero> Leer un .cs entero numerado, con marcas de tipo.
                              Flags: -l <líneas> (defecto 200).
          semantic <query>    Búsqueda semántica por intención (BM25).
                              Flags: -n <topK> (defecto 10).
          explore <patrón>    Contexto bidireccional (callers + deps + endpoints).
                              Flags: -d <depth> (defecto 2), -l <limit> (defecto 8).

        ── INSTALACIÓN Y CONFIGURACIÓN ───────────────────────────────
          setup               Menú interactivo: registra SharpGraph en tu
                              cliente MCP (Claude Code, Cursor, Cline, Zed,
                              Continue, OpenCode, Crush, genérico).
                              Flags: --client <nombre>, --no-hook,
                                     --install-path <dir>.
          help [comando]      Esta ayuda, o ayuda detallada de un comando.

        ── MODO MCP (servidor para LLMs) ─────────────────────────────
          (sin subcomando)    Arranca como servidor MCP sobre stdio.
                              Pasa un path como argumento para pre-escanear:
                                SharpGraph.exe C:\repo\MiProyecto

        ── EJEMPLOS ──────────────────────────────────────────────────
          # Escanear y explorar
          sharpgraph scan .
          sharpgraph stats
          sharpgraph hubs -n 10

          # Navegar dependencias
          sharpgraph callers IUserService -d 2
          sharpgraph callsites IUserService -m SaveChangesAsync
          sharpgraph trace IGrossService

          # Entender código
          sharpgraph flow OrderService -m Place -d 3
          sharpgraph understand OrderService
          sharpgraph source OrderService -m Place

          # Buscar
          sharpgraph search "Todo"
          sharpgraph semantic "cálculo de retención IRPF"

          # Instalar en tu cliente MCP
          sharpgraph setup
          sharpgraph setup --client cursor

        ── MÁS INFORMACIÓN ───────────────────────────────────────────
          GitHub:    https://github.com/JavierFrauca/sharpgraph
          Docs:      docs/CLIENTS.md (registro manual por cliente)
                     docs/BENCHMARK.md (benchmark de tokens)
                     docs/CALIDAD.md (comparativa de calidad)
          Licencia:  MIT
        """;
    }

    // ──────────────────── HELP POR COMANDO ────────────────────────────

    public static string ForCommand(string command) => command.ToLowerInvariant() switch
    {
        "scan" => CmdScan,
        "stats" => CmdStats,
        "search" => CmdSearch,
        "callers" or "find-callers" => CmdCallers,
        "usages" or "get-usages" => CmdUsages,
        "callsites" or "find-callsites" or "find_call_sites" => CmdCallsites,
        "trace" or "trace-to-endpoints" => CmdTrace,
        "flow" => CmdFlow,
        "hubs" => CmdHubs,
        "di" or "resolve-di" or "resolve_di" => CmdDi,
        "source" or "get-source" or "get_source" => CmdSource,
        "understand" => CmdUnderstand,
        "read-file" or "read_file" or "readfile" => CmdReadFile,
        "semantic" or "search-semantic" or "search_semantic" => CmdSemantic,
        "explore" or "explore-context" or "explore_context" => CmdExplore,
        "setup" => CmdSetup,
        "help" => CmdHelp,
        _ => $"""
            Comando desconocido: '{command}'

            Comandos disponibles: scan, stats, search, callers, usages, callsites,
            trace, flow, hubs, di, source, understand, read-file, semantic, explore,
            setup, help.

            Ejecuta 'sharpgraph help' para la lista completa.
            """
    };

    // ────────────────── TEXTO POR COMANDO ──────────────────────────

    private const string CmdScan = """
        sharpgraph scan — Indexa un proyecto C#

          Escanea todos los ficheros .cs del path indicado (excluyendo obj/, bin/,
          .git/, node_modules/) y construye el grafo de dependencias en memoria.
          Es INCREMENTAL: solo re-parsea ficheros nuevos o modificados (hash SHA1).
          Persiste la caché en disco para arranque en frío instantáneo.

          USO:
            sharpgraph scan [path]

          ARGUMENTOS:
            path              Ruta al .sln, .csproj o carpeta raíz.
                              Defecto: directorio actual (.).

          EJEMPLOS:
            sharpgraph scan                           # escanea el directorio actual
            sharpgraph scan C:\repo\MiProyecto        # ruta absoluta
            sharpgraph scan ./src/MyApp.csproj        # un csproj concreto

          TRAS ESCANEAR:
            sharpgraph stats        # verifica que se indexó correctamente
            sharpgraph hubs -n 10   # por dónde empezar a entender el código

          EQUIVALENTE MCP: scan(path)
        """;

    private const string CmdStats = """
        sharpgraph stats — Estado del grafo

          Muestra: tipos definidos, aristas, endpoints HTTP, call-sites
          (invocaciones reales), bindings DI y ficheros indexados.
          Si devuelve 0 tipos, el grafo está vacío → ejecuta 'scan'.

          USO:
            sharpgraph stats

          EQUIVALENTE MCP: stats()
        """;

    private const string CmdSearch = """
        sharpgraph search — Busca tipos por nombre

          Busca tipos DEFINIDOS en la solución por nombre parcial (insensible
          a mayúsculas). Los externos (BCL/NuGet) no aparecen.

          USO:
            sharpgraph search <patrón>

          ARGUMENTOS:
            patrón            Texto parcial a buscar en el nombre del tipo.

          EJEMPLOS:
            sharpgraph search Todo
            sharpgraph search "Service"
            sharpgraph search IGross

          EQUIVALENTE MCP: search(pattern)
        """;

    private const string CmdCallers = """
        sharpgraph callers — Árbol de dependencias inversas

          Muestra qué tipos dependen del tipo indicado, N niveles hacia arriba.
          Cada caller muestra la RELACIÓN concreta: [ctor-param] inyección,
          [call] invocación real, [implements] herencia, [sends]/[handled-by] MediatR.

          USO:
            sharpgraph callers <tipo> [-d <profundidad>]

          ARGUMENTOS:
            tipo               Nombre del tipo (sin namespace). Usa 'search' si no
                               estás seguro del nombre exacto.

          FLAGS:
            -d <profundidad>   Niveles hacia arriba (1-6, defecto 3).
                               1 = directos · 3 = red razonable · 5 = árbol profundo.

          EJEMPLOS:
            sharpgraph callers IUserService              # red directa
            sharpgraph callers IApplicationDbContext -d 2  # cadena MediatR

          EQUIVALENTE MCP: find_callers(typeName, depth)
        """;

    private const string CmdUsages = """
        sharpgraph usages — Dependencias salientes

          Muestra de qué tipos depende el tipo indicado (referencias salientes),
          agrupadas por destino con su relación y líneas.
          Útil para medir acoplamiento antes de refactorizar.

          USO:
            sharpgraph usages <tipo>

          EJEMPLOS:
            sharpgraph usages OrderService

          EQUIVALENTE MCP: get_usages(typeName)
        """;

    private const string CmdCallsites = """
        sharpgraph callsites — Dónde se invoca un tipo de verdad

          Lista invocaciones REALES (no inyección): _servicio.Metodo().
          Cada resultado muestra caller, miembro y file:line.
          Distingue "dependencia inyectada" de "llamada efectiva".

          USO:
            sharpgraph callsites <tipo> [-m <miembro>] [-l <límite>]

          FLAGS:
            -m <miembro>       Filtrar por un método concreto.
            -l <límite>        Máximo de resultados (defecto 50).

          EJEMPLOS:
            sharpgraph callsites IUserService
            sharpgraph callsites IUserService -m SaveChangesAsync

          EQUIVALENTE MCP: find_call_sites(typeName, member, limit)
        """;

    private const string CmdTrace = """
        sharpgraph trace — Camino a endpoints HTTP

          Traza el camino desde un tipo hasta los endpoints HTTP que lo invocan,
          navegando hacia atrás por el grafo. Modela MediatR de forma EXACTA:
          Controller → Command → Handler → Service.

          USO:
            sharpgraph trace <tipo> [-d <profundidad>]

          FLAGS:
            -d <profundidad>   Profundidad máxima hacia atrás (1-12, defecto 8).

          EJEMPLOS:
            sharpgraph trace IGrossService

          EQUIVALENTE MCP: trace_to_endpoints(typeName, maxDepth)
        """;

    private const string CmdFlow = """
        sharpgraph flow — Árbol de llamadas salientes

          Destila la secuencia de llamadas SALIENTES de un método, recursiva
          hasta N niveles, con fichero:línea. Muestra el árbol SIN código fuente.
          Sigue bindings DI (interface → implementación) automáticamente.

          USO:
            sharpgraph flow <tipo> [-m <miembro>] [-d <profundidad>]

          FLAGS:
            -m <miembro>       Método concreto a trazar.
                               Si se omite, resume las llamadas de cada método público (1 nivel).
            -d <profundidad>   Profundidad de recursión (1-5, defecto 2).

          EJEMPLOS:
            sharpgraph flow OrderService -m Place -d 3
              → _validator.Validate(order)        :58
              → _pricing.Calculate(order)         :74
                  → _taxRepo.GetRate(...)         :138
              → _repository.Save(order)           :86

            sharpgraph flow OrderService          # sin método: vista de 1 nivel

          EQUIVALENTE MCP: flow(typeName, member, depth)
        """;

    private const string CmdHubs = """
        sharpgraph hubs — Tipos más centrales (PageRank)

          Lista los tipos núcleo del sistema por centralidad (PageRank).
          Punto de partida ideal para entender un codebase desconocido.

          USO:
            sharpgraph hubs [-n <topK>] [--include-external]

          FLAGS:
            -n <topK>              Número de tipos a devolver (1-50, defecto 15).
            --include-external     Incluir tipos externos/BCL (ILogger, IMediator…).

          EJEMPLOS:
            sharpgraph hubs -n 10
            sharpgraph hubs --include-external -n 20

          EQUIVALENTE MCP: hubs(topK, includeExternal)
        """;

    private const string CmdDi = """
        sharpgraph di — Resuelve inyección de dependencias

          Dado un servicio o implementación, muestra el binding DI registrado
          (AddScoped / AddSingleton / AddTransient).
          Evita tener que leer Program.cs / DependencyInjection.cs a mano.

          USO:
            sharpgraph di <tipo>

          EJEMPLOS:
            sharpgraph di IIdentityService
              → IIdentityService → IdentityService [transient] (L77)

            sharpgraph di IdentityService
              → IdentityService ← IIdentityService [transient] (L77)

          EQUIVALENTE MCP: resolve_di(typeName)
        """;

    private const string CmdSource = """
        sharpgraph source — Ver código fuente de un tipo o método

          Devuelve CÓDIGO FUENTE directamente desde el grafo, sin leer ficheros
          enteros. Es la herramienta clave para ahorrar tokens.

          USO:
            sharpgraph source <tipo> [-m <miembro>] [-l <líneas>]

          FLAGS:
            -m <miembro>       Ver solo ese método/propiedad.
                               Si se omite, devuelve el cuerpo completo del tipo.
            -l <líneas>        Máximo de líneas (defecto 60 para miembro, 200 para tipo).

          EJEMPLOS:
            sharpgraph source OrderService -m Place       # solo el método Place
            sharpgraph source OrderService -l 100         # tipo completo, truncado a 100

          EQUIVALENTE MCP: get_source(typeName, member, maxBodyLines)
        """;

    private const string CmdUnderstand = """
        sharpgraph understand — Comprender un tipo en 1 llamada

          Devuelve el cuerpo completo del tipo + contexto curado del grafo
          (DI, callers, deps, endpoints). Pensada para "¿cómo funciona X?".

          USO:
            sharpgraph understand <tipo> [-l <budget>]

          FLAGS:
            -l <budget>        Máximo de líneas de cuerpo (20-800, defecto 200).

          EJEMPLOS:
            sharpgraph understand OrderService

          EQUIVALENTE MCP: understand(typeName, bodyBudget)
        """;

    private const string CmdReadFile = """
        sharpgraph read-file — Leer un fichero .cs entero

          Lee un fichero .cs del proyecto escaneado, con números de línea y
          marcas de región por tipo definido.

          USO:
            sharpgraph read-file <fichero> [-l <líneas>]

          ARGUMENTOS:
            fichero            Ruta relativa al proyecto o absoluta.

          FLAGS:
            -l <líneas>        Máximo de líneas (10-800, defecto 200).

          EJEMPLOS:
            sharpgraph read-file src/Application/Services/OrderService.cs
            sharpgraph read-file OrderService.cs -l 50

          EQUIVALENTE MCP: read_file(filePath, maxLines)
        """;

    private const string CmdSemantic = """
        sharpgraph semantic — Búsqueda semántica (BM25)

          Busca tipos públicos por INTENCIÓN, combinando nombre, summary XML,
          nombres de miembros y dependencias. Encuentra tipos aunque no sepas
          el nombre exacto.

          USO:
            sharpgraph semantic <query> [-n <topK>]

          FLAGS:
            -n <topK>          Número de resultados (1-30, defecto 10).

          EJEMPLOS:
            sharpgraph semantic "persistencia guardar base de datos"
            sharpgraph semantic "validación de pedidos" -n 5

          EQUIVALENTE MCP: search_semantic(query, topK)
        """;

    private const string CmdExplore = """
        sharpgraph explore — Contexto bidireccional

          Explora el contexto cercano de un tipo o patrón en ambas direcciones:
          endpoints directos, DI, callers cercanos, dependencias y endpoints HTTP.
          Útil como descubrimiento general.

          USO:
            sharpgraph explore <patrón> [-d <depth>] [-l <limit>]

          FLAGS:
            -d <depth>         Profundidad bidireccional (1-4, defecto 2).
            -l <limit>         Máximo por bloque (3-15, defecto 8).

          EJEMPLOS:
            sharpgraph explore OrderService
            sharpgraph explore "Service" -d 3

          EQUIVALENTE MCP: explore_context(typeOrPattern, depth, limitPerGroup)
        """;

    private const string CmdSetup = """
        sharpgraph setup — Instalación interactiva

          Menú ASCII para registrar SharpGraph en tu cliente MCP.
          Detecta el cliente, copia el binario, escribe la configuración
          y (si es Claude Code) configura el auto-scan hook.

          USO (interactivo):
            sharpgraph setup

          USO (no interactivo, con flags):
            sharpgraph setup --client <nombre> [--no-hook] [--install-path <dir>]

          CLIENTES SOPORTADOS:
            claude             Claude Code (con hook CwdChanged de auto-scan)
            cursor             Cursor IDE
            cline              Cline (extensión VS Code)
            continue           Continue (VS Code / JetBrains)
            zed                Zed editor
            opencode           OpenCode
            crush              Crush
            generic            Muestra el JSON para configuración manual

          FLAGS:
            --client <nombre>  Cliente objetivo (ver lista arriba).
            --no-hook          No configurar el hook de auto-scan (solo Claude Code).
            --install-path <dir>  Carpeta de instalación (defecto: ~/tools/SharpGraph).

          EJEMPLOS:
            sharpgraph setup                              # menú interactivo
            sharpgraph setup --client cursor              # registra en Cursor
            sharpgraph setup --client claude --no-hook    # Claude Code sin auto-scan
        """;

    private const string CmdHelp = """
        sharpgraph help — Ayuda de la CLI

          USO:
            sharpgraph help              # ayuda general (todos los comandos)
            sharpgraph help <comando>    # ayuda detallada de un comando

          COMANDOS DISPONIBLES:
            scan, stats, search, callers, usages, callsites, trace, flow,
            hubs, di, source, understand, read-file, semantic, explore,
            setup, help

          EJEMPLOS:
            sharpgraph help flow         # ayuda del comando flow
            sharpgraph help setup        # ayuda del comando setup
        """;
}
