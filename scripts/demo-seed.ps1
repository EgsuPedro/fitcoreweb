<#
.SYNOPSIS
  Crea un gimnasio de demostración con dos sucursales y un usuario por cada rol, para validar el sistema a mano.
.DESCRIPTION
  Solo para desarrollo local. No guarda contraseñas: las recibe por parámetro. Es repetible: si el gimnasio ya existe, no lo vuelve a crear.
.EXAMPLE
  ./scripts/demo-seed.ps1 -PlatformEmail admin@fitcore.local -PlatformPassword '<clave de PLATFORM_ADMIN_PASSWORD>' -DemoPassword '<clave nueva, 10+ caracteres con mayúscula, minúscula y número>'
#>
param(
    [Parameter(Mandatory)] [string] $PlatformEmail,
    [Parameter(Mandatory)] [string] $PlatformPassword,
    [Parameter(Mandatory)] [string] $DemoPassword,
    [string] $ApiUrl = 'http://localhost:8080/api/v1',
    [string] $Slug = 'demo',
    [string] $Domain = 'demo.fitcore.test'
)

$ErrorActionPreference = 'Stop'

function Invoke-Api([string] $Method, [string] $Path, $Body = $null, [string] $Token = $null) {
    $headers = @{}
    if ($Token) { $headers['Authorization'] = "Bearer $Token" }
    $request = @{ Method = $Method; Uri = "$ApiUrl$Path"; Headers = $headers; ContentType = 'application/json' }
    # Bytes UTF-8 explícitos: Windows PowerShell 5.1 enviaría los acentos en otra codificación.
    if ($null -ne $Body) { $request['Body'] = [Text.Encoding]::UTF8.GetBytes(($Body | ConvertTo-Json -Depth 5)) }
    Invoke-RestMethod @request
}

function Get-Token([string] $TenantSlug, [string] $Email, [string] $Password) {
    (Invoke-Api POST '/auth/login' @{ tenantSlug = $TenantSlug; email = $Email; password = $Password }).accessToken
}

$platform = Get-Token 'platform' $PlatformEmail $PlatformPassword
$existing = (Invoke-Api GET '/platform/tenants?pageSize=100' -Token $platform).items | Where-Object { $_.slug -eq $Slug }
$ownerEmail = "propietario@$Domain"

if (-not $existing) {
    Invoke-Api POST '/platform/tenants' @{
        slug = $Slug; name = 'Gimnasio Demo'; timeZone = 'America/Guayaquil'; locale = 'es-EC'
        ownerEmail = $ownerEmail; ownerFullName = 'Propietario Demo'; ownerPassword = $DemoPassword
    } -Token $platform | Out-Null
    Write-Host "Gimnasio '$Slug' creado."
} else {
    Write-Host "El gimnasio '$Slug' ya existe: solo se completan los datos que falten."
}

$owner = Get-Token $Slug $ownerEmail $DemoPassword

# Sucursales
$branches = (Invoke-Api GET '/branches?pageSize=100' -Token $owner).items
foreach ($b in @(
    @{ code = 'UIO'; name = 'Quito Norte'; sri = '001'; address = 'Av. Amazonas y Naciones Unidas' },
    @{ code = 'GYE'; name = 'Guayaquil Centro'; sri = '002'; address = 'Av. 9 de Octubre y Boyacá' })) {
    if (-not ($branches | Where-Object { $_.code -eq $b.code })) {
        Invoke-Api POST '/branches' @{
            code = $b.code; name = $b.name; timeZone = 'America/Guayaquil'
            sriEstablishmentCode = $b.sri; address = $b.address
        } -Token $owner | Out-Null
    }
}
$branches = (Invoke-Api GET '/branches?pageSize=100' -Token $owner).items
$uio = ($branches | Where-Object code -eq 'UIO').id
$gye = ($branches | Where-Object code -eq 'GYE').id

# Un usuario por rol
$roles = Invoke-Api GET '/roles' -Token $owner
$users = (Invoke-Api GET '/users?pageSize=100' -Token $owner).items
$plan = @(
    @{ user = 'admin.uio';     role = 'AdministradorSucursal'; branches = @($uio) },
    @{ user = 'recepcion.uio'; role = 'Recepcion';             branches = @($uio) },
    @{ user = 'cajero.gye';    role = 'Cajero';                branches = @($gye) },
    @{ user = 'entrenador.uio'; role = 'Entrenador';           branches = @($uio) },
    @{ user = 'nutricionista.gye'; role = 'Nutricionista';     branches = @($gye) },
    @{ user = 'contador';      role = 'Contador';              branches = @() },
    @{ user = 'alumno.uio';    role = 'Alumno';                branches = @($uio) }
)
foreach ($p in $plan) {
    $email = "$($p.user)@$Domain"
    if ($users | Where-Object { $_.email -eq $email }) { continue }
    $roleId = ($roles | Where-Object name -eq $p.role).id
    Invoke-Api POST '/users' @{
        email = $email; fullName = "$($p.role) Demo"; password = $DemoPassword
        roleIds = @($roleId); branchIds = @($p.branches)
    } -Token $owner | Out-Null
}

Write-Host ''
Write-Host "Listo. Inicie sesión con el gimnasio '$Slug' y la clave que indicó en -DemoPassword:"
Write-Host "  $ownerEmail  (Propietario, todas las sucursales)"
foreach ($p in $plan) { Write-Host ("  {0}@{1}  ({2})" -f $p.user, $Domain, $p.role) }
