<#
.SYNOPSIS
  Agrega historial de membresías a un gimnasio que ya tiene los datos de analytics-demo-data.ps1, para ver retención, renovaciones y vencimientos.
.DESCRIPTION
  Solo para desarrollo local (requiere Docker). Toma 5 membresías activas y ya cobradas del lote «Analítica» y mueve sus fechas en la base:
    - 3 vencieron hace 5 días (la primera se renueva por la API y la segunda queda sin renovar),
    - 1 vence en 5 días y 1 vence en 20 días (próximos vencimientos).
  Es repetible: solo toma membresías que todavía no se han movido.
.EXAMPLE
  ./scripts/analytics-demo-history.ps1 -DemoPassword 'DemoFit2026x' -Slug analitica -Domain analitica.fitcore.test
#>
param(
    [Parameter(Mandatory)] [string] $DemoPassword,
    [string] $ApiUrl = 'http://localhost:8080/api/v1',
    [string] $Slug = 'demo',
    [string] $Domain = 'demo.fitcore.test'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Invoke-Api([string] $Method, [string] $Path, $Body = $null, [string] $Token = $null) {
    $headers = @{}
    if ($Token) { $headers['Authorization'] = "Bearer $Token" }
    $request = @{ Method = $Method; Uri = "$ApiUrl$Path"; Headers = $headers; ContentType = 'application/json' }
    if ($null -ne $Body) { $request['Body'] = [Text.Encoding]::UTF8.GetBytes(($Body | ConvertTo-Json -Depth 5)) }
    try { Invoke-RestMethod @request }
    catch { throw "$Method $Path -> $($_.Exception.Message) $($_.ErrorDetails.Message)" }
}

$compose = Join-Path $PSScriptRoot '..\infra\compose\docker-compose.dev.yml'
$envFile = @{}
Get-Content (Join-Path $PSScriptRoot '..\infra\compose\.env') | ForEach-Object { $p = $_ -split '=', 2; if ($p.Count -eq 2) { $envFile[$p[0]] = $p[1] } }
$user = $(if ($envFile['POSTGRES_USER']) { $envFile['POSTGRES_USER'] } else { 'fitcore' })
$db = $(if ($envFile['POSTGRES_DB']) { $envFile['POSTGRES_DB'] } else { 'fitcore' })
function Invoke-Sql([string] $Sql) {
    $out = docker compose -f $compose exec -T postgres psql -U $user -d $db -v ON_ERROR_STOP=1 -t -A -c $Sql
    if ($LASTEXITCODE -ne 0) { throw "Falló el SQL: $Sql" }
    $out
}

$today = (Get-Date).ToString('yyyy-MM-dd')
$tenant = "(SELECT id FROM organizations.tenants WHERE slug = '$Slug')"
$picked = @"
SELECT m.id FROM members.memberships m
JOIN billing.payment_obligations o ON o.membership_id = m.id AND o.tenant_id = m.tenant_id
JOIN members.members x ON x.id = m.member_id AND x.tenant_id = m.tenant_id
WHERE m.tenant_id = $tenant AND x.last_name = 'Analítica' AND o.status = 'Paid' AND m.status = 'Active' AND m.start_date >= '$today'::date - 1
ORDER BY m.created_at, m.id LIMIT 5
"@
$ids = @(Invoke-Sql $picked | Where-Object { $_ })
if ($ids.Count -lt 5) { Write-Host "Solo hay $($ids.Count) membresías disponibles para mover (se necesitan 5). ¿Ya ejecutó este script?"; return }

$ends = @(-5, -5, -5, 5, 20)
for ($i = 0; $i -lt 5; $i++) {
    $start = if ($i -lt 3) { -35 } else { -25 }
    Invoke-Sql "UPDATE members.memberships SET start_date = '$today'::date + ($start), end_date = '$today'::date + ($($ends[$i])), created_at = now() - interval '35 days' WHERE id = '$($ids[$i])'" | Out-Null
    Invoke-Sql "UPDATE members.membership_events SET occurred_at = now() - interval '35 days' WHERE membership_id = '$($ids[$i])' AND type IN ('Created','Activated')" | Out-Null
}

$login = Invoke-Api POST '/auth/login' @{ tenantSlug = $Slug; email = "propietario@$Domain"; password = $DemoPassword }
Invoke-Api POST "/memberships/$($ids[0])/renew" @{} -Token $login.accessToken | Out-Null

Write-Host 'Listo: 3 membresías vencidas (1 renovada, 2 sin renovar), 1 que vence en 5 días y 1 que vence en 20 días.'
Write-Host 'Ejecute ./scripts/analytics-check.ps1 y consulte /reports/members/retention, /renewals y /expirations.'
