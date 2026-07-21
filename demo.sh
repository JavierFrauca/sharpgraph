#!/usr/bin/env bash
# Demo script: reproduce the headline queries from the benchmark on a
# CleanArchitecture clone. Clones it if not present.
# Usage: ./demo.sh [path_to_LocalGraph_executable]

EXE="${1:-./publish/LocalGraph}"
CA_DIR="./bench/_external/CleanArchitecture"

if [ ! -d "$CA_DIR" ]; then
    echo "Clonando CleanArchitecture..."
    git clone --depth 1 https://github.com/JasonTaylorDev/CleanArchitecture.git "$CA_DIR" >/dev/null 2>&1
fi

lg() {
    python bench/probe_lg.py "$1" "$2"
}

echo "====================================="
echo " LocalGraph Demo"
echo "====================================="
echo ""
echo " Proyecto: CleanArchitecture (110 .cs, MIT)"
echo ""
echo " 1) Escanear proyecto"
"$EXE" "$CA_DIR" 2>&1 | head -1
echo ""

echo " 2) ¿Quién usa IApplicationDbContext? (MediatR chain)"
lg "find_callers" '{"typeName":"IApplicationDbContext","depth":2}'
echo ""

echo " 3) ¿Dónde se invoca SaveChangesAsync? (file:line)"
lg "find_call_sites" '{"typeName":"IApplicationDbContext","member":"SaveChangesAsync"}'
echo ""

echo " 4) Flujo de CreateTodoItemCommandHandler.Handle"
lg "flow" '{"typeName":"CreateTodoItemCommandHandler","member":"Handle","depth":3}'
echo ""

echo " 5) Tipos más centrales (hubs)"
lg "hubs" '{"topK":6}'

echo ""
echo "====================================="
echo " Fin demo. Sustituye los nombres por los de tu proyecto."
