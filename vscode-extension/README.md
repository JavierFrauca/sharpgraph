# SharpGraph Flow (extensión VS Code)

Diagrama descendente desde cualquier endpoint HTTP de tu solución .NET:
`controller → command/query (MediatR) → handler → servicios → implementaciones DI`.
Clic en un bloque del diagrama → se abre el fichero real en su línea exacta.

Es la cara visual del MCP server [SharpGraph](https://github.com/JavierFrauca/sharpgraph):
la extensión **no parsea código** — lanza el binario de SharpGraph como servidor MCP
stdio y consume `list_endpoints` + `endpoint_flow`. Un motor, dos consumidores
(tu LLM y tu editor). El prototipo visual de referencia está en
[design/vscode-extension-prototype.html](../design/vscode-extension-prototype.html).

## Requisitos

- El ejecutable de SharpGraph ≥ 2.4.0 publicado (`publish.ps1` en el repo del motor,
  o `sharpgraph update`). Si está en PATH basta con el nombre; si no, configura
  `sharpgraphFlow.serverPath` (botón de la tuerca en la vista).
- Un workspace con .sln/.csproj (o `sharpgraphFlow.solutionPath`).

## Uso

1. Icono **SharpGraph Flow** en la Activity Bar (badge = nº de endpoints indexados).
2. Árbol de endpoints agrupado por controlador, con verbos de color.
3. Clic en un endpoint → webview con el diagrama descendente.
4. Clic en cualquier bloque → abre el fichero en esa línea (editor real de VS Code).
5. Toggle **infraestructura** en el webview: muestra/oculta ILogger, ControllerBase…
6. **Ver Mermaid**: el bloque `flowchart` equivalente para pegar en docs.

## Comandos

| Comando | Qué hace |
|---|---|
| `SharpGraph Flow: Reindexar` | re-escanea la solución (el watcher de VS Code también lo hace al guardar .cs) |
| `SharpGraph Flow: Configurar ruta de SharpGraph` | apunta al ejecutable del motor |

## Configuración

| Setting | Defecto | Descripción |
|---|---|---|
| `sharpgraphFlow.serverPath` | `SharpGraph` | ejecutable del MCP server |
| `sharpgraphFlow.solutionPath` | *(autodetecta)* | .sln/.csproj/carpeta a indexar |
| `sharpgraphFlow.includeInfra` | `false` | mostrar infraestructura por defecto |
| `sharpgraphFlow.maxNodes` | `80` | tope de nodos por diagrama (5-200) |

## Desarrollo

```bash
npm install
npm run compile         # esbuild → dist/extension.js
npm run preview         # vuelca design/flow-preview.html con un flujo sintético
npm run package         # VSIX (requiere vsce)
```

Licencia MIT.
