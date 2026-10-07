namespace SharpGraph.Cli;

/// <summary>
/// Help estático de la CLI. Texto completo con banner, lista de comandos
/// y ayuda detallada por comando. Generado a partir de las descripciones
/// de GraphTools.cs pero mantenido aquí como constantes para máximo control
/// del formato.
/// </summary>
internal static class CliHelp
{
    public const string Version = "2.3.0";

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
          impact <tipo>       Radio de impacto transitivo: qué rompes si cambias X
                              (niveles + endpoints en riesgo + tests). Propaga DI.
                              Flags: -d <profundidad> (defecto 6), --with-tests.
          flow <tipo>         Árbol de llamadas SALIENTES (sin código).
                              Flags: -m <miembro>, -d <profundidad> (defecto 2).
          hubs                Tipos más centrales (PageRank).
                              Flags: -n <topK> (defecto 15).
          di <tipo>           Resuelve inyección de dependencias (IFoo → Foo).

        ── DIAGRAMAS MERMAID ─────────────────────────────────────────
          mermaid <tipo>      Diagrama bidireccional (callers + deps) como bloque
                              ```mermaid pegable en Markdown/docs.
                              Flags: -u <callers> (defecto 3), -d <deps> (defecto 3),
                                     -n <nodos> (defecto 40), --lr, --external.
          mermaid-seq <tipo>  Cadena ancla → endpoints HTTP como sequenceDiagram.
                              Flags: -m <miembro>, -p <caminos> (defecto 3),
                                     -d <depth> (defecto 8).
          mermaid-overview    Mapa de arquitectura: todos los endpoints + deps,
                              agrupado por namespace.
                              Flags: -a <área>, -d <deps> (defecto 2),
                                     -n <nodos> (defecto 60).

        ── CÓDIGO FUENTE ─────────────────────────────────────────────
          source <tipo>       Ver código de un tipo o método concreto.
                              Flags: -m <miembro>, -l <líneas> (defecto 60).
          understand <tipo>   Clase completa + contexto del grafo en 1 llamada.
                              Flags: -l <budget> (defecto 200).
          read-file <fichero> Leer un .cs entero numerado, con marcas de tipo.
                              Flags: -l <líneas> (defecto 200).
          semantic <query>    Búsqueda semántica por intención (BM25).
                              Flags: -n <topK> (defecto 10).
          literals <texto>    Busca en los string literals del código (grep del
                              grafo): file:line + tipo que lo contiene.
                              Flags: -l <límite> (defecto 30), -c (case-sensitive).
          explore <patrón>    Contexto bidireccional (callers + deps + endpoints).
                              Flags: -d <depth> (defecto 2), -l <limit> (defecto 8).

        ── INSTALACIÓN Y CONFIGURACIÓN ───────────────────────────────
          setup               Menú interactivo: registra SharpGraph en tu
                              cliente MCP (Claude Code, Cursor, Cline,
                              Continue, VS Code, Zed, OpenCode, Crush, genérico).
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

          # Diagramas para docs / humano
          sharpgraph mermaid CreateOrderCommandHandler
          sharpgraph mermaid-seq IGrossService -p 2
          sharpgraph mermaid-overview -a Billing

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
        "impact" => CmdImpact,
        "flow" => CmdFlow,
        "hubs" => CmdHubs,
        "di" or "resolve-di" or "resolve_di" => CmdDi,
        "source" or "get-source" or "get_source" => CmdSource,
        "understand" => CmdUnderstand,
        "read-file" or "read_file" or "readfile" => CmdReadFile,
        "semantic" or "search-semantic" or "search_semantic" => CmdSemantic,
        "literals" or "search-literals" or "search_literals" => CmdLiterals,
        "explore" or "explore-context" or "explore_context" => CmdExplore,
        "mermaid" or "mermaid-context" or "mermaid_context" => CmdMermaid,
        "mermaid-seq" or "mermaid-sequence" or "mermaid_sequence" => CmdMermaidSeq,
        "mermaid-overview" or "mermaid_overview" => CmdMermaidOverview,
        "setup" => CmdSetup,
        "help" => CmdHelp,
        _ => $"""
            Comando desconocido: '{command}'

            Comandos disponibles: scan, stats, search, callers, usages, callsites,
            trace, impact, flow, hubs, di, source, understand, read-file,
            semantic, literals, explore, mermaid, mermaid-seq, mermaid-overview,
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

    private const string CmdImpact = """
        sharpgraph impact — Impacto de cambio (blast radius)

          ANTES de tocar un tipo: calcula el radio de impacto TRANSITIVO —
          todo lo que se rompe o hay que revisar si lo cambias. Resumen por
          niveles (no un árbol): tipos afectados con su relación, endpoints
          HTTP en riesgo y tests que ejercitan el área.

          Propaga DI: si cambias una implementación, el impacto sube a la
          interfaz que registra y de ahí a todos sus consumidores.

          USO:
            sharpgraph impact <tipo> [-d <profundidad>] [--with-tests]

          FLAGS:
            -d <profundidad>   Profundidad máxima (1-10, defecto 6).
            --with-tests       Incluir tipos de test dentro de los niveles
                               (siempre se listan aparte al final).

          EJEMPLO:
            sharpgraph impact Repo0
              87 tipos en 6 niveles · 8 endpoints HTTP · 2 tests
              Nivel 1: IRepo0 [di-impl], Service0 [ctor-param]
              Nivel 2: Controller0 [sends] [ENDPOINT: POST /d001/op0]
              Endpoints en riesgo (8): [POST /d001/op0] Controller0.Op0 ...

          EQUIVALENTE MCP: impact(typeName, maxDepth, includeTests)
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

    private const string CmdLiterals = """
        sharpgraph literals — Buscar literales de cadena

          ¿Dónde está este string en el código? Busca en los literales de
          cadena indexados (plain, verbatim y const) de los .cs del proyecto
          y devuelve file:line + el tipo que lo contiene, acotado a N hits.

          Es el grep de SharpGraph para strings/mensajes/claves dentro de C#.

          USO:
            sharpgraph literals <texto> [-l <límite>] [-c]

          FLAGS:
            -l <límite>        Máximo de resultados (1-100, defecto 30).
            -c                 Sensible a mayúsculas (defecto: insensible).

          EJEMPLOS:
            sharpgraph literals "Todo Lists"
            sharpgraph literals "connectionString" -c
            sharpgraph literals "not found" -l 100

          EQUIVALENTE MCP: search_literals(pattern, limit, caseSensitive)
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

    private const string CmdMermaid = """
        sharpgraph mermaid — Diagrama Mermaid bidireccional

          Dibuja el contexto de un tipo como flowchart: la cadena de llamadores
          hacia arriba (hasta los endpoints HTTP) y el árbol de dependencias hacia
          abajo, con la relación de cada flecha (sends, handled-by, di-bound,
          ctor-param, call...). El ancla sale resaltada.

          La salida es un bloque ```mermaid pegable tal cual en Markdown
          (GitHub, GitLab, Obsidian y mermaid.live lo renderizan).

          USO:
            sharpgraph mermaid <tipo> [-u <callers>] [-d <deps>] [-n <nodos>] [--lr] [--external]

          FLAGS:
            -u <callers>   Niveles hacia los llamadores (0-6, defecto 3).
            -d <deps>      Niveles hacia las dependencias (0-6, defecto 3).
            -n <nodos>     Tope de nodos (5-100, defecto 40).
            --lr           Orientación izquierda→derecha (defecto TD).
            --external     Incluir tipos BCL/NuGet (ILogger, IMediator…).

          EJEMPLOS:
            sharpgraph mermaid CreateOrderCommandHandler
            sharpgraph mermaid OrderService -u 0 -d 4      # solo dependencias
            sharpgraph mermaid IUserService --lr --external

          EQUIVALENTE MCP: mermaid_context(typeName, callersDepth, depsDepth, ...)
        """;

    private const string CmdMermaidSeq = """
        sharpgraph mermaid-seq — Diagrama de secuencia ancla → endpoint

          Cada cadena estructural desde el ancla hasta los endpoints HTTP que la
          invocan, dibujada como sequenceDiagram en orden de ejecución, seguida de
          las llamadas salientes del ancla a nivel de método.

          USO:
            sharpgraph mermaid-seq <tipo> [-m <miembro>] [-p <caminos>] [-d <depth>]

          FLAGS:
            -m <miembro>   Mostrar solo las llamadas de ese método.
            -p <caminos>   Máximo de cadenas a endpoint (1-8, defecto 3).
            -d <depth>     Profundidad máxima de cadena (1-12, defecto 8).

          EJEMPLOS:
            sharpgraph mermaid-seq CreateOrderCommandHandler
            sharpgraph mermaid-seq OrderService -m Place -p 2

          EQUIVALENTE MCP: mermaid_sequence(typeName, member, maxPaths, maxDepth)
        """;

    private const string CmdMermaidOverview = """
        sharpgraph mermaid-overview — Mapa de arquitectura Mermaid

          Grafo de arquitectura: BFS desde todos los endpoints HTTP hacia sus
          dependencias, agrupado en subgraphs por namespace. El diagrama de
          cabecera para docs/architecture/overview.md.

          USO:
            sharpgraph mermaid-overview [-a <área>] [-d <deps>] [-n <nodos>]

          FLAGS:
            -a <área>      Filtrar endpoints por substring de tipo/namespace.
            -d <deps>      Profundidad de dependencias (1-5, defecto 2).
            -n <nodos>     Tope de nodos (10-150, defecto 60).

          EJEMPLOS:
            sharpgraph mermaid-overview
            sharpgraph mermaid-overview -a Billing -d 3

          EQUIVALENTE MCP: mermaid_overview(area, depsDepth, maxNodes)
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
            cline              Cline (extensión IDE)
            continue           Continue (VS Code / JetBrains)
            vscode             VS Code (workspace .vscode/mcp.json)
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
            scan, stats, search, callers, usages, callsites, trace, impact,
            flow, hubs, di, source, understand, read-file, semantic, literals,
            explore, mermaid, mermaid-seq, mermaid-overview, setup, help

          EJEMPLOS:
            sharpgraph help flow         # ayuda del comando flow
            sharpgraph help setup        # ayuda del comando setup
        """;
}
