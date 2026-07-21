<#
.SYNOPSIS
    Demo script: reproduce las queries headline del benchmark sobre un proyecto
    CleanArchitecture clonado en bench/_external/CleanArchitecture.

.DESCRIPTION
    Si el repo no está clonado, lo clona automáticamente (necesitas git).
    Después ejecuta 5 queries clave que muestran lo que LocalGraph puede hacer
    que grep + lectura no pueden. Ideal para grabar una demo de 30 segundos.

.EXAMPLE
    .\demo.ps1                        # usa publish/LocalGraph.exe (compilado)
    .\demo.ps1 -Exe C:\tools\LocalGraph.exe
#>
param([string] $Exe = (Join-Path $PSScriptRoot "publish" "LocalGraph.exe"))

$ca = Join-Path $PSScriptRoot "bench" "_external" "CleanArchitecture"
if (-not (Test-Path $ca)) {
    Write-Host "Clonando CleanArchitecture..."
    git clone --depth 1 https://github.com/JasonTaylorDev/CleanArchitecture.git $ca 2>&1 | Out-Null
}

$demo = @"
=============================
 LocalGraph Demo
=============================

 Proyecto: CleanArchitecture (110 ficheros .cs, MIT)
    1. Escanear el proyecto
    2. ¿Quién usa IApplicationDbContext? (MediatR chain)
    3. ¿Dónde se invoca SaveChangesAsync de verdad? (file:line)
    4. ¿Flujo de CreateTodoItemCommandHandler.Handle?
    5. ¿Tipos más centrales del sistema? (hubs)

"@
Write-Host $demo

# una función que llama a probe_lg.py
function Invoke-LG($tool, $argsJson) {
    python (Join-Path $PSScriptRoot "bench" "probe_lg.py") $tool $argsJson
}

Write-Host "1. scan()" -ForegroundColor Cyan
& $Exe $ca 2>&1 | Select-Object -First 1

Write-Host "`n2. find_callers(IApplicationDbContext)" -ForegroundColor Cyan
Invoke-LG "find_callers" '{"typeName":"IApplicationDbContext","depth":2}'

Write-Host "`n3. find_call_sites(IApplicationDbContext, SaveChangesAsync)" -ForegroundColor Cyan
Invoke-LG "find_call_sites" '{"typeName":"IApplicationDbContext","member":"SaveChangesAsync"}'

Write-Host "`n4. flow(CreateTodoItemCommandHandler.Handle)" -ForegroundColor Cyan
Invoke-LG "flow" '{"typeName":"CreateTodoItemCommandHandler","member":"Handle","depth":3}'

Write-Host "`n5. hubs(top 6)" -ForegroundColor Cyan
Invoke-LG "hubs" '{"topK":6}'

Write-Host "`n=============================" -ForegroundColor Cyan
Write-Host " Fin demo. 5 queries. En tu proyecto real,"
Write-Host " sustituye los nombres por los tuyos." -ForegroundColor Yellow
