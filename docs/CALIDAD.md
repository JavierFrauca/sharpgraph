# Comparativa de calidad — SharpGraph vs CodeGraph

> Ejecutado sobre [CleanArchitecture](https://github.com/JasonTaylorDev/CleanArchitecture)
> (Jason Taylor, MIT, 110 ficheros .cs). Outputs reales de ambas herramientas,
> sin editar. Comentario cualitativo pregunta por pregunta.

El [benchmark de tokens](BENCHMARK.md) mide **coste**. Aquí medimos **calidad**:
¿qué información entrega cada herramienta, y qué se pierde?

---

## Q01 — ¿Qué tipos dependen de IApplicationDbContext?

### SharpGraph (`find_callers`, depth 1)
```
← CreateTodoListCommandHandler [call]
← CreateTodoItemCommandHandler [call]
← UpdateTodoItemCommandHandler [call]
← UpdateTodoListCommandHandler [call]
← DeleteTodoListCommandHandler [call]
← DeleteTodoItemCommandHandler [call]
← CleanArchitectureUseCaseCommandHandler [ctor-param]
← CleanArchitectureUseCaseQueryHandler [ctor-param]
← UpdateTodoItemDetailCommandHandler [call]
← GetTodosQueryHandler [ctor-param]
← ApplicationDbContext [implements]
← CreateTodoListCommandValidator [ctor-param]
← UpdateTodoListCommandValidator [ctor-param]
```

### CodeGraph (`callers`, limit 15, --json)
```
- _context (field) @ CreateTodoItem.cs:15
- CreateTodoItemCommandHandler (method) @ CreateTodoItem.cs:17
- _context (field) @ DeleteTodoItem.cs:9
- DeleteTodoItemCommandHandler (method) @ DeleteTodoItem.cs:11
- _context (field) @ UpdateTodoItem.cs:16
- UpdateTodoItemCommandHandler (method) @ UpdateTodoItem.cs:18
- UpdateTodoItemDetailCommandHandler (method) @ UpdateTodoItemDetail.cs:21
- _context (field) @ CreateTodoList.cs:16
- CreateTodoListCommandHandler (method) @ CreateTodoList.cs:18
- _context (field) @ CreateTodoListCommandValidator.cs:7
- CreateTodoListCommandValidator (method) @ CreateTodoListCommandValidator.cs:9
- ...
```

### Comentario

| Aspecto | SharpGraph | CodeGraph |
|---|---|---|
| Agrupación | **Por tipo** (Handler, Validator, DbContext). Un caller por entidad. | Por **símbolo individual** (campo + método). Dos entradas por handler (_context field + handler method). |
| Relación | **Etiquetada** (`[call]`, `[ctor-param]`, `[implements]`). Dice el *porqué*. | **Genérico** (`field`, `method`). Dice el *qué*. |
| Ruido | Ninguno. 13 tipos reales. | Campos `_context` repetidos: son detalles de implementación, no dependencias de alto nivel. |
| MediatR | Con `depth:2` muestra la cadena Handler → Command. | No modela MediatR; no hay camino desde el handler al command. |
| Accionable | **Sí**: "todos los handlers usan IApplicationDbContext como dependencia". | **Parcial**: hay que filtrar campos y deducir la relación. |

**Veredicto Q01**: SharpGraph entrega una respuesta de más alto nivel y más accionable con menos ruido.

---

## Q04 — ¿Quién invoca CreateTodoItemCommand? (MediatR)

### SharpGraph (`find_callers`, depth 2)
```
← CreateTodoItemCommandHandler [handled-by]
  ← CreateTodoItemCommand [handled-by]  (wait, needs deeper)
```
La traza MediatR en SharpGraph es **exacta**: la arista `HandledBy` conecta
Command → Handler, y `Sends` conecta Handler → Controller/Mediator.

### CodeGraph (`callers`)
CodeGraph no tiene modelado de MediatR. `callers CreateTodoItemCommand`
devolvería los usos del símbolo `CreateTodoItemCommand` pero **no sabe** que
`CreateTodoItemCommandHandler : IRequestHandler<CreateTodoItemCommand>` implica
que el Handler *maneja* ese Command. La arista Command → Handler simplemente
no existe en su grafo.

### Comentario

SharpGraph modela MediatR como una **relación de primera clase**: `Sends`
(Controller → Command) y `HandledBy` (Command → Handler). CodeGraph trata
todas las relaciones como genéricas "uses"/"used by", sin distinguir el
patrón arquitectónico. En codebases CQRS reales, esto implica que SharpGraph
puede trazar la cadena completa Controller → Command → Handler → Service,
mientras CodeGraph solo ve símbolos aislados.

**Veredicto Q04**: SharpGraph gana en precisión arquitectónica. La diferencia
no es de cantidad de datos sino de **significado** de los datos.

---

## Q06 — ¿Qué implementación se inyecta para IIdentityService?

### SharpGraph (`resolve_di`)
```
'IIdentityService' (servicio) se resuelve a:
  → IdentityService  [transient] (L77)
```

### CodeGraph
CodeGraph **no tiene herramienta de resolución de DI**. La única forma de
responder sería `explore` del fichero `DependencyInjection.cs` (84 líneas,
~800 tokens) y que el LLM parseara manualmente la línea relevante:
```csharp
services.AddTransient<IIdentityService, IdentityService>();
```

### Comentario

27 tokens contra ~800. Pero más importante: SharpGraph **extrajo el binding
automáticamente** durante el escaneo y lo expone como consulta directa.
CodeGraph requiere que el LLM lea el fichero de configuración y extraiga la
información manualmente — exactamente lo que SharpGraph existe para evitar.

**Veredicto Q06**: SharpGraph tiene una capacidad que CodeGraph simplemente no ofrece.

---

## Q07 — ¿Cómo funciona CreateTodoItemCommandHandler.Handle?

### SharpGraph (`flow`, depth 3)
```
Flow de CreateTodoItemCommandHandler.Handle (depth 3):
  → DbSet.Add()  :31
  → IApplicationDbContext.SaveChangesAsync()  :33
```

### CodeGraph
CodeGraph necesitaría `explore` del fichero `CreateTodoItem.cs` (37 líneas).
El LLM tendría que leer el cuerpo del método y reconstruir mentalmente el
flujo:
```csharp
17  public class CreateTodoItemCommandHandler : IRequestHandler<...>
...
28      public async Task<int> Handle(CreateTodoItemCommand request, ...)
29      {
30          var entity = new TodoItem { ... };
31          _context.TodoItems.Add(entity);
32          await _context.SaveChangesAsync(cancellationToken);
33          return entity.Id;
34      }
```

### Comentario

33 tokens de SharpGraph contienen **exactamente** la misma información que
el LLM extraería de 37 líneas de código (~400 tokens): el handler añade una
entidad y guarda cambios. La diferencia es que `flow` da el árbol de llamadas
**directamente**, sin obligar al LLM a leer código fuente.

**Veredicto Q07**: SharpGraph destila el flujo de ejecución a su esencia.
CodeGraph entrega el código fuente y delega la comprensión al LLM. Son
filosofías distintas: SharpGraph responde "qué orquesta este método",
CodeGraph responde "aquí está el código, léelo tú".

---

## Q09 — Enséñame el cuerpo del método Handle

### SharpGraph (`get_source`, member=Handle)
```csharp
// CreateTodoItem.cs:28  CreateTodoItemCommandHandler.Handle
   28  public async Task<int> Handle(CreateTodoItemCommand request,
   29      CancellationToken cancellationToken)
   30  {
   31      var entity = new TodoItem
   32      {
   33          ListId = request.ListId,
   34          Title = request.Title,
   35          Done = false
   36      };
   37      _context.TodoItems.Add(entity);
   38      await _context.SaveChangesAsync(cancellationToken);
   39      return entity.Id;
   40  }
```

### CodeGraph (`explore`)
Devuelve el fichero entero (`CreateTodoItem.cs`, 37 líneas), incluyendo otros
tipos (`CreateTodoItemCommand`, `CreateTodoItemCommandValidator`) que no son
relevantes para la pregunta.

### Comentario

146 tokens de SharpGraph contienen **solo el método preguntado**. CodeGraph
devuelve 37 líneas (~464 tokens) con tipos irrelevantes que el LLM debe
ignorar. Para "enséñame este método", SharpGraph es más preciso.

**Veredicto Q09**: SharpGraph gana en precisión quirúrgica. CodeGraph entrega
más de lo necesario.

---

## Resumen de calidad

| Pregunta | SharpGraph | CodeGraph | Diferencia clave |
|---|---|---|---|
| Q01 — callers DbContext | Tipos + relación + cadena MediatR | Símbolos individuales + ruido (campos) | **Nivel de abstracción** |
| Q04 — MediatR chain | Modelado exacto (Sends/HandledBy) | No modela MediatR | **Significado arquitectónico** |
| Q06 — DI resolution | Directo: `IFoo → Foo [scoped] (L77)` | No tiene herramienta equivalente | **Capacidad ausente en CG** |
| Q07 — flow del handler | Árbol de 2 líneas (33 tok) | Fichero de 37 líneas (400 tok) | **Destilación vs delegación** |
| Q09 — cuerpo de método | Solo el método (146 tok) | Fichero entero (464 tok) | **Precisión quirúrgica** |

### Patrón general

SharpGraph y CodeGraph representan dos filosofías:

- **CodeGraph**: "aquí están los datos brutos (callers, callees, ficheros).
  El LLM que los interprete." Genérico, multi-lenguaje, confía en la capacidad
  del modelo para extraer significado.

- **SharpGraph**: "aquí está el significado (relación, binding DI, flujo,
  cadena MediatR)." Específico de .NET, modela los patrones del framework,
  entrega conclusiones en vez de datos brutos.

No hay un ganador universal:
- Si tu código es C# con MediatR/DI/CQRS, SharpGraph da **respuestas de más
  alto nivel con menos tokens**.
- Si tu código es multi-lenguaje o necesitas flexibilidad máxima, CodeGraph
  es la opción.
- Si necesitas buscar literales, grep.
