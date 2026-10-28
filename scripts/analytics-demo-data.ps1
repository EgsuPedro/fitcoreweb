<#
.SYNOPSIS
  Carga datos de negocio de demostración (alumnos, membresías, pagos, caja, clases y asistencias) para validar los dashboards y reportes.
.DESCRIPTION
  Solo para desarrollo local. Usa la API real (nada se escribe directamente en la base, salvo los ajustes de fechas pasadas del paso opcional).
  Requiere que el gimnasio ya exista con ./scripts/demo-seed.ps1. No guarda contraseñas: las recibe por parámetro.
  Crea cada vez alumnos nuevos con el apellido «Analítica»; si ya existen, no vuelve a cargar (use -Force para repetir).
.EXAMPLE
  ./scripts/analytics-demo-data.ps1 -DemoPassword 'DemoFit2026x' -Slug revision -Domain revision.fitcore.test
#>
param(
    [Parameter(Mandatory)] [string] $DemoPassword,
    [string] $ApiUrl = 'http://localhost:8080/api/v1',
    [string] $Slug = 'demo',
    [string] $Domain = 'demo.fitcore.test',
    # Mueve en la base fechas de obligaciones y de una clase al pasado para ver saldos vencidos y clases realizadas (requiere Docker).
    [switch] $ConDatosPasados,
    [switch] $Force
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Invoke-Api([string] $Method, [string] $Path, $Body = $null, [string] $Token = $null, [string] $Key = $null) {
    $headers = @{}
    if ($Token) { $headers['Authorization'] = "Bearer $Token" }
    if ($Key) { $headers['Idempotency-Key'] = $Key }
    $request = @{ Method = $Method; Uri = "$ApiUrl$Path"; Headers = $headers; ContentType = 'application/json' }
    if ($null -ne $Body) { $request['Body'] = [Text.Encoding]::UTF8.GetBytes(($Body | ConvertTo-Json -Depth 6)) }
    try { Invoke-RestMethod @request }
    catch { throw "$Method $Path -> $($_.Exception.Message) $($_.ErrorDetails.Message)" }
}

function New-Cedula {
    $digits = @(1, 7, (Get-Random -Minimum 0 -Maximum 6)) + (1..6 | ForEach-Object { Get-Random -Minimum 0 -Maximum 10 })
    $sum = 0
    for ($i = 0; $i -lt 9; $i++) {
        $coef = if ($i % 2 -eq 0) { 2 } else { 1 }
        $product = $digits[$i] * $coef
        if ($product -gt 9) { $product -= 9 }
        $sum += $product
    }
    $check = (10 - ($sum % 10)) % 10
    (($digits + $check) -join '')
}

function Pay($Token, $MemberId, [decimal] $Amount, [string] $Method, [int] $DaysAgo = 0, $Allocations = $null) {
    $body = @{
        memberId = $MemberId; method = $Method; amount = $Amount; reference = "REF-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
        bankName = $(if ($Method -eq 'BankTransfer') { 'Banco Pichincha' } else { $null }); notes = 'Pago de demostración'
        paidAt = $(if ($DaysAgo -gt 0) { (Get-Date).ToUniversalTime().AddDays(-$DaysAgo).ToString('yyyy-MM-ddTHH:mm:ssZ') } else { $null })
        allocations = $Allocations
    }
    if ($Method -eq 'Cash') { $body['reference'] = $null }
    Invoke-Api POST '/payments' $body -Token $Token -Key ([guid]::NewGuid().ToString())
}

$login = Invoke-Api POST '/auth/login' @{ tenantSlug = $Slug; email = "propietario@$Domain"; password = $DemoPassword }
$token = $login.accessToken
$branches = (Invoke-Api GET '/branches?pageSize=100' -Token $token).items
$uio = ($branches | Where-Object code -eq 'UIO').id
$gye = ($branches | Where-Object code -eq 'GYE').id
if (-not $uio -or -not $gye) { throw "El gimnasio '$Slug' no tiene las sucursales UIO y GYE: ejecute primero ./scripts/demo-seed.ps1" }

if (-not $Force) {
    $already = (Invoke-Api GET '/members?search=Analítica&pageSize=1' -Token $token).items
    if ($already -and $already.Count -gt 0) {
        Write-Host "Ya hay datos de demostración de analítica en '$Slug'. Use -Force para cargar otro lote."
        return
    }
}

# --- Planes ---------------------------------------------------------------------------------------------------------------
$plans = (Invoke-Api GET '/membership-plans?pageSize=100' -Token $token).items
function Ensure-Plan([string] $Name, [decimal] $Price, [int] $Months) {
    $found = $plans | Where-Object { $_.name -eq $Name }
    if ($found) { return $found }
    Invoke-Api POST '/membership-plans' @{
        name = $Name; description = 'Plan de demostración'; price = $Price; durationValue = $Months; durationUnit = 'Month'
        allowedAccess = 'Sala de pesas'; restrictions = $null; commercialTerms = 'Sin devoluciones'; taxKind = 'General'
        isActive = $true; allowFreeze = $true; maxFreezeDays = 15; branchIds = @()
    } -Token $token
}
$mensual = Ensure-Plan 'Mensual' 30 1
$trimestral = Ensure-Plan 'Trimestral' 80 3

# --- Caja de Quito (los pagos en efectivo exigen una caja abierta) -----------------------------------------------------------
$register = (Invoke-Api GET "/cash-registers?branchId=$uio&pageSize=1" -Token $token).items | Select-Object -First 1
if (-not $register) { $register = Invoke-Api POST '/cash-registers' @{ branchId = $uio; name = 'Caja principal'; allowMultipleOpenSessions = $false } -Token $token }
$cashOpened = $false
try {
    Invoke-Api POST "/cash-registers/$($register.id)/open" @{ openingAmount = 50; notes = 'Apertura de demostración' } -Token $token -Key ([guid]::NewGuid().ToString()) | Out-Null
    $cashOpened = $true
} catch { Write-Host 'La caja ya tenía un turno abierto: se usa ese.' }

# --- Alumnos, membresías y pagos ------------------------------------------------------------------------------------------------
$names = @('Camila', 'Mateo', 'Valentina', 'Sebastián', 'Lucía', 'Diego', 'Daniela', 'Andrés', 'Paula', 'Javier', 'Gabriela', 'Nicolás', 'Isabella', 'Martín', 'Renata', 'Santiago')
$members = @()
$index = 0
foreach ($first in $names) {
    $branch = if ($index % 4 -eq 3) { $gye } else { $uio }
    $member = Invoke-Api POST '/members' @{
        branchId = $branch; firstName = $first; lastName = 'Analítica'; idType = 'Cedula'; idNumber = (New-Cedula)
        birthDate = '1993-06-15'; email = "$($first.ToLower())$([guid]::NewGuid().ToString('N').Substring(0, 6))@analitica.test".Replace('á','a').Replace('é','e').Replace('í','i').Replace('ó','o').Replace('ú','u')
        phone = '0991234567'
    } -Token $token
    $members += [pscustomobject]@{ Id = $member.id; Branch = $branch; Index = $index; Name = $first }
    $index++
}

$summary = [ordered]@{ pagos = 0; reversos = 0; abonos = 0; porVerificar = 0 }
foreach ($m in $members) {
    $plan = if ($m.Index % 3 -eq 2) { $trimestral } else { $mensual }
    $membership = Invoke-Api POST '/memberships' @{ memberId = $m.Id; planId = $plan.id } -Token $token
    $day = 1 + ($m.Index * 2) % 27          # reparte los pagos en los últimos 28 días para ver la evolución
    $uioMember = ($m.Branch -eq $uio)
    switch ($m.Index % 8) {
        0 { Pay $token $m.Id $plan.price $(if ($uioMember) { 'Cash' } else { 'Card' }) $day | Out-Null; $summary.pagos++ }
        1 { Pay $token $m.Id $plan.price 'Card' $day | Out-Null; $summary.pagos++ }
        2 { Pay $token $m.Id ([math]::Round($plan.price / 3, 2)) 'Card' $day | Out-Null; $summary.pagos++; $summary.abonos++ }     # abono: queda saldo
        3 { Pay $token $m.Id $plan.price 'BankTransfer' $day | Out-Null; $summary.porVerificar++ }                                  # transferencia sin verificar
        4 {
            $payment = Pay $token $m.Id $plan.price 'Card' $day
            Invoke-Api POST "/payments/$($payment.id)/reverse" @{ reason = 'Cobro duplicado en el datáfono' } -Token $token -Key ([guid]::NewGuid().ToString()) | Out-Null
            $summary.reversos++
        }
        5 { Pay $token $m.Id ([math]::Round($plan.price / 2, 2)) 'Card' $day | Out-Null; $summary.pagos++; $summary.abonos++ }
        6 { Pay $token $m.Id $plan.price 'Card' $day | Out-Null; $summary.pagos++ }
        default { }                                                                                                                   # sin pagar: debe todo
    }
}
# Una venta suelta (camiseta) pagada junto con otra cuota: un pago repartido en dos obligaciones.
$split = $members[1]
$shirt = Invoke-Api POST '/payment-obligations' @{ memberId = $split.Id; concept = 'Camiseta del gimnasio'; amount = 20; taxKind = 'General' } -Token $token
Pay $token $split.Id 20 'Card' 0 @(@{ obligationId = $shirt.id; amount = 20 }) | Out-Null
$summary.pagos++

# Entradas al gimnasio de los socios al día.
$checkins = 0
foreach ($m in $members | Where-Object { $_.Index % 8 -in 0, 1, 6 -and $_.Branch -eq $uio }) {
    try { Invoke-Api POST '/attendance/check-in' @{ branchId = $m.Branch; memberId = $m.Id } -Token $token | Out-Null; $checkins++ } catch { }
}

# --- Clases grupales ----------------------------------------------------------------------------------------------------------------
$trainer = (Invoke-Api GET '/trainers?pageSize=100' -Token $token).items | Select-Object -First 1
$sessionsCreated = 0
if ($trainer) {
    $class = Invoke-Api POST '/group-classes' @{
        branchId = $uio; name = "Funcional demo $(Get-Date -Format 'HHmmss')"; activity = 'Yoga'; instructorId = $trainer.id; durationMinutes = 60; capacity = 3
        level = 'AllLevels'; minBookingNoticeMinutes = 0; maxBookingAdvanceDays = 30; cancellationDeadlineMinutes = 0
        waitlistEnabled = $true; waitlistCapacity = 5; offerWindowMinutes = 30; requireActiveMembership = $false; applyPaymentPolicy = $false
    } -Token $token
    $uioMembers = @($members | Where-Object { $_.Branch -eq $uio })
    $sessions = @()
    foreach ($offset in 1, 2, 3, 4) {
        # Una hora libre del instructor (si otra carga ya ocupó esa hora, se prueba otra).
        $created = $null
        foreach ($hour in (Get-Random -InputObject (11..23) -Count 8)) {
            $start = (Get-Date).ToUniversalTime().Date.AddDays($offset).AddHours($hour)
            try { $created = Invoke-Api POST '/class-sessions' @{ classId = $class.id; startsAt = $start.ToString('yyyy-MM-ddTHH:mm:ssZ') } -Token $token; break } catch { }
        }
        if (-not $created) { throw 'No se encontró una hora libre del instructor para programar la sesión.' }
        $sessions += $created
        $sessionsCreated++
    }
    # Sesión 1: llena (3) + 2 en espera, y una cancelación. Sesión 2: 2 reservas. Sesión 3 se cancela con una reserva.
    for ($i = 0; $i -lt 5; $i++) {
        Invoke-Api POST '/class-reservations' @{ sessionId = $sessions[0].id; memberId = $uioMembers[$i].Id; joinWaitlist = $true } -Token $token -Key ([guid]::NewGuid().ToString()) | Out-Null
    }
    $cancelMe = Invoke-Api POST '/class-reservations' @{ sessionId = $sessions[1].id; memberId = $uioMembers[0].Id; joinWaitlist = $false } -Token $token -Key ([guid]::NewGuid().ToString())
    Invoke-Api POST '/class-reservations' @{ sessionId = $sessions[1].id; memberId = $uioMembers[1].Id; joinWaitlist = $false } -Token $token -Key ([guid]::NewGuid().ToString()) | Out-Null
    Invoke-Api POST "/class-reservations/$($cancelMe.id)/cancel" @{ reason = 'Cambio de planes' } -Token $token | Out-Null
    Invoke-Api POST '/class-reservations' @{ sessionId = $sessions[2].id; memberId = $uioMembers[2].Id; joinWaitlist = $false } -Token $token -Key ([guid]::NewGuid().ToString()) | Out-Null
    Invoke-Api POST "/class-sessions/$($sessions[2].id)/cancel" @{ reason = 'Mantenimiento de la sala' } -Token $token | Out-Null
}

# --- Cierre de caja con diferencia (faltan 5 dólares) -------------------------------------------------------------------------------
if ($cashOpened) {
    $session = Invoke-Api GET "/cash-registers/$($register.id)/sessions?status=Open&pageSize=1" -Token $token
    $current = $session.items | Select-Object -First 1
    if ($current) {
        Invoke-Api POST "/cash-registers/$($register.id)/close" @{ sessionId = $current.id; countedAmount = ($current.expectedAmount - 5); notes = 'Faltaron 5 dólares en el arqueo' } -Token $token -Key ([guid]::NewGuid().ToString()) | Out-Null
    }
}

# --- Opcional: fechas pasadas (saldos vencidos y una clase realizada) ----------------------------------------------------------------------
if ($ConDatosPasados) {
    $env = @{}
    Get-Content (Join-Path $PSScriptRoot '..\infra\compose\.env') | ForEach-Object { $p = $_ -split '=', 2; if ($p.Count -eq 2) { $env[$p[0]] = $p[1] } }
    $user = $(if ($env['POSTGRES_USER']) { $env['POSTGRES_USER'] } else { 'fitcore' })
    $db = $(if ($env['POSTGRES_DB']) { $env['POSTGRES_DB'] } else { 'fitcore' })
    $sql = @"
UPDATE billing.payment_obligations SET issue_date = current_date - 70, due_date = current_date - 50
 WHERE id IN (SELECT o.id FROM billing.payment_obligations o JOIN members.members m ON m.id = o.member_id AND m.tenant_id = o.tenant_id
              WHERE o.tenant_id = (SELECT id FROM organizations.tenants WHERE slug = '$Slug') AND m.last_name = 'Analítica' AND o.status IN ('Pending','PartiallyPaid')
              ORDER BY o.reference LIMIT 2);
UPDATE billing.payment_obligations SET issue_date = current_date - 25, due_date = current_date - 10
 WHERE id IN (SELECT o.id FROM billing.payment_obligations o JOIN members.members m ON m.id = o.member_id AND m.tenant_id = o.tenant_id
              WHERE o.tenant_id = (SELECT id FROM organizations.tenants WHERE slug = '$Slug') AND m.last_name = 'Analítica' AND o.status IN ('Pending','PartiallyPaid') AND o.due_date > current_date
              ORDER BY o.reference LIMIT 2);
"@
    docker compose -f (Join-Path $PSScriptRoot '..\infra\compose\docker-compose.dev.yml') exec -T postgres psql -U $user -d $db -v ON_ERROR_STOP=1 -c $sql | Out-Null
    if ($trainer -and $sessions.Count -gt 3) {
        # La sesión 4 se reserva con dos alumnos, se mueve al pasado, se registra su asistencia y se finaliza: queda como clase realizada.
        $past = $sessions[3]
        $people = @($members | Where-Object { $_.Branch -eq $uio })
        $reservations = @()
        foreach ($person in $people[3], $people[4]) {
            $reservations += Invoke-Api POST '/class-reservations' @{ sessionId = $past.id; memberId = $person.Id; joinWaitlist = $false } -Token $token -Key ([guid]::NewGuid().ToString())
        }
        $hoursAgo = Get-Random -Minimum 5 -Maximum 22
        $sqlSession = "UPDATE classes.class_sessions SET starts_at = now() - interval '$hoursAgo hours', ends_at = now() - interval '$($hoursAgo - 1) hours' WHERE id = '$($past.id)';"
        docker compose -f (Join-Path $PSScriptRoot '..\infra\compose\docker-compose.dev.yml') exec -T postgres psql -U $user -d $db -v ON_ERROR_STOP=1 -c $sqlSession | Out-Null
        if ($LASTEXITCODE -eq 0) {
            Invoke-Api PUT "/class-sessions/$($past.id)/attendance" @{ entries = @(
                @{ reservationId = $reservations[0].id; outcome = 'Attended'; notes = $null },
                @{ reservationId = $reservations[1].id; outcome = 'NoShow'; notes = $null }) } -Token $token | Out-Null
            Invoke-Api POST "/class-sessions/$($past.id)/complete" @{ markPendingAsNoShow = $false } -Token $token | Out-Null
        }
    }
}

Write-Host ''
Write-Host "Listo. Datos de analítica cargados en '$Slug':"
Write-Host ("  Alumnos: {0} · pagos confirmados: {1} · abonos: {2} · por verificar: {3} · revertidos: {4}" -f $members.Count, $summary.pagos, $summary.abonos, $summary.porVerificar, $summary.reversos)
Write-Host ("  Entradas al gimnasio: {0} · sesiones de clase creadas: {1}" -f $checkins, $sessionsCreated)
Write-Host '  Ahora ejecute ./scripts/analytics-check.ps1 para ver los indicadores y reportes.'
