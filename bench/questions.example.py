# -*- coding: utf-8 -*-
"""
PLANTILLA — copia este fichero a `questions.py` y edítalo con TUS repos y símbolos.

Define una batería de preguntas para comparar, en tokens, tres enfoques al responderlas:
  - LocalGraph (herramientas MCP)
  - CodeGraph  (CLI `codegraph` + modelo de su `explore`)
  - sin-MCP    (grep + lectura de ficheros)

Cada pregunta indica cómo resolverla con cada enfoque. El motor (benchmark.py) mide
los tokens que entran en el contexto del modelo y declara ganador solo si la herramienta
responde de verdad (los literales, p.ej., solo los encuentra grep).

`favor` documenta para qué enfoque se diseñó la pregunta (no afecta a la medición):
  LG = estructura/navegación .NET · CG = leer/entender código · FLOW = flujo de llamadas
  GREP = literales · NEU = neutral
"""

# Rutas absolutas a tus repos .NET (ejecuta `codegraph init` en cada uno antes).
REPOS = {
    "A": r"C:\ruta\a\tu\repo-dotnet",
    # "B": r"C:\ruta\a\otro\repo",
}

Q = [
    # --- estructura: ¿quién depende de una interfaz? (favorece a LocalGraph) ---
    dict(id="Q01", repo="A", favor="LG", type="deps",
         q="¿Qué depende de IOrderRepository?",
         lg=[("search", {"pattern": "IOrderRepository"}),
             ("find_callers", {"typeName": "IOrderRepository", "depth": 3})],
         cg={"mode": "callers", "symbol": "IOrderRepository",
             "fallback_files": ["src/App/Repositories/IOrderRepository.cs"]},
         grep=["IOrderRepository"], read_hits=True),

    # --- inyección de dependencias (favorece a LocalGraph) ---
    dict(id="Q02", repo="A", favor="LG", type="di",
         q="¿Qué implementación se inyecta para IOrderService?",
         lg=[("resolve_di", {"typeName": "IOrderService"})],
         cg={"mode": "explore", "files": ["src/App/DependencyInjection.cs"]},
         grep=["IOrderService"], read_files=["src/App/DependencyInjection.cs"]),

    # --- invocaciones reales de un método (favorece a LocalGraph) ---
    dict(id="Q03", repo="A", favor="LG", type="callsites",
         q="¿Dónde se invoca OrderService.Place?",
         lg=[("find_call_sites", {"typeName": "OrderService", "member": "Place"})],
         cg={"mode": "callers", "symbol": "Place",
             "fallback_files": ["src/App/Services/OrderService.cs"]},
         grep=["Place("], grep_fixed=True, read_hits=True),

    # --- comprensión de FLUJO: árbol de llamadas (favorece a LocalGraph: flow) ---
    dict(id="Q04", repo="A", favor="FLOW", type="flow",
         q="¿Cómo funciona OrderService.Place de principio a fin?",
         lg=[("flow", {"typeName": "OrderService", "member": "Place", "depth": 3})],
         # sin flow, entender el flujo = leer el método + cada colaborador de la cadena
         cg={"mode": "explore", "files": ["src/App/Services/OrderService.cs",
                                          "src/App/Services/PricingService.cs",
                                          "src/App/Repositories/OrderRepository.cs"]},
         grep=[], read_files=["src/App/Services/OrderService.cs",
                              "src/App/Services/PricingService.cs",
                              "src/App/Repositories/OrderRepository.cs"]),

    # --- leer un método concreto (favorece a LocalGraph: get_source) ---
    dict(id="Q05", repo="A", favor="CG", type="source",
         q="Enséñame el cuerpo del método Place.",
         lg=[("get_source", {"typeName": "OrderService", "member": "Place"})],
         cg={"mode": "explore", "files": ["src/App/Services/OrderService.cs"]},
         grep=["class OrderService"], read_files=["src/App/Services/OrderService.cs"]),

    # --- entender una clase completa + su contexto (understand vs explore) ---
    dict(id="Q06", repo="A", favor="CG", type="understand",
         q="Enséñame la clase OrderService completa y su rol.",
         lg=[("understand", {"typeName": "OrderService", "bodyBudget": 300})],
         cg={"mode": "explore", "files": ["src/App/Services/OrderService.cs"]},
         grep=["class OrderService"], read_files=["src/App/Services/OrderService.cs"]),

    # --- arquitectura / tipos núcleo (favorece a LocalGraph: hubs) ---
    dict(id="Q07", repo="A", favor="LG", type="hubs",
         q="¿Por dónde empiezo a entender el sistema?",
         lg=[("stats", {}), ("hubs", {"topK": 12})],
         cg={"mode": "explore", "files": ["src/App/DependencyInjection.cs"]},
         grep=[], read_files=["src/App/DependencyInjection.cs"]),

    # --- literal (solo lo encuentra grep) ---
    dict(id="Q08", repo="A", favor="GREP", type="literal", kind="literal",
         q='¿Dónde está el mensaje "order not found"?',
         lg=[("search", {"pattern": "Order"})],
         cg={"mode": "query", "term": "order not found"},
         grep=["order not found"], grep_fixed=True, read_hits=False),
]

# --- Referencia de campos por pregunta -------------------------------------
# repo:        clave de REPOS
# lg:          lista de (herramienta_MCP, argumentos) que ejecuta LocalGraph
# cg:          estrategia CodeGraph:
#   {"mode":"callers"|"callees", "symbol":"X", "limit":20, "fallback_files":[...]}
#       -> ejecuta `codegraph callers/callees X --json`; si vacío, modela `explore` de fallback_files
#   {"mode":"query", "term":"X", "limit":10}  -> `codegraph query X --json`
#   {"mode":"explore", "files":[...]}         -> modela el coste de explore = fuente verbatim de esos ficheros
# grep:        lista de patrones (su salida cuenta como contexto)
# grep_fixed:  True para búsqueda de cadena fija (grep -F)
# read_hits:   True -> además lee enteros los ficheros con hit (excluye tests/samples)
# read_files:  ficheros concretos a leer enteros (rutas relativas al repo)
# kind:        "literal" -> solo grep es elegible para ganar (los grafos no indexan literales)
