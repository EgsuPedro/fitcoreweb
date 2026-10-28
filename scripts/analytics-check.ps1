<#
.SYNOPSIS
  Muestra en la consola el dashboard y los reportes de analítica de un gimnasio, para validarlos a mano sin pantallas.
.DESCRIPTION
  Solo lectura. Inicia sesión con el usuario que se indique (por defecto el propietario) y consulta los endpoints reales de /analytics y /reports.
  Cambie -Usuario para ver qué recibe cada rol (por ejemplo cajero.gye, recepcion.uio, entrenador.uio, contador, admin.uio).
.EXAMPLE
  ./scripts/analytics-check.ps1 -DemoPassword 'DemoFit2026x' -Slug revision -Domain revision.fitcore.test
  ./scripts/analytics-check.ps1 -DemoPassword 'DemoFit2026x' -Slug revision -Domain revision.fitcore.test -Usuario cajero.gye -Periodo Last7Days
#>
param(
    [Parameter(Mandatory)] [string] $DemoPassword,
    [string] $ApiUrl = 'http://localhost:8080/api/v1',
    [string] $Slug = 'demo',
    [string] $Domain = 'demo.fitcore.test',
    [string] $Usuario = 'propietario',
    [ValidateSet('Today', 'Last7Days', 'Last30Days', 'CurrentMonth', 'PreviousMonth', 'CurrentYear')] [string] $Periodo = 'Last30Days',
    # Rango personalizado (AAAA-MM-DD); si se indican ambas fechas reemplaza a -Periodo. Útil para incluir las clases de los próximos días.
    [string] $Desde = '',
    [string] $Hasta = ''
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

function Get-Api([string] $Path, [string] $Token) {
    try { Invoke-RestMethod -Method GET -Uri "$ApiUrl$Path" -Headers @{ Authorization = "Bearer $Token" } }
    catch {
        $status = [int]$_.Exception.Response.StatusCode
        [pscustomobject]@{ __error = $status; __detail = $_.ErrorDetails.Message }
    }
}

$login = Invoke-RestMethod -Method POST -Uri "$ApiUrl/auth/login" -ContentType 'application/json' `
    -Body (@{ tenantSlug = $Slug; email = "$Usuario@$Domain"; password = $DemoPassword } | ConvertTo-Json)
$token = $login.accessToken

function Show-Kpis([string] $Title, $Kpis) {
    Write-Host ''
    Write-Host "== $Title" -ForegroundColor Cyan
    foreach ($k in $Kpis) {
        $value = if ($null -eq $k.value) { 'Sin datos' } elseif ($k.unit -eq 'Currency') { ('{0:N2} USD' -f $k.value) } elseif ($k.unit -eq 'Percent') { ('{0:N1} %' -f $k.value) } else { [string]$k.value }
        $change = switch ($k.variation.state) {
            'Ok' { if ($null -ne $k.variation.percent) { ('{0:+0.0;-0.0;0.0} % vs anterior ({1})' -f $k.variation.percent, $k.previous) } else { ('{0:+0.0;-0.0;0.0} pts vs anterior' -f $k.variation.absolute) } }
            'NoBaseline' { 'Sin base de comparación' }
            'NoData' { 'Sin datos' }
            'NotApplicable' { 'medida de hoy' }
            default { '' }
        }
        $empty = if ($k.isEmpty) { ' (vacío)' } else { '' }
        Write-Host ('  {0,-34} {1,16}   {2}{3}' -f $k.name, $value, $change, $empty)
    }
}

$query = if ($Desde -and $Hasta) { "preset=Custom&from=$Desde&to=$Hasta" } else { "preset=$Periodo" }
$context = Get-Api "/analytics/context?$query" $token
if ($context.__error) { Write-Host "Sin acceso a analítica (HTTP $($context.__error)) con el usuario '$Usuario'." -ForegroundColor Yellow; return }
Write-Host ("Usuario: {0}@{1} · Gimnasio: {2} · Zona horaria: {3} · Hoy: {4}" -f $Usuario, $Domain, $Slug, $context.timeZone, $context.today)
Write-Host ("Período: {0} → {1} ({2} días) · Comparable: {3} → {4}" -f $context.period.from, $context.period.to, $context.period.days, $context.comparable.from, $context.comparable.to)
Write-Host ("Sucursales: {0}" -f (($context.branches | ForEach-Object { $_.name }) -join ', '))
Write-Host ("Áreas permitidas: " + (($context.sections.PSObject.Properties | Where-Object { $_.Value } | ForEach-Object { $_.Name }) -join ', '))

$dash = Get-Api "/analytics/dashboard?$query" $token
if ($dash.financial) { Show-Kpis 'DASHBOARD · Finanzas' $dash.financial.kpis; Write-Host ('  Medios de pago: ' + (($dash.financial.methods | ForEach-Object { '{0} {1:N2} ({2} %)' -f $_.method, $_.amount, $_.share }) -join ' · ')) }
if ($dash.members) { Show-Kpis 'DASHBOARD · Alumnos y membresías' $dash.members.kpis }
if ($dash.operations) {
    Show-Kpis 'DASHBOARD · Operación' $dash.operations.kpis
    $s = $dash.operations.sessions
    Write-Host ("  Sesiones: {0} realizadas · {1} canceladas · {2} por ocurrir · {3} pendientes de cierre" -f $s.held, $s.cancelled, $s.upcoming, $s.pendingClosure)
}
if ($dash.instructor) { Show-Kpis 'DASHBOARD · Instructor (sus sesiones)' $dash.instructor.kpis }
if ($dash.omittedSections.Count -gt 0) { Write-Host ("`n  Secciones que este usuario NO recibe: " + ($dash.omittedSections -join ', ')) -ForegroundColor DarkYellow }

$series = Get-Api "/analytics/dashboard/comparison?$query" $token
if ($series.series) {
    Write-Host "`n== EVOLUCIÓN (tramos de $($series.bucketDays) día/s; actual vs comparable)" -ForegroundColor Cyan
    foreach ($s in $series.series) {
        $now = ($s.current.values | Measure-Object -Sum).Sum
        $before = ($s.previous.values | Measure-Object -Sum).Sum
        Write-Host ('  {0,-30} total actual {1,10:N2} · total anterior {2,10:N2} · puntos: {3}' -f $s.name, $now, $before, $s.current.values.Count)
    }
}

if ($context.sections.financial) {
    $c = Get-Api "/reports/financial/collections?$query&pageSize=5" $token
    Show-Kpis 'REPORTE · Cobros' $c.summary
    Write-Host ('  Por sucursal: ' + (($c.byBranch | ForEach-Object { '{0} {1:N2}' -f $_.branchName, $_.amount }) -join ' · '))
    Write-Host ('  Por plan:     ' + (($c.byPlan | ForEach-Object { '{0} {1:N2}' -f $_.planName, $_.amount }) -join ' · '))
    Write-Host ("  Últimos pagos ({0} en total):" -f $c.payments.totalCount)
    foreach ($p in $c.payments.items) { Write-Host ('    {0} {1:yyyy-MM-dd HH:mm} {2,-16} {3,-12} {4,8:N2} {5}' -f $p.receiptNumber, $p.paidAt, $p.memberName, $p.method, $p.amount, $p.status) }

    $o = Get-Api '/reports/financial/outstanding-balances?pageSize=5' $token
    Write-Host "`n== REPORTE · Saldos pendientes (foto de hoy)" -ForegroundColor Cyan
    Write-Host ('  Pendiente {0:N2} USD en {1} obligaciones · Vencido {2:N2} USD en {3}' -f $o.outstanding, $o.openCount, $o.overdue, $o.overdueCount)
    Write-Host ('  Antigüedad: ' + (($o.aging | ForEach-Object { '{0}: {1:N2}' -f $_.label, $_.amount }) -join ' · '))
    foreach ($m in $o.members.items) { Write-Host ('    {0} {1,-18} debe {2,7:N2} (vencido {3,7:N2}) desde {4}' -f $m.memberCode, $m.memberName, $m.outstanding, $m.overdue, $m.oldestDueDate) }

    $cash = Get-Api "/reports/financial/cash-registers?$query" $token
    Write-Host "`n== REPORTE · Caja" -ForegroundColor Cyan
    Write-Host ('  Turnos: {0} abiertos en el período ({1} siguen abiertos, {2} cerrados) · Faltante {3:N2} · Sobrante {4:N2}' -f $cash.sessionsOpened, $cash.sessionsStillOpen, $cash.sessionsClosed, $cash.shortage, $cash.surplus)
    foreach ($s in $cash.sessions.items) { Write-Host ('    {0} {1,-14} apertura {2,7:N2} esperado {3,8} contado {4,8} diferencia {5,6}' -f $s.status, $s.registerName, $s.openingAmount, $s.expected, $s.counted, $s.difference) }

    $rev = Get-Api "/reports/financial/reversals?$query" $token
    Write-Host "`n== REPORTE · Reversiones y anulaciones" -ForegroundColor Cyan
    Write-Host ('  Pagos revertidos: {0} por {1:N2} USD · Obligaciones anuladas: {2} por {3:N2} USD' -f $rev.reversedCount, $rev.reversedAmount, $rev.voidedCount, $rev.voidedAmount)
    foreach ($r in $rev.reversals.items) { Write-Host ('    {0} {1,-18} {2,7:N2} "{3}" por {4}' -f $r.receiptNumber, $r.memberName, $r.amount, $r.reason, $r.reversedBy) }
}
else {
    Write-Host "`n(Este usuario no tiene reportes financieros: es lo esperado según su rol.)" -ForegroundColor DarkYellow
}
if ($context.sections.operations) {
    $a = Get-Api "/reports/attendance?$query&view=Members&pageSize=3" $token
    Show-Kpis 'REPORTE · Asistencia al gimnasio' $a.summary
    Write-Host ('  Por sucursal: ' + (($a.byBranch | ForEach-Object { '{0} {1}' -f $_.branchName, $_.visits }) -join ' · '))
    Write-Host ('  Por día de la semana: ' + (($a.byWeekday | ForEach-Object { '{0} {1}' -f $_.key.Substring(0, 3), $_.visits }) -join ' · '))
    Write-Host ('  Frecuencia (alumnos por número de visitas): ' + (($a.frequency | ForEach-Object { '{0}: {1}' -f $_.label, $_.members }) -join ' · '))
    foreach ($m in $a.members.items) { Write-Host ('    {0} {1,-20} {2} visitas en {3} días' -f $m.memberCode, $m.memberName, $m.visits, $m.days) }
}
if ($context.sections.operations -or $context.sections.instructor) {
    $c = Get-Api "/reports/classes/occupancy?$query&pageSize=5" $token
    if ($c.__error) { Write-Host "  (reporte de clases: HTTP $($c.__error))" }
    else {
        $suffix = if ($c.instructorOnly) { ' (solo sus sesiones)' } else { '' }
        Show-Kpis ('REPORTE · Ocupación de clases' + $suffix) $c.summary
        Write-Host ('  Sesiones: {0} realizadas · {1} canceladas · {2} por ocurrir · {3} pendientes de cierre' -f $c.sessions.held, $c.sessions.cancelled, $c.sessions.upcoming, $c.sessions.pendingClosure)
        Write-Host ('  Demanda por actividad: ' + (($c.byActivity | ForEach-Object { '{0}: {1} confirmados + {2} en espera ({3} %)' -f $_.name, $_.confirmed, $_.waitlisted, $_.occupancyPercent }) -join ' · '))
        Write-Host ('  Horarios más ocupados: ' + (($c.topSlots | Select-Object -First 3 | ForEach-Object { '{0} {1}:00 {2} %' -f $_.weekday.Substring(0, 3), $_.hour, $_.occupancyPercent }) -join ' · '))
        foreach ($s in $c.sessionList.items) { Write-Host ('    {0:yyyy-MM-dd HH:mm} {1,-26} {2,-14} {3}/{4} (espera {5})' -f $s.startsAt, $s.className, $s.status, $s.confirmed, $s.capacity, $s.waitlisted) }
    }
    $x = Get-Api "/reports/classes/cancellations?$query" $token
    if (-not $x.__error) {
        Show-Kpis 'REPORTE · Cancelaciones de clases' $x.summary
        foreach ($s in $x.cancelledSessions.items) { Write-Host ('    {0:yyyy-MM-dd HH:mm} {1,-26} "{2}" ({3} reservas afectadas)' -f $s.startsAt, $s.className, $s.reason, $s.reservationsAffected) }
    }
}

if ($context.sections.compare) {
    $cmp = Get-Api "/reports/branches/comparison?$query" $token
    if (-not $cmp.__error) {
        Write-Host "`n== REPORTE · Comparación de sucursales (mismo período y definiciones para todas)" -ForegroundColor Cyan
        foreach ($b in @($cmp.branches) + @($cmp.total)) {
            $line = '  {0,-34}' -f $b.branchName
            if ($b.financial) { $line += (' cobrado {0,9:N2} ({1}) · pendiente {2,8:N2}' -f $b.financial.collected, $(if ($b.financial.collectedShare -ne $null) { "$($b.financial.collectedShare) %" } else { '—' }), $b.financial.outstanding) }
            if ($b.members) { $line += (' · activos {0,3} · inscripciones {1,3} · renovaciones {2,2}' -f $b.members.activeMembers, $b.members.newEnrollments, $b.members.renewals) }
            if ($b.operations) { $line += (' · visitas {0,4} · ocupación {1}' -f $b.operations.visits, $(if ($b.operations.occupancyPercent -ne $null) { "$($b.operations.occupancyPercent) %" } else { 'Sin datos' })) }
            if (-not $b.hasActivity) { $line += '  [Sin datos]' }
            Write-Host $line
        }
        if ($cmp.omittedSections.Count -gt 0) { Write-Host ('  Áreas no incluidas por permisos: ' + ($cmp.omittedSections -join ', ')) -ForegroundColor DarkYellow }
        Write-Host ('  Meses de la evolución: ' + (($cmp.months | ForEach-Object { '{0}→{1}' -f $_.from, $_.to }) -join ' · '))
    }
}
Write-Host ''
