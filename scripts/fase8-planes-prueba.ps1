<#
.SYNOPSIS
  Crea (o deja en el estado correcto) los planes de práctica de la Fase 8 para probar el sitio público y el registro desde pantalla.
.DESCRIPTION
  Solo para desarrollo local. Es repetible: si un plan ya existe, solo corrige su visibilidad y su estado. No guarda contraseñas: las recibe por parámetro.
  Planes que deja:  inicio-prueba ($29, 14 días de prueba, público) · gratis ($0, público) · pro-ventas ($79 sin prueba, público: solo por ventas)
                    oculto ($15, activo pero NO público) · retirado ($19, fue público y se desactivó: no debe verse).
.EXAMPLE
  ./scripts/fase8-planes-prueba.ps1 -PlatformEmail admin@fitcore.local -PlatformPassword '<clave de PLATFORM_ADMIN_PASSWORD>'
#>
param(
    [Parameter(Mandatory)] [string] $PlatformEmail,
    [Parameter(Mandatory)] [string] $PlatformPassword,
    [string] $ApiUrl = 'http://localhost:8080/api/v1',
    # Por defecto se ocultan del sitio los demás planes públicos (las pruebas automáticas dejan decenas); con este interruptor se respetan.
    [switch] $ConservarOtros
)

$ErrorActionPreference = 'Stop'

function Invoke-Api([string] $Method, [string] $Path, $Body = $null, [string] $Token = $null) {
    $headers = @{}
    if ($Token) { $headers['Authorization'] = "Bearer $Token" }
    $request = @{ Method = $Method; Uri = "$ApiUrl$Path"; Headers = $headers; ContentType = 'application/json' }
    # Bytes UTF-8 explícitos: Windows PowerShell 5.1 enviaría los acentos en otra codificación.
    if ($null -ne $Body) { $request['Body'] = [Text.Encoding]::UTF8.GetBytes(($Body | ConvertTo-Json -Depth 6)) }
    Invoke-RestMethod @request
}

$token = (Invoke-Api POST '/auth/login' @{ tenantSlug = 'platform'; email = $PlatformEmail; password = $PlatformPassword }).accessToken
$existing = (Invoke-Api GET '/platform/plans?pageSize=100' -Token $token).items

function Limits([int] $branches, $users, $members) {
    @(
        @{ code = 'branches'; value = $branches },
        @{ code = 'internal_users'; value = $users },
        @{ code = 'members'; value = $members },
        @{ code = 'storage_mb'; value = 500 }
    )
}

$features = @('members', 'branches', 'cash_payments', 'attendance', 'classes', 'training', 'nutrition', 'advanced_reports', 'exports')
$plans = @(
    @{ code = 'inicio-prueba'; name = 'Inicio'; description = 'Para gimnasios que empiezan. Incluye 14 días de prueba gratis.'; price = 29; trialDays = 14; limits = (Limits 1 3 100); public = $true; active = $true },
    @{ code = 'gratis'; name = 'Gratis'; description = 'Para conocer el sistema con un solo gimnasio pequeño.'; price = 0; trialDays = 0; limits = (Limits 1 2 30); public = $true; active = $true },
    @{ code = 'pro-ventas'; name = 'Pro'; description = 'Para cadenas de gimnasios. Se contrata con el equipo comercial.'; price = 79; trialDays = 0; limits = (Limits 5 15 1000); public = $true; active = $true },
    @{ code = 'oculto'; name = 'Plan oculto'; description = 'Existe y está activo, pero no se publica en el sitio.'; price = 15; trialDays = 7; limits = (Limits 1 2 50); public = $false; active = $true },
    @{ code = 'retirado'; name = 'Plan retirado'; description = 'Estuvo publicado y se desactivó: no debe verse.'; price = 19; trialDays = 7; limits = (Limits 1 2 50); public = $true; active = $false }
)

foreach ($p in $plans) {
    $plan = $existing | Where-Object { $_.code -eq $p.code }
    if (-not $plan) {
        $plan = Invoke-Api POST '/platform/plans' @{
            code = $p.code; name = $p.name; description = $p.description; isActive = $true
            currency = 'USD'; billingPeriod = 'Monthly'; price = $p.price; trialDays = $p.trialDays
            features = $features; limits = $p.limits
        } -Token $token
        Write-Host "Plan '$($p.code)' creado."
    }
    # Primero se publica (solo un plan activo se puede publicar) y después se desactiva el que debe quedar retirado.
    if ($p.public -and -not $plan.isPublic) { Invoke-Api PATCH "/platform/plans/$($plan.id)/visibility" @{ isPublic = $true } -Token $token | Out-Null }
    if (-not $p.public -and $plan.isPublic) { Invoke-Api PATCH "/platform/plans/$($plan.id)/visibility" @{ isPublic = $false } -Token $token | Out-Null }
    if (-not $p.active -and $plan.isActive) { Invoke-Api PATCH "/platform/plans/$($plan.id)/status" @{ isActive = $false } -Token $token | Out-Null }
    if ($p.active -and -not $plan.isActive) { Invoke-Api PATCH "/platform/plans/$($plan.id)/status" @{ isActive = $true } -Token $token | Out-Null }
}

if (-not $ConservarOtros) {
    $mine = $plans | ForEach-Object { $_.code }
    $others = (Invoke-Api GET '/platform/plans?pageSize=200' -Token $token).items | Where-Object { $_.isPublic -and ($mine -notcontains $_.code) }
    foreach ($o in $others) { Invoke-Api PATCH "/platform/plans/$($o.id)/visibility" @{ isPublic = $false } -Token $token | Out-Null }
    if ($others) { Write-Host "Se ocultaron del sitio $(@($others).Count) planes de otras pruebas (siguen existiendo)." }
}

Write-Host ''
Write-Host 'Planes públicos que debe ver el sitio ahora:'
(Invoke-Api GET '/public/plans').ForEach({ "  {0,-14} {1,6} USD  prueba {2,2} días  contratable por registro: {3}" -f $_.code, $_.price, $_.trialDays, $_.selfService })
