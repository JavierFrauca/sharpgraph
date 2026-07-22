# Comparativa detallada: CodeGraph vs LocalGraph vs Sin MCP

Fecha: 2026-05-23
Repositorio: Payroll
Caso de análisis: localizar desde dónde se usa `GrossService` / `IGrossService`

## 1. Objetivo

Comparar tres enfoques para resolver una misma pregunta técnica:

- Enfoque A: MCP `CodeGraph`
- Enfoque B: MCP `LocalGraph`
- Enfoque C: Sin MCP (búsqueda textual + lectura manual)

La pregunta usada para la comparación fue:

> "¿Desde dónde se usa el servicio GrossService?"

## 2. Alcance y criterios de evaluación

### 2.1 Alcance

La comparación cubre el proceso completo de investigación:

1. Encontrar el símbolo correcto (`GrossService` o `IGrossService`)
2. Identificar consumidores (callers)
3. Identificar llamadas concretas a métodos
4. Relacionar uso en flujo de aplicación (handlers/servicios/endpoints)
5. Estimar coste total (tokens y pasos)

### 2.2 Criterios

- Precisión estructural: calidad para responder "quién depende de quién"
- Precisión operativa: calidad para responder "dónde se invoca realmente"
- Ruido: cantidad de resultados irrelevantes o ambiguos
- Coste operativo: número de llamadas y esfuerzo manual
- Coste de tokens: estimación por tamaño de respuestas y número de pasos

## 3. Metodología y limitaciones

### 3.1 Metodología

Se ejecutó el mismo caso práctico en los tres enfoques, recogiendo:

- número de pasos aproximado
- tipo de resultado devuelto
- facilidad para llegar a un resultado accionable
- volumen de salida textual (aproximación al consumo de tokens)

### 3.2 Limitaciones

No hay telemetría auditada por herramienta MCP sobre tokens facturados en esta sesión.

Por tanto, los valores de tokens de este documento son:

- comparativos
- orientativos
- válidos para priorización técnica
- no válidos como contabilidad de facturación

## 4. Flujo de trabajo por enfoque

## 4.1 CodeGraph

Flujo típico aplicado:

1. `codegraph_context` para contexto inicial de símbolo y relaciones
2. `codegraph_callers` (en este caso, con resultados limitados para interfaz inyectada)
3. `codegraph_explore` para agrupar símbolos y fuentes relacionadas
4. lectura puntual de ficheros concretos para confirmar invocaciones

Fortalezas observadas:

- muy bueno para aterrizar en símbolos y código relacionado
- útil para bajar a uso real por método

Debilidades observadas:

- en este caso, `callers` directo sobre interfaz inyectada no fue suficiente por sí solo
- necesita combinar varias vistas para cerrar la respuesta

## 4.2 LocalGraph

Flujo típico aplicado:

1. `stats` para comprobar si el grafo estaba cargado
2. `scan` de solución cuando el grafo estaba vacío
3. `search("IGrossService")` para nombre exacto
4. `find_callers("IGrossService")` para red de dependencias
5. `trace_to_endpoints("IGrossService")` para rutas hacia endpoints

Fortalezas observadas:

- excelente para mapa de dependencias por tipo
- rápido para descubrir red de consumidores

Debilidades observadas:

- `trace_to_endpoints` puede introducir ruido en cadenas largas
- en este caso aparecieron rutas de baja fiabilidad semántica para "llamada real"

## 4.3 Sin MCP

Flujo típico equivalente:

1. `grep` de `IGrossService`
2. `grep` de `_grossService.` para llamadas efectivas
3. lecturas manuales de handlers/servicios clave
4. consolidación manual de hallazgos

Fortalezas observadas:

- muy eficaz cuando el nombre del símbolo es estable y distintivo
- control total del investigador sobre filtros y evidencia

Debilidades observadas:

- más manual y más propenso a sesgo por omisión
- peor escalabilidad para análisis estructural profundo

## 5. Evidencia funcional (caso `IGrossService`)

Se identificaron consumidores y llamadas reales en capas de cálculo de nómina.

Ejemplos de puntos de uso representativos:

- `InternalPayrollCalculationCommand.Handler`
- `InternalExtraPayCalculationCommand.Handler`
- `InternalDelayCalculationCommand.Handler`
- `BonusCalculationCommand.Handler`
- `ComplementaryL02CalculationCommand.Handler`
- `ComplementaryV03CalculationCommand.Handler`
- `ConciliationCalculationCommand.Handler`
- `DelayImportCalculationCommand.Handler`
- `IrpfService`
- `SettlementSalaryService`
- `SettlementExtraPayService`
- `ComplementService`
- `AggregateAccrualService`
- `ExtraPaysHolidayService`

Registro DI detectado:

- `AddScoped<IGrossService, GrossService>()`

## 6. Tabla comparativa (proceso completo)

| Dimensión | CodeGraph | LocalGraph | Sin MCP |
|---|---|---|---|
| Objetivo principal en el que destaca | contexto de símbolos + uso en código | árbol de dependencias C# por tipo | verificación manual de usos reales |
| Flujo completo típico | contexto -> callers -> explore -> validación puntual | stats -> scan -> search -> find_callers -> trace_to_endpoints | grep interfaz -> grep invocaciones -> read manual |
| Nº de llamadas (aprox.) | 5-8 | 5-7 | 8-15 |
| Volumen de salida al modelo | medio-alto | bajo-medio | medio |
| Tokens del proceso completo (aprox.) | 6k-15k | 2k-8k | 4k-12k |
| Precisión estructural | alta | muy alta | media |
| Precisión operativa (método invocado) | alta | media | alta |
| Ruido observado en este caso | medio | medio-alto en trazas a endpoint | bajo-medio (depende del filtro humano) |
| Esfuerzo manual | medio | bajo | alto |
| Escalabilidad para auditoría amplia | alta | alta | media-baja |

## 7. Interpretación de coste de tokens

### 7.1 Qué se entiende por "token" aquí

En esta comparativa, "tokens" representa una estimación de contexto procesado por el modelo para llegar a la respuesta, aproximada por:

- cantidad de llamadas
- longitud de respuesta por llamada
- proporción de salida útil frente a ruido

### 7.2 Rango por enfoque

- CodeGraph: más contexto por llamada, mayor densidad semántica, coste medio-alto
- LocalGraph: respuestas compactas para estructura, coste bajo-medio
- Sin MCP: coste variable; puede crecer por iteraciones manuales

## 8. Resultado comparado para este caso concreto

### 8.1 Quién gana por objetivo

- Mejor mapa de dependencias por tipo: LocalGraph
- Mejor respuesta accionable de uso real por método: CodeGraph
- Mejor alternativa cuando no se quiere depender de MCP: Sin MCP

### 8.2 Ranking práctico

1. Para arquitectura/dependencias: LocalGraph
2. Para investigar invocaciones reales: CodeGraph
3. Para casos simples y directos: Sin MCP

## 9. Recomendación de uso combinado

Secuencia recomendada para investigación técnica de servicios en Payroll:

1. LocalGraph para identificar rápidamente red de dependientes
2. CodeGraph para confirmar símbolos y aterrizar en llamadas reales
3. Búsqueda textual sin MCP como validación final o fallback

Esta estrategia reduce tiempo total y mejora robustez de la respuesta.

## 10. Plantilla reutilizable de decisión rápida

Usar esta guía para próximos análisis:

- Pregunta "quién depende de X": empezar por LocalGraph
- Pregunta "dónde se llama X": empezar por CodeGraph
- Pregunta puntual y símbolo muy claro: empezar por sin MCP (grep)

## 11. Riesgos y mitigaciones

Riesgo: confundir dependencia inyectada con llamada efectiva.

Mitigación:

1. verificar `_service.` (invocación real)
2. revisar método `Handle(...)` en handlers relevantes
3. contrastar con registro DI

Riesgo: sobreinterpretar trazas a endpoints en grafos amplios.

Mitigación:

1. usar profundidad moderada
2. validar con llamadas explícitas en código
3. priorizar interfaces de dominio frente dependencias transversales

## 12. Conclusión final

No existe un único "mejor" enfoque universal.

Para este caso (`IGrossService`):

- LocalGraph fue más eficiente para mapa estructural.
- CodeGraph fue más eficaz para respuesta operativa accionable.
- Sin MCP fue competitivo como validación, con mayor esfuerzo manual.

La combinación LocalGraph + CodeGraph + validación textual puntual ofrece el mejor equilibrio entre coste, precisión y trazabilidad.

## 13. Decisión operativa recomendada (simplificada)

Para este repositorio, se adopta como estrategia principal:

- LocalGraph para contexto estructural (tipos y dependencias)
- grep para confirmar llamadas reales por método
- LLM para síntesis y priorización de hallazgos

No se recomienda usar dos grafos en el mismo flujo estándar, salvo análisis excepcional.

### Flujo estándar propuesto

1. LocalGraph: `search("IGrossService")`
2. LocalGraph: `find_callers("IGrossService", depth: 1..2)`
3. LocalGraph: `explore_context("IGrossService")`
4. grep: buscar invocaciones reales (`_grossService.` o equivalente)
5. Lectura puntual de 2-4 ficheros críticos para validar orden de ejecución
6. Síntesis final del LLM con riesgos y confianza

### Ventajas del enfoque LocalGraph + grep

- Menor complejidad operativa
- Menor coste total de contexto/tokens en promedio
- Mejor control del ruido en trazas heurísticas
- Evidencia final más auditable (líneas de código con llamada explícita)

### Cuándo sí considerar un segundo grafo

Solo en casos concretos:

1. resolución de símbolos ambigua en una zona grande del código
2. investigación de regresión transversal difícil de cerrar
3. discrepancia no resuelta entre dependencias y llamadas reales

### Tabla de referencia rápida (modelo operativo)

| Estrategia | Coste operativo | Precisión estructural | Precisión de llamada real | Recomendación |
|---|---|---|---|---|
| LocalGraph + grep + LLM | bajo-medio | muy alta | alta | recomendada por defecto |
| LocalGraph + CodeGraph + grep + LLM | medio-alto | muy alta | alta | usar solo en casos excepcionales |
| Solo grep + LLM | medio | media | alta | útil para casos simples, menor escalabilidad |
