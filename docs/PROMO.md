# Borrador de post para promoción en foros

> NO publique directamente. Adapta a tu estilo y al foro destino. Este es un
> borrador genérico sobre el que puedes trabajar. Revisa las normas del foro
> (auto-promoción, formato, etiquetas de self-promo) antes de publicar.

---

## Título sugerido (elige el que mejor encaje)

- Reddit r/dotnet: **"SharpGraph — un MCP server C#-first que entrega respuestas de más alto nivel que leer ficheros (y ~4× menos tokens)"**
- Reddit r/LocalLLaMA: **"Build a knowledge graph from your .NET codebase for your LLM — SharpGraph v2.1.0"**
- Reddit r/ClaudeAI: **"Better .NET answers in Claude: SharpGraph models MediatR/DI/routing so the LLM doesn't have to parse raw code"**
- Hacker News: **"Show HN: SharpGraph — C# code-graph MCP server that models framework patterns (MediatR, DI, ASP.NET routing)"**

## Cuerpo (para r/dotnet)

Hola r/dotnet!

He trabajado en un proyecto que creo vale la pena compartir: **SharpGraph**, un servidor MCP que escanea tu codebase C# y construye un grafo de dependencias en memoria, permitiendo que tu LLM (Claude Code, Cursor, Cline...) navegue el código **sin leer ficheros enteros**.

### ¿Por qué?

Imagina que quieres trazar "¿desde qué endpoint se llama a UserService?" en un proyecto con CQRS/MediatR. La cadena es Controller → Command → Handler → Service, repartida en 4-5 ficheros. Un LLM que intente responder leyendo esos ficheros uno a uno va a gastar cientos o miles de tokens, y puede perderse.

SharpGraph responde a esa pregunta en **~50-250 tokens**, con la cadena exacta y el file:line de cada paso. Lo mismo para "¿dónde se invoca de verdad SaveChangesAsync?" o "¿cómo funciona el flujo de CreateHandler?".

### ¿Qué modela?

- **MediatR/CQRS**: `_mediator.Send(new XCommand())` → `XCommand → XCommandHandler` modelado como aristas explícitas. La traza a endpoints cruza el diamante MediatR de forma exacta, no heurística.
- **Inyección de dependencias**: `AddScoped<I,C>()`, `AddTransient(typeof(I), typeof(C))`, `AddKeyedSingleton<>`.
- **Routing ASP.NET Core**: `[Route("api/[controller]")] [HttpGet("{id}")]` → ruta combinada, más Minimal APIs `app.MapGet(...)`.
- **Call-sites reales**: distingue "dependencia inyectada" de "llamada de verdad", con file:line del lugar donde se invoca.
- **Comprensión de flujo**: `flow(X, M)` devuelve el árbol de llamadas siguiendo DI interface→impl sin devolver código, ~20-30× más barato que leer los cuerpos.

### El benchmark (público y reproducible)

Lo probamos sobre [CleanArchitecture](https://github.com/JasonTaylorDev/CleanArchitecture) (110 ficheros .cs, CQRS/MediatR/DI). 13 preguntas, medición con tiktoken cl100k_base:

| | SharpGraph | CodeGraph | grep+read |
|---|---|---|---|
| Total tokens (13 preguntas) | **1,715** | 6,101 (3.6×) | 12,039 (7.0×) |
| Preguntas ganadas | **9/13** | 1/13 | 3/13 |
| Comprensión de flujo (`flow`) | **23-33 tok** | 464-683 tok (~20×) | 183-575 tok |

Reproducible: `git clone`, `pip install tiktoken`, `python benchmark.py`. Todo en el repo.

### Pero no es solo ahorrar tokens — es que la información es MEJOR

Hicimos una [comparativa de calidad](https://github.com/JavierFrauca/sharpgraph/blob/main/docs/CALIDAD.md) ejecutando las mismas preguntas en ambas herramientas. Ejemplos reales:

**"¿Quién depende de IApplicationDbContext?"**
- CodeGraph devuelve símbolos individuales con ruido: `_context (field)`, `_context (field)`, `_context (field)`... repetidos por cada handler.
- SharpGraph devuelve **tipos con la relación**: `CreateTodoItemCommandHandler [call]`, `GetTodosQueryHandler [ctor-param]`, `ApplicationDbContext [implements]`. Sin ruido, accionable.

**"¿Quién invoca CreateTodoItemCommand?" (MediatR)**
- CodeGraph **no modela MediatR**: no sabe que `IRequestHandler<CreateTodoItemCommand>` conecta el Handler con el Command.
- SharpGraph modela la cadena completa: `Controller →sends→ Command →handled-by→ Handler`. Aristas de primera clase.

**"¿Qué implementación se inyecta para IIdentityService?"**
- CodeGraph **no tiene herramienta equivalente**: hay que leer `DependencyInjection.cs` (84 líneas, ~800 tokens) y parsearlo manualmente.
- SharpGraph responde en 1 llamada: `IIdentityService → IdentityService [transient] (L77)`.

**"¿Cómo funciona CreateTodoItemCommandHandler.Handle?"**
- CodeGraph te devuelve el fichero entero (37 líneas, ~400 tokens) para que el LLM lo lea.
- SharpGraph destila el flujo en 2 líneas: `→ DbSet.Add() :31 → IApplicationDbContext.SaveChangesAsync() :33`.

La diferencia no es de cantidad de datos: es de **significado**. SharpGraph entrega conclusiones (relación, binding DI, flujo, cadena MediatR); CodeGraph entrega datos brutos y delega la interpretación al LLM. Para código .NET con patrones de framework, eso marca la diferencia entre una respuesta útil y una respuesta barata pero incompleta.

> Ver la [comparativa completa de calidad](https://github.com/JavierFrauca/sharpgraph/blob/main/docs/CALIDAD.md) con outputs reales lado a lado.

### Limitaciones honestas

No es una bala de plata:
- **Solo C#** (parsing con Roslyn AST, sin compilar: rápido pero sin sobrecargas/ext methods/dynamic).
- **No indexa literales** (para strings, grep sigue siendo la herramienta).
- **No modela types externos** (BCL/NuGet) en call-sites encadenados (host.Services.GetRequiredService<T>()).

Está documentado en el README, con una sección "Lo que SÍ resuelve" y "Lo que NO resuelve".

### Prueba

```bash
git clone https://github.com/JavierFrauca/sharpgraph.git
cd sharpgraph
.\demo.ps1   # o ./demo.sh en macOS/Linux
```

La demo escanea CleanArchitecture y ejecuta 5 queries clave.

### Estado

v2.1.0, beta pública. MIT. 45 tests (xUnit). Multiplataforma (win-x64, linux-x64, osx-arm64, self-contained binaries). Documentación para registrarlo en Claude Code, Cursor, Cline, Continue y Zed.

Feedback bienvenido — issues, PRs, discusiones en el repo.

---

## Publicar en este orden (sugerido)

1. **r/dotnet** primero (target principal, ~280k miembros)
2. **r/LocalLLaMA** / **r/MachineLearning** segundo (~140k + ~2.8M, según tono)
3. **r/ClaudeAI** tercero (~40k, si usas Claude Code como cliente principal)
4. **r/programming** / **Hacker News** solo si los anteriores generan tracción (son masivos y el post puede pasar desapercibido si no hay momentum)
5. **Threads / Newsletters / Blogs** solo si se convierte en noticia

---

## Pre-publicación checklist

Antes de publicar en foros:
- [ ] **Dogfooding serio**: 2-3 sesiones de uso real en tu proyecto .NET. Si encuentras bugs, arréglalos antes de publicar.
- [ ] **La demo funciona** desde un `git clone` limpio.
- [ ] **El benchmark es reproducible** siguiendo las instrucciones del README.
- [ ] **Los enlaces funcionan** (releases, docs, clients, comparativa, benchmark).
- [ ] **La licencia está clara** (MIT en el repo).
- [ ] **Estás preparado para responder issues en <24h** durante la primera semana. El mejor marketing es un mantenedor presente.
