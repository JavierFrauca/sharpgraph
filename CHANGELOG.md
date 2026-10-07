# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html)
(with the caveat that, during beta, the MCP tool surface may change between
minor versions).

## [Unreleased]

### Added
- **`sharpgraph install vscode`** — instala la extensión SharpGraph Flow en
  VS Code: descarga el `.vsix` del último release de GitHub (misma maquinaria
  que `update`, con verificación SHA-256 cuando SHA256SUMS.txt lo cubre) y
  ejecuta `code --install-extension`. Localiza la CLI en PATH y rutas
  habituales de Windows/macOS/Linux (`code`; alternativa `code-insiders` /
  `codium`). `--vsix <ruta>` instala un fichero local (offline/desarrollo).
  Avisa si la versión del `.vsix` y la del binario no van alineadas.
- El release ahora incluye el `.vsix` en `SHA256SUMS.txt` (antes solo los
  zips del binario), para que `install vscode` pueda verificarlo.

## [2.5.6] — extensión

### Fixed
- **EL CUELLO DE BOTELLA que lo lentificaba todo**: los refrescos del watcher
  lanzaban el `scan` incremental, que re-hashea TODA la solución (medido en
  Payroll: 23-86 SEGUNDOS) bloqueando el único servidor — cualquier clic en un
  endpoint se quedaba encolado detrás. Ahora el watcher SOLO recarga la lista
  desde el servidor vivo (milisegundos): el motor se auto-vigila tras su primer
  scan, así que el scan solo corre en la primera carga y en el botón
  "Indexar / actualizar repo". Los listados vacíos no borran lo sembrado.

## [2.5.4] — extensión

### Added
- **Extensión SharpGraph Flow — UN endpoint de uno en uno**: clic en una FILA
  → diagrama de SOLO ese endpoint (el motor 2.5.1 filtra por el método de
  acción: nada de los hermanos del controller); clic en el NOMBRE del
  controlador (subrayado punteado al hover) → flujo completo del controlador
  con TODAS sus acciones. El chevron sigue plegando/desplegando el grupo.
  Requiere motor 2.5.1.

## [2.5.3] - 2026-10-07

### Fixed
- **Extensión SharpGraph Flow — el diagrama por fin SIEMPRE vertical**: los
  niveles se dibujan ahora en CUADRÍCULA de máx 4 columnas — lo que excede
  ENVUELVE a filas extra, así un nivel con 10 commands no se derrama en una
  tira horizontal (a nivel 2 con fan-out de 10: 4 columnas × 5 bandas). Los
  niveles estrechos se CENTRAN (vuelve la columna del prototipo); los hermanos
  van juntos en orden de descubrimiento.
- **El slider arranca por defecto en el nivel 2** (mediator + dependencias de
  primer nivel): `sharpgraphFlow.defaultDepth` pasa de 4 a 2.

## [2.5.2] - 2026-10-07

### Fixed
- **Extensión SharpGraph Flow — el código ya no parte la pantalla al abrirse
  desde la barra lateral**: clic en un bloque del diagrama embebido abría el
  fichero con `ViewColumn.Beside`, que dividía la zona central con la mitad
  izquierda vacía cuando no había editor activo. Desde la barra lateral abre en
  la columna ACTIVA (sin split); desde el panel grande se mantiene el split
  (diagrama + código al lado). Los ficheros abren ahora como preview (pestaña
  en cursiva que se reutiliza entre clics; doble clic en la pestaña para fijar).

## [2.5.1] - 2026-10-07

### Fixed
- **Extensión SharpGraph Flow — expandir/contraer blindado**: los handlers de
  los botones se registraban por elemento al cargar el script; si algo fallaba
  antes en el entorno real, quedaban muertos en silencio. Ahora todos los
  clicks delegan en un único listener de document (data-action/data-cmd),
  "Quitar filtro" es una acción local del webview (antes colgaba del comando
  de host equivocado) y cualquier error JS de la vista se reporta al canal
  "SharpGraph Flow" para diagnóstico.

## [2.5.3] — motor

### Fixed
- **SCANNER — rutas de acción con [Route] separado (la raíz del "batiburrido")**:
  el patrón `[HttpGet]` SIN plantilla + `[Route("sectors")]` en atributo separado
  (así está escrito GeroaController de Payroll) NO componía ruta — las 25
  acciones colapsaban a "/api/geroas" y CUALQUIER clic resolvía 11 endpoints a
  la vez (el controlador entero; y los "no encontrado" del catálogo). Ahora el
  visitor compone prefijo + `[Route]` de la acción cuando el `[HttpX]` va sin
  plantilla. Verificado E2E contra el worktree Geroa real: 25 endpoints → 23
  rutas distintas; clic en GET /api/geroas/sectors → modo endpoint, 1 match,
  2/114 aristas del controller, 6 nodos (endpoint → controller → query).
- **ParserVersion 9 → 10**: invalida todas las cachés (las rutas extraídas
  cambian; el primer scan tras actualizar es completo, una sola vez).

## [2.5.2] — motor

### Fixed
- **`endpoint_flow` — fallback por SPAN de líneas + diagnóstico**: si el filtro
  por FromMember no encuentra aristas del método de acción (wrappers que pierden
  el FromMember), se reintenta por SPAN (líneas de la arista dentro de
  StartLine..EndLine del método); solo si tampoco, expansión completa. El JSON
  incluye ahora `"mode"` (endpoint|controller) y `"controllerEdges"`
  {total, matched} — la extensión 2.5.5 lo registra en su canal de salida al
  pintar cada diagrama, para diagnosticar sin CLI.

## [2.5.1] — motor

### Fixed
- **`endpoint_flow` — MODO ENDPOINT-CONCRETO**: la traza desde "VERB /ruta"
  expandía el controlador ENTERO (los commands de TODAS sus acciones = el
  "batiburrido"). Ahora, del controller SOLO se expanden las aristas de SU
  método de acción (filtro por `FromMember` en el primer nivel de BFS; si el
  método no genera aristas resueltas, fallback al expansionado completo para
  minimal APIs). Por nombre de controlador se sigue viendo TODO (las dos cosas
  que pide la extensión: clic en endpoint = solo ese endpoint; clic en el
  nombre del controlador = el controlador entero). 9 tests de endpoint_flow.
  **Requiere motor 2.5.1** para el checkbox/slider de la extensión 2.5.4+.

## [2.5.0] - 2026-10-07

### Added
- **`endpoint_flow(includeDtos:)` — nivel 4: contratos de entrada/salida**:
  nuevo parámetro que incluye los DTOs (parámetros y retornos de métodos
  públicos) como nodos hoja `kind=dto` (no se expanden). La detección es doble:
  descubrimiento por arista ParamType/ReturnType, o por nombre (Dto/Request/
  Response/Result) porque los `new XxxResponse()` generan arista `New` que
  domina sobre ReturnType en la relación dominante. Sin el flag, esos nodos son
  "class" normales — el nivel de contratos solo existe cuando se pide.
  CLI: `sharpgraph endpoint-flow … --dtos`. Test nuevo (8 en total de
  endpoint_flow). Es el nivel 4 del control de profundidad de la extensión
  SharpGraph Flow (slider de niveles: 1=mediator, 2=deps 1er nivel,
  3=deps de deps, 4=+contratos).
- **Extensión SharpGraph Flow 2.5.0 — niveles con slider, contratos y pan**:
  la barra del diagrama añade un SLIDER DE NIVELES (1-8) que re-consulta el
  motor al soltarlo (nivel 1 = endpoint→mediator, 2 = dependencias de primer
  nivel, 3 = dependencias de las dependencias…), un checkbox **contratos**
  (DTOs, nivel 4, requiere motor 2.5.0) y **arrastre con el ratón** para
  desplazarse (grab; los clics en bloques tras arrastrar no se disparan).
  Con pocos niveles el grafo es una columna vertical — se acabó el "se ve en
  horizontal". Settings nuevos: `sharpgraphFlow.defaultDepth` (4) y
  `sharpgraphFlow.includeDtos` (false). Nodos DTO con color propio en la leyenda.

## [2.4.7] - 2026-10-07

### Changed
- **Carga rápida de la vista de endpoints** (queja: "tarda mucho cargando"):
  - El servidor ya NO se arranca con la solución delante (eso recargaba TODA la
    caché binaria del motor — 13,6 MB / 5.137 fragmentos en Payroll — en cada
    refresco). Ahora el initialize es instantáneo y el scan incremental va como
    tool, en background, con la lista ya pintada.
  - La última lista conocida se siembra desde `workspaceState`: al abrir la
    ventana la vista pinta AL INSTANTE con los endpoints de la sesión anterior
    y se actualiza cuando el scan termina.
  - **Expandir un grupo refresca SUS endpoints** (contraer + expandir =
    actualizar): el contador del grupo muestra "…" mientras, la sustitución es
    parcial (solo ese controlador) y no mueve el scroll ni colapsa nada.
- Motor intacto: todo es reutilización del servidor vivo + scan incremental.

## [2.4.6] - 2026-10-07

### Added
- **Extensión SharpGraph Flow — campo de filtro FIJO sobre la lista**: la vista
  "Endpoints" deja de ser un TreeView nativo y pasa a ser un webview (como ya
  hicimos con el diagrama), lo que permite incrustar el input de filtro SIEMPRE
  visible encima del árbol — filtrado instantáneo y en local (sin quick-input
  central ni round-trips). Expandir/contraer todo pasan a botones junto al
  filtro; los chips de verbo son CSS (adiós SVGs); el contador ("N endpoints" /
  "N de M con filtro") vive en la vista. Estados loading/error/vacío con sus
  acciones embebidas.
- **Trade-off asumido**: sin TreeView nativo no hay API de badge en el icono del
  ActivityBar; el nº de endpoints se muestra en la propia vista. Los botones de
  título (indexar, recargar, configurar) siguen siendo nativos.

## [2.4.5] - 2026-10-07

### Fixed
- **El árbol se quedaba en "Escaneando…" y no volvía a cargar los endpoints**:
  tres causas encadenadas, todas del lado de la extensión (el motor quedó
  descartado con una sonda MCP standalone):
  - `stop()` del cliente MCP limpiaba el mapa de llamadas pendientes SIN
    rechazarlas: cuando un refresco pisaba a otro (watcher + clic + botón), los
    `await` quedaban colgados para siempre. Ahora se rechazan antes de limpiar
    y `start()` espera a que el proceso anterior muera antes de spawnear.
  - **Colisión de ids de webview**: el panel del editor usaba el viewType
    `sharpgraphFlow.diagram`, el mismo que la nueva vista embebida de la barra
    lateral. Renombrado a `sharpgraphFlow.editorPanel`.
  - `refreshTree` sin mutex: el watcher podía pisar un refresco en curso; ahora
    se encola. Además, si ya hay datos, los re-escaneos ya NO vacían el árbol
    con "Escaneando…" (solo la primera carga o tras un error).
- Diagnóstico: el canal de salida "SharpGraph Flow" registra ahora cada
  refresco (solución, nº de endpoints, duración) y las caídas del servidor.

## [2.4.4] - 2026-10-07

### Added
- **Extensión SharpGraph Flow — diagrama embebido en la barra lateral**: el
  contenedor del ActivityBar pasa a tener DOS vistas apiladas: el árbol de
  endpoints arriba y una vista webview "Diagrama" abajo (divisor arrastrable,
  VS Code recuerda las proporciones). Clic en un endpoint → el diagrama gráfico
  real (mismo render, zoom y back-edges) se pinta embebido SIN abrir paneles,
  sin robar el foco (navegable con flechas actualizando el diagrama); botón
  "Editor" en su cabecera y botón `⤖` inline en cada fila para abrir el panel
  grande del editor como hasta ahora. Se re-renderiza con guard de generación
  para ignorar respuestas del motor desfasadas.

## [2.4.3] - 2026-10-07

### Added
- **Extensión SharpGraph Flow — árbol de endpoints: filtrar y expandir/contraer**:
  botón de FILTRO con cuadro de texto que filtra en vivo mientras se escribe
  (coincide por controlador, ruta, método o verbo; los grupos sin coincidencias
  desaparecen, y si un controlador coincide por nombre se muestran todos sus
  endpoints), botón "Quitar filtro" visible solo con filtro activo (contexto
  `sharpgraphFlow.filterActive`), y botones EXPANDIR TODO / CONTRAER TODO para los
  grupos. El estado de expansión es ahora propio del provider (se retira el
  collapse-all nativo) para que los botones y los re-escaneos no lo desincronicen;
  con filtro sin coincidencias el árbol ofrece quitarlo desde su propio mensaje.

## [2.4.2] - 2026-10-07

### Added
- **Extensión SharpGraph Flow — zoom del diagrama**: ruleta del ratón (zoom
  anclado al cursor; Shift+ruleta = scroll horizontal) y botones +/−/100%/Ajustar
  en la barra del webview, con el porcentaje visible. Por defecto el diagrama
  arranca a escala LEGIBLE (mínimo umbral de 65%: por debajo, se abre al 90%
  sobre la raíz y se recorre con la ruleta/scroll) — un árbol ancho ya no se
  aplasta en una tira horizontal: se ve vertical y navegable.

### Fixed
- Orden de hermanos en el layout: el último hijo se colocaba antes que los
  intermedios (los slots se asignaban en dos pasadas), dejando los nodos de un
  mismo nivel desordenados respecto al orden de descubrimiento.

## [2.4.1] - 2026-10-07

### Added
- **Extensión SharpGraph Flow — acción "Indexar / actualizar repo"**: botón nuevo
  en el título de la vista (y en el estado vacío/error del árbol) que escanea el
  repo ABIERTO en ese momento: elige carpeta en multi-root y .sln/.slnx/.csproj si
  hay varios, muestra progreso y lanza el `scan` incremental del motor (primera
  inicialización y actualización, mismo botón). La vista muestra ahora la solución
  cargada como descripción; "Reindexar" pasa a "Recargar catálogo desde caché"
  (re-arranca el motor sin forzar scan).

## [2.4.0] - 2026-10-07

### Added
- **`endpoint_flow()` — subgrafo descendente desde un endpoint HTTP (22.ª tool MCP +
  `sharpgraph endpoint-flow`)**: el "qué toca este endpoint de arriba abajo" en JSON
  estructurado — controller → command/query (MediatR) → handler → servicios →
  implementaciones DI — pensado para el consumo por herramienta (extensión SharpGraph
  Flow para VS Code) y para el LLM. Acepta `"POST /api/orders"`, `"/api/orders"`
  (exacta o substring, máx 5) o el nombre del controlador (todas sus rutas). Cada
  **nodo** lleva `id` (N0..Nn), `kind` (endpoint/controller/command/query/handler/
  interface/implementation/validator/class/infra/external), `name`, `fqn`, `file` y
  `line` (click-to-open); cada **arista** lleva `relation`, la línea de la primera
  referencia y `back=true` cuando su destino YA estaba dibujado (dependencia
  compartida o ciclo: no se re-expande — el árbol ramifica sin explosión). Las
  dependencias externas (ILogger, IMapper, ControllerBase…) entran marcadas con
  `infra=true` (el cliente decide si las pinta); la arista `implements` de una impl
  hacia su interfaz ya dibujada por di-bound se deduplica (el mismo binding dos veces,
  igual que `IsMediatRArtifact` hace con MediatR). Incluye el bloque ```mermaid```
  equivalente (clave `mermaid`, back-edges en rojo con `linkStyle`). Podas habituales:
  tests/mocks fuera, `ParamType`/`ReturnType` fuera, tope duro de `maxNodes` con
  `truncated`/`omitted`. 7 tests nuevos (`EndpointFlowTests`): catálogo, las 4 capas,
  back-edge de IUnitOfWork compartido, flag de ILogger/IMediator, resolución por
  controlador/ruta, presupuesto y error JSON.
- **`list_endpoints()` — catálogo de endpoints HTTP (23.ª tool MCP +
  `sharpgraph endpoints`)**: todos los endpoints indexados (controllers + minimal
  APIs) en JSON: controlador, verbo, ruta, método y file:line. El punto de partida
  de endpoint_flow y la fuente del árbol lateral de la extensión.
- **`vscode-extension/` — extensión SharpGraph Flow para VS Code (VSIX)**: icono en
  la Activity Bar con badge de nº de endpoints; árbol de endpoints agrupado por
  controlador con verbos de color (GET/POST/PUT/DELETE como SVG); clic en un endpoint
  → webview con el diagrama descendente (layout jerárquico dagre, capas con el mismo
  código de color que el grafo, back-edges discontinuos, toggle "ocultar
  infraestructura") y clic en cualquier bloque → abre el fichero real en la línea
  exacta. Habla con el motor SharpGraph como cliente MCP stdio (mismo exe, sin
  duplicar parsing); configuración `sharpgraphflow.serverPath` y
  `sharpgraphflow.solutionPath`.

## [2.3.0] - 2026-10-07

### Added
- **Auto-actualización explícita (`sharpgraph update`) + aviso de versión nueva**:
  dos niveles, ambos del lado del usuario — nunca se descarga ni se sustituye nada
  sin pedido:
  - **Aviso por defecto**: check contra `releases/latest` de la API de GitHub con
    caché de 24 h en `%LOCALAPPDATA%/SharpGraph/` (≤1 petición/día), fail-silent
    (sin conexión no estorba) y opt-out con `SHARPGRAPH_NO_UPDATE_CHECK=1`. En CLI
    avisa a stderr al arrancar cualquier comando; en modo MCP `stats()` refresca la
    caché EN BACKGROUND y avisa leyendo solo la caché — las tools no pagan nunca la
    red.
  - **`sharpgraph update [--check]`**: descarga el zip del RID actual
    (win-x64/linux-x64/osx-arm64 detectado en runtime), verifica SHA-256 contra el
    `SHA256SUMS.txt` del release y sustituye el binario EN SITU con el truco del
    rename (un exe vivo no puede sobrescribirse pero sí renombrarse) y rollback si
    falla; el `.old` se limpia al siguiente arranque. Se niega si detecta árbol
    fuente (hay .git cerca: ahí el camino es `git pull` + `publish.ps1`). Sin
    equivalente MCP, deliberado: el servidor no se actualiza a sí mismo durante
    una sesión del LLM.
  - **Versión consolidada**: `VersionInfo.Current` única fuente (antes literal
    duplicado en ServerInfo y CliHelp).
  - **release.yml**: ahora empaqueta zip para los TRES RID (el updater tiene una
    sola ruta de extracción — System.IO.Compression, cero deps nuevas; el tar.gz
    unix se mantiene para el flujo manual/install.sh) y genera `SHA256SUMS.txt`
    con el hash de todos los artefactos.
  - 20 tests nuevos (`UpdateTests`): comparador de tags (numérico, prereleases
    fuera), mapa de RID, selección de asset, SHA256SUMS estándar, frescura de
    caché y el swap real con zip + rollback. Verificado E2E contra la API real
    (guard de árbol fuente, aviso con caché simulada, opt-out).
- **`mermaid_context()` — diagrama Mermaid del contexto de un tipo (19.ª tool MCP +
  `sharpgraph mermaid`)**: travesía bidireccional desde un ancla — cadena de llamadores
  hacia arriba (los endpoints se dibujan con su verbo+ruta pero no se expanden: son
  entrada) y árbol de dependencias hacia abajo — serializada como flowchart pegable tal
  cual en Markdown (GitHub/Obsidian/mermaid.live lo renderizan). Cada flecha conserva la
  relación del grafo: sólida sin label = `call`, punteada = resolución por convención
  (`sends`/`handled-by`/`di-bound`), gruesa = herencia/implementación; el ancla sale
  resaltada y los endpoints en verde. Los parámetros nombran semántica, no orientación
  (`callersDepth`/`depsDepth`, 0 desactiva una dirección; la visual la decide
  `direction: TD|LR`). Recorte de ruido por defecto: tests/mocks fuera, BCL fuera
  (`includeExternal` para verlos), `ParamType`/`ReturnType` fuera, tope duro de `maxNodes`
  con aviso de truncado dentro del propio bloque (el corte por orden BFS mantiene el
  grafo conexo). El artefacto del visitor con los argumentos genéricos de interfaces base
  (`IRequestHandler<X,_>` dejaba un `implements` Handler⇒X que duplicaba el binding y
  pintaba un ciclo falso) se suprime cuando el par ya tiene `sends`/`handled-by` opuesto.
  15 tests nuevos (`MermaidTests`): cadena MediatR completa, ciclo DI, truncado, escape de
  rutas con llaves, inclusión/exclusión de BCL, secuencia y overview.
- **`mermaid_sequence()` — sequenceDiagram de las cadenas ancla → endpoint (20.ª tool +
  `sharpgraph mermaid-seq`)**: las cadenas estructurales que `trace_to_endpoints` ya
  buscaba (sin heurísticos), dibujadas en orden de ejecución con `autonumber`, más las
  llamadas salientes del ancla a nivel de método. Si no hay camino a endpoint, dibuja
  igualmente las llamadas y lo avisa en el bloque.
- **`mermaid_overview()` — mapa de arquitectura (21.ª tool + `sharpgraph mermaid-overview`)**:
  BFS multi-fuente desde todos los endpoints HTTP (o los de un `area` por
  tipo/namespace) hacia sus dependencias, agrupado en `subgraph` por namespace (máx 12).
  Sin endpoints indexados cae al top-5 por PageRank avisándolo. El diagrama de cabecera
  para `docs/architecture/overview.md`.

### Fixed
- **Minimal APIs tipadas por grupos no detectadas** (hallazgo de la auditoría por
  ground truth del 2026-10-02): el estilo de CleanArchitecture v2 —
  `groupBuilder.MapGet(GetTodoLists)`, con método-grupo como argumento y la ruta
  como SEGUNDO argumento (`MapPut(UpdateTodoList, "{id}")`) — no casaba con la
  detección (que exigía ruta literal en primer argumento), dejando
  `trace_to_endpoints` e `impact` a cero en el codebase de referencia moderno.
  Ahora el endpoint se adscribe a la clase que declara el handler (la ruta
  mostrada es la relativa; el prefijo del grupo no se resuelve). Sobre
  CleanArchitecture: `stats` 0 → 10 endpoints, `trace_to_endpoints` "no paths"
  → 9 rutas `[exact-mediatr]`, `impact` "0 endpoints" → 8 en riesgo con verbo y
  ruta. Regresión: fixture `TypedMinimalApi` + tests de trace/impact. ParserVersion 9.
- **Etiquetas de relación de `impact` invertidas**: se buscaba la arista del
  predecessor hacia el afectado (que no existe — la dependencia real va del
  afectado hacia su dependencia), así que todo caía al fallback `[param]` o a
  etiquetas sin sentido. Ahora cada nivel explica POR QUÉ el tipo está afectado
  (`Handler [ctor-param]`, `Command [handled-by]`, `TodoLists [sends] [ENDPOINT: …]`).
- **`impact` contaba tipos con endpoint, no endpoints**: el resumen ahora suma
  los endpoints reales (una clase puede declarar varios) y el listado muestra
  hasta 4 por tipo.

### Added
- **`search_literals()` — grep nativo sobre literales de cadena** (18.ª tool MCP +
  `sharpgraph literals`): el visitor indexa los string literals (plain/verbatim/const,
  triviales <2 chars filtrados, interpolated fuera v1) y la query responde
  "¿dónde está este string?" con `file:line` + tipo contenedor y salida acotada.
  Paridad con grep dentro de los .cs: 46 vs 22 tokens en el caso mínimo, y cuando el
  patrón es común grep inunda con líneas no-literales (251 hits / 7.673 tok vs 344 tok
  curados). Server instructions actualizadas (los strings en .cs YA se buscan desde el
  grafo; grep queda para ficheros no-.cs). ParserVersion 8 + FormatoBinario v2
  (las cachés anteriores se re-escanean una vez).
- **`bench/compare_perf.py` — batería comparativa de rendimiento vs CodeGraph**:
  mide (mediana de 3 pasadas) indexado en frío con cachés borradas, arranque
  caliente, latencia de respuesta CLI, huella en disco del índice y latencia MCP
  por tool, sobre CleanArchitecture y un corpus sintético de 5.001 .cs (generador
  `%TEMP%\sgcorpus_gen.py`). Cifras de referencia documentadas en
  `docs/BENCHMARK.md`: frío 8× más rápido (6,8 s vs 54,6 s), índice 24× menor
  (1,91 vs 46,44 MB), MCP por tool 10-30 ms. Matiz honesto detectado: una
  invocación CLI suelta en corpus grande re-hashea todos los ficheros (~2,1 s vs
  ~0,6 s de CodeGraph) — en modo MCP (servidor vivo) no se paga; palanca futura
  un camino rápido sin re-hash en comandos de solo lectura.

### Changed
- **CLI de una consulta sin re-hash (la palanca anterior)**: los comandos de
  lectura (`callers`, `impact`, `literals`…) cargan la caché y responden, sin
  re-hashear el proyecto — mismo contrato que `codegraph status` (su sync también
  es explícito). `sharpgraph scan` queda como sync explícito; el watcher en
  caliente cubre el modo MCP. Medido sobre el corpus de 5.001 .cs: query CLI
  2,0-2,1 s → **1,3 s** y `stats` caliente 2,1 s → **1,5 s** (queda el arranque
  del proceso self-contained ~0,9 s como diferencia restante frente a node).
- **Tool titles and annotations**: every tool now advertises a human-readable `title`
  ("Quién usa este tipo", "Comprender un tipo (código + contexto)", "Buscar en la documentación"…)
  plus MCP hints: `readOnlyHint` + `idempotentHint` on the 15 query tools; `scan` and
  `configure_auto_scan` are idempotent but not read-only (they mutate the graph/cache and
  `~/.claude/settings.json` respectively). Verified over the wire via JSON-RPC probe.
- **Server metadata for LLM discoverability**: the MCP `initialize` response now carries
  `serverInfo.title` ("SharpGraph — grafo de código y documentación C#"), a `serverInfo.description`
  of what/how (dependency graph + docs index, token-saving thesis) and `websiteUrl`. The server
  instructions were rewritten around what/how/when: a new "CUÁNDO USAR / CUÁNDO NO" section
  (when to prefer grep, C#-only scope, read-only, unindexed formats), the 11-step flow, and an
  expanded "LÍMITES" section. All 17 tools and their 31 parameters already carried descriptions
  (verified over the wire with a JSON-RPC probe).
- **Documentation index (`search_docs`)**: `scan` now also indexes project documentation —
  `.md`/`.markdown`/`.txt` and known config JSON (`appsettings*`, `launchSettings`) — into a
  lightweight in-RAM index (`Docs/DocIndex.cs`; no new dependencies, no disk cache). New MCP
  tool `search_docs` (CLI: `sharpgraph docs`) runs BM25 over content with titles and sections
  weighted x3, returning path + section — never content. Docs are cross-linked with code:
  `search` tags types with `[docs:N]` and `understand` lists the docs mentioning the type
  (PascalCase/camelCase symbols of 4+ chars only, to avoid prose false positives). A second
  `FileSystemWatcher` in `ProjectWatcher` keeps the doc index fresh on save; doc-only changes
  skip the fragment-cache save.
- **Incremental fragment merge**: `GraphEngine.MergeFragments` now takes a fast path when
  every changed file still declares the same types and exposes the same return signatures —
  the old fragment's contributions are subtracted and the new ones added in milliseconds
  (`GraphEngine.Incremental.cs`), instead of rebuilding every index from every fragment.
  Structural changes (type added/removed/renamed, changed return type, new file) fall back
  to a single full rebuild per batch. Exposed `GraphEngine.LastMergeIncremental` for
  diagnostics/tests.

### Removed
- **Documentation index (`search_docs`)**: the whole docs feature is gone — the MCP tool
  `search_docs`, the CLI command `sharpgraph docs`, the `[docs:N]` tags on `search` and the
  doc-mentions line on `understand`, the docs counter on `stats`, and `Docs/DocIndex.cs`
  with its second `FileSystemWatcher` (the watcher now only observes `.cs`, so a `*`-filter
  instance no longer fires on every file in the tree). Rationale: documentation search is
  now handled by a dedicated RAG MCP (`docurag`) in parallel, and exposing the same
  capability from two servers confused the LLM's tool routing. `scan` indexes code only;
  server metadata/instructions no longer mention docs. The BM25 tokenizer is kept —
  `search_semantic` still uses it. The docs index never lived in the disk cache, so old
  caches remain valid. BREAKING for the tool surface (17 → 16 tools).

### Changed
- **Watcher batching**: `ProjectWatcher.Flush` parses all pending files and performs ONE
  merge per batch (was: one full rebuild per file), guards against overlapping flushes, and
  saves the cache with a 10 s throttle. `SolutionScanner.RescanFiles` parses a batch without
  merging.

### Fixed
- **Auto-scan hook invoked a wrong tool name**: the `CwdChanged` hook written by
  `configure_auto_scan`, `install.ps1` and `install.sh` used `"tool": "Scan"`, but the MCP wire
  name is snake_case (`scan`) — the hook failed silently with "Unknown tool". All three writers
  now emit `"scan"`. Existing hooks in `~/.claude/settings.json` need a one-line manual fix (or
  remove the old entry and re-run `configure_auto_scan`).
- **Query starvation on file saves**: saving `.cs` files while queries were in flight could
  freeze `understand`/`search` for a long time on large solutions — each saved file held the
  graph lock for a full rebuild and the cache was rewritten entirely on every flush, with
  concurrent saves failing silently. The incremental merge plus throttled, atomic cache
  writes (`GraphStore` temp-file + move) eliminate both stalls and corrupted/failed saves.
- PageRank is now only recomputed on full rebuilds; body-edit deltas keep the previous
  ranking (suggestion ordering only, never edge correctness).

### Tests
- 11 new tests (`IncrementalMergeTests`) asserting delta-vs-full-rebuild equivalence
  (stats + query outputs) for body edits, batches, removals, structural fallbacks, chained
  receivers, partial classes, `RemoveFile`, BM25 index, and a delta latency smoke test.
- 15 new tests (`MermaidTests`) covering the three diagram tools (see Added).
- 8 new tests (`DocIndexTests`) covering markdown parsing (title/sections, code-fence skip),
  heading-weighted BM25 ranking, config-JSON filtering, doc↔type mentions (including the
  `understand`/`search` `[docs:N]` integration), excluded directories, and incremental rescans.

## [2.2.0] - 2026-10-02

### Added
- **`impact()` — radio de impacto de cambio (blast radius)**: nueva herramienta MCP
  (+ `sharpgraph impact` en CLI) que responde "¿qué rompo si cambio X?" en UNA llamada:
  BFS transitivo sobre los callers resumido por niveles (con la relación de cada arista),
  endpoints HTTP en riesgo (verbo + ruta) y tests que ejercitan el área. Propaga DI —
  cambiar una implementación afecta a la interfaz que registra y de ahí a todos sus
  consumidores; los tests se listan siempre aparte y solo entran en los niveles con
  `includeTests`. Server instructions actualizadas con el paso "¿QUÉ ROMPO SI CAMBIO X?
  → impact(X)". Medido sobre CleanArchitecture: la respuesta transitiva completa
  (32 tipos, 4 niveles, 9 tests) cuesta **362 tokens**; el `callers` de CodeGraph gasta
  1.007 tokens en SOLO el nivel directo.
- **Mantenimiento automático del directorio de caché**: los `.tmp` huérfanos de writes
  atómicos interrumpidos (procesos muertos a mitad de save) se borran al arrancar (umbral
  de 10 min para no tocar saves en curso de otro proceso), y se conservan como máximo 10
  cachés de soluciones distintas por LRU — antes el directorio solo crecía (se hallaron
  81 MB con huérfanos de días atrás). `Save` ignora listas vacías para no pisar una
  caché buena con un grafo vacío.

### Changed
- **Caché binaria v2 (`.sgcache`)**: `GraphStore` pasa de JSON a un formato binario propio
  (`Persistence/FragmentBinary.cs`): todas las cadenas deduplicadas en una tabla única al
  inicio del fichero y el cuerpo como índices varint. Sin dependencias nuevas. Medido sobre
  un corpus sintético de 5.001 ficheros / 4.900 tipos / 39.600 aristas: **16,7 MB → 1,9 MB
  (8,8×)**, carga **3,6 s → ~0,12 s (30×)**, save completo **173 ms → ~55 ms**. Las cachés
  `.json` con el mismo ParserVersion se leen para migración y el siguiente save las
  reemplaza. `TryLoad` además filtra los ficheros desaparecidos con UN walk recursivo del
  árbol (HashSet) en vez de un `File.Exists` por fragmento: el stat × N era el auténtico
  cuello de botella del arranque en frío (2,4-6,4 s solo en stats con rutas de nombre
  corto 8.3).
- **Normalización de rutas**: scanner, caché y comparaciones usan siempre `Path.GetFullPath`,
  que expande los nombres corto 8.3 (`C:\Users\JAVIER~1\...`) a la forma larga — antes la
  misma carpeta escaneada por ruta corta y larga producía fragmentos que no casaban y
  re-parseos completos. `GraphStore` gana un constructor con cacheDir inyectable para tests.
- **Telemetría ligera de caché**: `TryLoad`/`Save` loguean a stderr fragmentos, ms y MB
  (`Cache loaded (binary): 5001 fragments in 51ms read + 64ms stat (1,9 MB)`).

## [2.1.0] — 2026-07-21

First public beta. The first release with a published changelog; earlier
internal history is summarized at the bottom.

### Added
- **Tests**: xUnit test project (`src/SharpGraph.Tests/`) with 45 tests covering
  `TypeReferenceVisitor` extraction (MediatR, Minimal API, DI registrations in
  all forms, nested types, generic containers, routing, ambiguous names, false
  positives) and `CodeGraph` queries (`resolve_di`, `find_callers`,
  `find_call_sites`, `trace_to_endpoints`, `flow` cycles). Synthetic `.cs`
  fixtures are embedded as resources so tests run without touching disk.
- **Call-site coverage (Fases A + B)**: `find_call_sites` now recognizes
  patterns that previously caused silent loss of invocations:
  - Null-conditional `_svc?.Method()` (visitor-level fix).
  - Factory/chaining `_factory.Get().Method()`, `a.B().C().M()` (two-pass
    resolution via the new `MemberReturnSignature` index).
  - Deep member-access `_outer.Inner.Method()`.
  - `var x = await svc.GetAsync(); x.Method()` (via the new `PendingLocal`
    mechanism).
  - Lambdas (already worked; explicit regression tests added).
- **Multiplatform**: publish targets `win-x64`, `linux-x64`, `osx-arm64` as
  self-contained single-file binaries. New `publish-all.ps1` builds and packages
  all three.
- **Multi-client support**: new `docs/CLIENTS.md` with verified registration
  snippets for Claude Code, Cursor, Cline, Continue, Zed, and generic VS Code.
  New `install.sh` for macOS/Linux; `install.ps1` refactored with
  `-Client`/`-ConfigureHook`/`-InstallPath` flags.
- **CI/CD**: GitHub Actions workflows for CI (`ci.yml`, runs tests on every PR)
  and release (`release.yml`, builds 3 RIDs and publishes a GitHub Release on
  tag `v*`).
- **Community health files**: `LICENSE` (MIT), `CONTRIBUTING.md`, `SECURITY.md`,
  `CODE_OF_CONDUCT.md`, issue templates, `CODEOWNERS`.
- **Public benchmark**: `docs/BENCHMARK.md` updated with results over
  [CleanArchitecture](https://github.com/JasonTaylorDev/CleanArchitecture),
  reproducible with `bench/benchmark.py bench/questions.cleanarchitecture.py`.
- **Docs**: `docs/COMPARATIVA.md` (public, anonymized comparison with
  CodeGraph, Sourcegraph MCP, code-graph-mcp).
- **Quickstart** section in README (5 minutes from download to first query).
- **Demo script** (`demo.ps1` / `demo.sh`) reproducing the headline queries
  on CleanArchitecture.

### Changed
- `SharpGraph.csproj` no longer hardcodes `win-x64`; the RID is passed at
  publish time.
- `ServerInfo.Version` bumped to `2.1.0`.
- `ParserVersion` bumped to `7` (the `FileFragment` model gained
  `ReturnSignatures`, `PendingCallSites`, and `PendingLocals`; old caches are
  invalidated automatically).
- `docs/ARCHITECTURE.md` corrected: it previously claimed "no persistence at
  all", but `GraphStore` has cached to disk since v2.0. The persistence,
  incremental scan, and watcher sections now reflect reality.
- README header now states the niche unambiguously: **C#/.NET-only,
  token-efficient**.

### Fixed
- `DetectSend` no longer treats arbitrary `Send`/`Publish`/`Dispatch` calls as
  MediatR/bus messages: it now validates the receiver type against a list of
  known bus types (`IMediator`, `IBus`, `IDispatcher`, …), eliminating false
  positives like `smtp.Send(email)`.
- `flow()` cycle handling: the existing `visited` deduplication was confirmed
  correct via contract tests; a comment was added to `RenderFlow` explaining
  why cycles are cut.
- `configure_auto_scan()` MCP tool no longer blindly writes to
  `~/.claude/settings.json` when the Claude Code folder doesn't exist: it
  returns an explanatory message instead.

### Known limitations (documented in README)
- Indexer receivers (`_map[key].Method()`) and top-level statements with DI
  chaining remain unsupported. See "Limitaciones conocidas" in README.

## [2.0.0] — 2026-05 (internal)

- Rewritten as a .NET MCP server with a token-efficient graph.
- MediatR, DI, Minimal API, and ASP.NET Core routing modeled explicitly.
- 15 MCP tools: `scan`, `trace_to_endpoints`, `find_callers`, `get_usages`,
  `find_call_sites`, `get_source`, `understand`, `flow`, `resolve_di`,
  `search`, `explore_context`, `hubs`, `search_semantic`, `stats`,
  `configure_auto_scan`.
- Disk cache with `ParserVersion` and `FileSystemWatcher` for incremental
  updates.

## [1.0.0] — earlier internal versions

- Initial prototype. Replaced by v2.

[Unreleased]: https://github.com/JavierFrauca/sharpgraph/compare/v2.3.0...HEAD
[2.3.0]: https://github.com/JavierFrauca/sharpgraph/compare/v2.2.0...v2.3.0
[2.2.0]: https://github.com/JavierFrauca/sharpgraph/releases/tag/v2.2.0
[2.1.0]: https://github.com/JavierFrauca/sharpgraph/releases/tag/v2.1.0
[2.0.0]: https://github.com/JavierFrauca/sharpgraph/compare/v1.0.0...v2.0.0
