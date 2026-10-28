<#
.SYNOPSIS
  Pide una exportación a CSV de un reporte de analítica, la descarga y muestra las primeras filas.
.DESCRIPTION
  Solo para desarrollo local. Usa la API real: la exportación se genera en el servidor con los mismos filtros, permisos y sucursales del usuario, se
  guarda en el almacén privado (vence a las 24 horas) y se descarga con la sesión del usuario. El archivo se guarda en la carpeta indicada.
.EXAMPLE
  ./scripts/analytics-export.ps1 -DemoPassword 'DemoFit2026x' -Slug analitica -Domain analitica.fitcore.test -Reporte Collections
  ./scripts/analytics-export.ps1 -DemoPassword 'DemoFit2026x' -Slug analitica -Domain analitica.fitcore.test -Reporte OutstandingBalances -Usuario contador
#>
param(
    [Parameter(Mandatory)] [string] $DemoPassword,
    [Parameter(Mandatory)]
    [ValidateSet('Collections', 'PaymentMethods', 'OutstandingBalances', 'CashRegisters', 'Reversals', 'MemberAcquisitions', 'MemberRetention', 'MemberRenewals',
        'MemberExpirations', 'Attendance', 'ClassOccupancy', 'ClassCancellations', 'BranchComparison')] [string] $Reporte,
    [string] $ApiUrl = 'http://localhost:8080/api/v1',
    [string] $Slug = 'demo',
    [string] $Domain = 'demo.fitcore.test',
    [string] $Usuario = 'propietario',
    [ValidateSet('Today', 'Last7Days', 'Last30Days', 'CurrentMonth', 'PreviousMonth', 'CurrentYear')] [string] $Periodo = 'Last30Days',
    [string] $Carpeta = (Join-Path ([IO.Path]::GetTempPath()) 'fitcore-exportaciones')
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$login = Invoke-RestMethod -Method POST -Uri "$ApiUrl/auth/login" -ContentType 'application/json' `
    -Body (@{ tenantSlug = $Slug; email = "$Usuario@$Domain"; password = $DemoPassword } | ConvertTo-Json)
$headers = @{ Authorization = "Bearer $($login.accessToken)" }

try {
    $body = @{ report = $Reporte; preset = $Periodo } | ConvertTo-Json
    $export = Invoke-RestMethod -Method POST -Uri "$ApiUrl/reports/exports" -Headers $headers -ContentType 'application/json' -Body $body
}
catch {
    Write-Host ("No se pudo exportar: HTTP {0} {1}" -f [int]$_.Exception.Response.StatusCode, $_.ErrorDetails.Message) -ForegroundColor Yellow
    return
}

Write-Host ("Exportación {0}: {1} filas, {2} bytes, disponible hasta {3:yyyy-MM-dd HH:mm}" -f $export.id, $export.rowCount, $export.fileSize, $export.expiresAt)
New-Item -ItemType Directory -Force -Path $Carpeta | Out-Null
$path = Join-Path $Carpeta $export.fileName
Invoke-WebRequest -Uri "$ApiUrl/reports/exports/$($export.id)/download" -Headers $headers -OutFile $path
Write-Host "Archivo descargado: $path"
Write-Host ''
Get-Content -Path $path -Encoding UTF8 -TotalCount 6 | ForEach-Object { Write-Host "  $_" }
