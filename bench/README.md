# Benchmark de tokens — LocalGraph vs CodeGraph vs sin-MCP

Mide, con cuenta **exacta** de tokens (`tiktoken`, `cl100k_base`), cuántos tokens
entran en el contexto del modelo para responder una misma batería de preguntas con
tres enfoques:

- **LocalGraph** — sus herramientas MCP (se dirige el ejecutable por stdio).
- **CodeGraph** — su CLI `codegraph … --json` reformateado al formato de su salida MCP;
  el coste de su herramienta `explore` se modela como la fuente verbatim numerada de los
  ficheros que devuelve + ~230 tokens de framing.
- **sin-MCP** — salida de `grep` + el contenido íntegro de los ficheros que habría que leer.

Solo se mide **coste** (no calidad), y una herramienta solo "gana" una pregunta si
**responde de verdad**: en preguntas de literal, por ejemplo, únicamente grep es elegible
(los grafos no indexan literales); los resultados vacíos se marcan y no cuentan como victoria.

## Reproducir

```bash
pip install tiktoken
# instala el CLI de CodeGraph (npm) si quieres la columna CodeGraph:  npm i -g codegraph
# 1) índice de CodeGraph en cada repo a medir:
codegraph init /ruta/a/tu/repo
# 2) define tu batería:
cp bench/questions.example.py bench/questions.py   # edítalo con tus repos y símbolos
# 3) compila LocalGraph en Release (genera el .exe):
./publish.ps1     # o: dotnet build src/LocalGraph -c Release
# 4) ejecuta:
cd bench && python benchmark.py [ruta_opcional_al_LocalGraph.exe]
```

Genera `RESULTS.md` con: resumen, desglose por categoría, tabla por pregunta y conclusiones.

`questions.py` y `RESULTS.md` quedan fuera de git (son específicos de tus repos);
parte de [`questions.example.py`](questions.example.py), que documenta el esquema completo.

## Qué encontramos (batería interna)

Batería de **31 preguntas** de 5 tipos sobre **dos repos .NET reales** (≈100 y ≈240
ficheros `.cs`, con inyección de dependencias, MediatR/CQRS y controllers). El agregado
mezcla usos muy distintos, así que lo importante es el desglose por categoría:

| Tipo de pregunta | Ganador | Margen (tokens) |
|---|---|---|
| **Navegar / localizar / resolver** (deps, DI, call-sites, endpoints, hubs) | **LocalGraph** | **~7× vs CodeGraph · ~16× vs grep** |
| **Comprensión de flujo** (`flow`: árbol de llamadas siguiendo DI) | **LocalGraph** | **~45×** más barato que leer la cadena |
| **Leer la clase completa** | empate | `understand` gana clases grandes; leer el fichero gana las pequeñas |
| **Explicar la lógica de un método** | empate | requiere su fuente (`get_source`); el techo es leerla |
| **Literales / strings** | **grep** | los grafos no indexan literales |

**Lectura.** Para el uso dominante de un agente —navegar el código, localizar usos,
resolver inyección de dependencias, trazar a endpoints y entender flujos— LocalGraph
responde con datos derivados del grafo (relación, línea, binding DI, centralidad, árbol
de llamadas) en salida compacta, y ahorra del orden de **un orden de magnitud** de tokens.
La filosofía de CodeGraph (devolver fuente verbatim multi-fichero) brilla cuando el objetivo
es *leer* código a fondo; ahí la diferencia es paridad. Para literales, grep es la herramienta.

> Los valores son del entorno de prueba interno; reprodúcelos sobre tus propios repos con los
> pasos de arriba. El **ratio** entre enfoques es robusto porque los tres se miden con el mismo tokenizador.
