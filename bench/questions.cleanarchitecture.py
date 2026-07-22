# -*- coding: utf-8 -*-
"""
Batería PÚBLICA de preguntas para el benchmark sobre CleanArchitecture
(https://github.com/JasonTaylorDev/CleanArchitecture, licencia MIT).

Reproducible: clona el repo, instala dependencias y ejecuta
    python benchmark.py publish/SharpGraph.exe questions.cleanarchitecture

Cada pregunta indica cómo resolverla con cada enfoque:
  - SharpGraph (herramientas MCP)
  - CodeGraph  (CLI `codegraph` + modelo de su `explore`)
  - sin-MCP    (grep + lectura de ficheros)

`favor` documenta para qué enfoque se diseñó la pregunta (no afecta a la medición):
  LG = estructura/navegación .NET · CG = leer/entender código · FLOW = flujo de llamadas
  GREP = literales · NEU = neutral
"""

import os

# Ruta al repo CleanArchitecture clonado. Por defecto: bench/_external/CleanArchitecture
# (añadido a .gitignore; el usuario lo clona con `git clone --depth 1 ...`).
_DEFAULT_REPO = os.path.join(os.path.dirname(__file__), "_external", "CleanArchitecture")
REPO_ENV = os.environ.get("CLEANARCH_REPO", _DEFAULT_REPO)

REPOS = {
    "CA": REPO_ENV,
}

Q = [
    # ============ estructura / navegación (favorece SharpGraph) ============

    dict(id="Q01", repo="CA", favor="LG", type="deps",
         q="¿Qué tipos dependen de IApplicationDbContext?",
         lg=[("find_callers", {"typeName": "IApplicationDbContext", "depth": 2})],
         cg={"mode": "callers", "symbol": "IApplicationDbContext",
             "fallback_files": ["src/Application/Common/Interfaces/IApplicationDbContext.cs"]},
         grep=["IApplicationDbContext"], read_hits=True),

    dict(id="Q02", repo="CA", favor="LG", type="callsites",
         q="¿Dónde se invoca IApplicationDbContext.SaveChangesAsync?",
         lg=[("find_call_sites", {"typeName": "IApplicationDbContext", "member": "SaveChangesAsync"})],
         cg={"mode": "callers", "symbol": "SaveChangesAsync",
             "fallback_files": ["src/Application/Common/Interfaces/IApplicationDbContext.cs"]},
         grep=["SaveChangesAsync"], grep_fixed=True, read_hits=True),

    dict(id="Q03", repo="CA", favor="LG", type="deps",
         q="¿De qué depende CreateTodoItemCommandHandler?",
         lg=[("get_usages", {"typeName": "CreateTodoItemCommandHandler"})],
         cg={"mode": "callees", "symbol": "CreateTodoItemCommandHandler",
             "fallback_files": ["src/Application/TodoItems/Commands/CreateTodoItem/CreateTodoItem.cs"]},
         grep=["CreateTodoItemCommandHandler"], read_files=[
             "src/Application/TodoItems/Commands/CreateTodoItem/CreateTodoItem.cs"]),

    dict(id="Q04", repo="CA", favor="LG", type="mediatr",
         q="¿Quién invoca la command CreateTodoItemCommand (MediatR)?",
         lg=[("find_callers", {"typeName": "CreateTodoItemCommand", "depth": 2})],
         cg={"mode": "callers", "symbol": "CreateTodoItemCommand",
             "fallback_files": ["src/Application/TodoItems/Commands/CreateTodoItem/CreateTodoItem.cs"]},
         grep=["CreateTodoItemCommand"], read_hits=True),

    dict(id="Q05", repo="CA", favor="LG", type="hubs",
         q="¿Por dónde empiezo a entender el sistema? (tipos más centrales)",
         lg=[("stats", {}), ("hubs", {"topK": 10})],
         cg={"mode": "explore", "files": ["src/AppHost/Program.cs",
                                          "src/Infrastructure/DependencyInjection.cs"]},
         grep=[], read_files=["src/AppHost/Program.cs",
                             "src/Infrastructure/DependencyInjection.cs"]),

    # ============ DI (favorece SharpGraph) ============

    dict(id="Q06", repo="CA", favor="LG", type="di",
         q="¿Qué implementación se inyecta para IIdentityService?",
         lg=[("resolve_di", {"typeName": "IIdentityService"})],
         cg={"mode": "explore", "files": ["src/Infrastructure/DependencyInjection.cs"]},
         grep=["IIdentityService"], read_files=["src/Infrastructure/DependencyInjection.cs"]),

    # ============ comprensión de FLUJO (favorece SharpGraph: flow) ============

    dict(id="Q07", repo="CA", favor="FLOW", type="flow",
         q="¿Cómo funciona CreateTodoItemCommandHandler.Handle de principio a fin?",
         lg=[("flow", {"typeName": "CreateTodoItemCommandHandler", "member": "Handle", "depth": 3})],
         cg={"mode": "explore", "files": [
             "src/Application/TodoItems/Commands/CreateTodoItem/CreateTodoItem.cs"]},
         grep=[], read_files=[
             "src/Application/TodoItems/Commands/CreateTodoItem/CreateTodoItem.cs"]),

    dict(id="Q08", repo="CA", favor="FLOW", type="flow",
         q="¿Qué hace GetTodosQueryHandler.Handle (flujo de llamadas)?",
         lg=[("flow", {"typeName": "GetTodosQueryHandler", "member": "Handle", "depth": 3})],
         cg={"mode": "explore", "files": [
             "src/Application/TodoLists/Queries/GetTodos/GetTodos.cs"]},
         grep=[], read_files=["src/Application/TodoLists/Queries/GetTodos/GetTodos.cs"]),

    # ============ leer código (favor neutral / CG) ============

    dict(id="Q09", repo="CA", favor="CG", type="source",
         q="Enséñame el cuerpo del método Handle de CreateTodoItemCommandHandler",
         lg=[("get_source", {"typeName": "CreateTodoItemCommandHandler", "member": "Handle"})],
         cg={"mode": "explore", "files": [
             "src/Application/TodoItems/Commands/CreateTodoItem/CreateTodoItem.cs"]},
         grep=["class CreateTodoItemCommandHandler"], read_files=[
             "src/Application/TodoItems/Commands/CreateTodoItem/CreateTodoItem.cs"]),

    dict(id="Q10", repo="CA", favor="CG", type="source",
         q="Enséñame la clase CreateTodoItemCommandHandler completa (modo tipo, compite con explore)",
         lg=[("get_source", {"typeName": "CreateTodoItemCommandHandler", "maxBodyLines": 400})],
         cg={"mode": "explore", "files": [
             "src/Application/TodoItems/Commands/CreateTodoItem/CreateTodoItem.cs"]},
         grep=["class CreateTodoItemCommandHandler"], read_files=[
             "src/Application/TodoItems/Commands/CreateTodoItem/CreateTodoItem.cs"]),

    dict(id="Q10b", repo="CA", favor="CG", type="understand",
         q="Enséñame la clase CreateTodoItemCommandHandler con contexto (understand)",
         lg=[("understand", {"typeName": "CreateTodoItemCommandHandler", "bodyBudget": 250})],
         cg={"mode": "explore", "files": [
             "src/Application/TodoItems/Commands/CreateTodoItem/CreateTodoItem.cs"]},
         grep=["class CreateTodoItemCommandHandler"], read_files=[
             "src/Application/TodoItems/Commands/CreateTodoItem/CreateTodoItem.cs"]),

    # ============ búsqueda semántica ============

    dict(id="Q11", repo="CA", favor="LG", type="semantic",
         q="¿Qué tipo se ocupa de la persistencia/escritura en base de datos?",
         lg=[("search_semantic", {"query": "persistencia guardar base de datos save changes", "topK": 5})],
         cg={"mode": "query", "term": "database save"},
         grep=["SaveChanges"], read_hits=False),

    dict(id="Q12", repo="CA", favor="GREP", type="literal", kind="literal",
         q='¿Dónde se define el mensaje "Todo Lists" (string literal)?',
         lg=[("search", {"pattern": "Todo Lists"})],
         cg={"mode": "query", "term": "Todo Lists"},
         grep=["Todo Lists"], grep_fixed=True, read_hits=False),
]

# Referencia de campos: ver questions.example.py (mismo esquema).
