# Borrador de post para promoción en foros

> NO publique directamente. Adapta a tu estilo y al foro destino. Este es un
> borrador genérico sobre el que puedes trabajar. Revisa las normas del foro
> (auto-promoción, formato, etiquetas de self-promo) antes de publicar.

---

## Título sugerido (elige el que mejor encaje)

- Reddit r/dotnet: **"LocalGraph — un MCP server C#-first que ahorra ~4-8× tokens al LLM vs leer ficheros enteros"**
- Reddit r/LocalLLaMA: **"Build a knowledge graph from your .NET codebase for your LLM — LocalGraph v2.1.0"**
- Reddit r/ClaudeAI: **"Snappier Claude in .NET repos: 4-8× fewer tokens than reading files with this MCP server"**
- Hacker News: **"Show HN: LocalGraph — token-efficient code navigation for .NET (MCP)"**

## Cuerpo (para r/dotnet)

Hola r/dotnet!

He trabajado en un proyecto que creo vale la pena compartir: **LocalGraph**, un servidor MCP que escanea tu codebase C# y construye un grafo de dependencias en memoria, permitiendo que tu LLM (Claude Code, Cursor, Cline...) navegue el código **sin leer ficheros enteros**.

### ¿Por qué?

Imagina que quieres trazar "¿desde qué endpoint se llama a UserService?" en un proyecto con CQRS/MediatR. La cadena es Controller → Command → Handler → Service, repartida en 4-5 ficheros. Un LLM que intente responder leyendo esos ficheros uno a uno va a gastar cientos o miles de tokens, y puede perderse.

LocalGraph responde a esa pregunta en **~50-250 tokens**, con la cadena exacta y el file:line de cada paso. Lo mismo para "¿dónde se invoca de verdad SaveChangesAsync?" o "¿cómo funciona el flujo de CreateHandler?".

### ¿Qué modela?

- **MediatR/CQRS**: `_mediator.Send(new XCommand())` → `XCommand → XCommandHandler` modelado como aristas explícitas. La traza a endpoints cruza el diamante MediatR de forma exacta, no heurística.
- **Inyección de dependencias**: `AddScoped<I,C>()`, `AddTransient(typeof(I), typeof(C))`, `AddKeyedSingleton<>`.
- **Routing ASP.NET Core**: `[Route("api/[controller]")] [HttpGet("{id}")]` → ruta combinada, más Minimal APIs `app.MapGet(...)`.
- **Call-sites reales**: distingue "dependencia inyectada" de "llamada de verdad", con file:line del lugar donde se invoca.
- **Comprensión de flujo**: `flow(X, M)` devuelve el árbol de llamadas siguiendo DI interface→impl sin devolver código, ~20-30× más barato que leer los cuerpos.

### El benchmark (público y reproducible)

Lo probamos sobre [CleanArchitecture](https://github.com/JasonTaylorDev/CleanArchitecture) (110 ficheros .cs, CQRS/MediatR/DI). 12 preguntas, medición con tiktoken cl100k_base:

| | LocalGraph | CodeGraph | grep+read |
|---|---|---|---|
| Total tokens (12 preguntas) | **1,494** | 5,637 (3.8×) | 11,808 (7.9×) |
| Preguntas ganadas | **9/12** | 1/12 | 2/12 |
| Comprensión de flujo (`flow`) | **23-33 tok** | 464-683 tok (~20×) | 183-575 tok |

Reproducible: `git clone`, `pip install tiktoken`, `python benchmark.py`. Todo en el repo.

### Limitaciones honestas

No es una bala de plata:
- **Solo C#** (parsing con Roslyn AST, sin compilar: rápido pero sin sobrecargas/ext methods/dynamic).
- **No indexa literales** (para strings, grep sigue siendo la herramienta).
- **No modela types externos** (BCL/NuGet) en call-sites encadenados (host.Services.GetRequiredService<T>()).

Está documentado en el README, con una sección "Lo que SÍ resuelve" y "Lo que NO resuelve".

### Prueba

```bash
git clone https://github.com/JavierFrauca/localgraph.git
cd localgraph
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
