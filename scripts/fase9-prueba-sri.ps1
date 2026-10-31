<#
.SYNOPSIS
  Prueba REAL de facturación electrónica contra el ambiente de PRUEBAS del SRI (Fase 9, bloque 7). Solo para desarrollo local.
.DESCRIPTION
  Entra a la API como propietario de un gimnasio ya configurado (emisor, punto de emisión y certificado cargados desde la pantalla "Facturación electrónica"),
  crea ventas de práctica, las cobra y las EMITE con envío al SRI (submit). El Worker del stack las firma, las envía a celcer.sri.gob.ec y consulta su autorización;
  este script solo espera y muestra, por cada caso, el estado final y los mensajes que devolvió el SRI.

  Casos:
    A  $20,00 a nombre del propio titular (cédula del RUC): el IVA se desglosa exacto (base 17,39 + IVA 2,61).
    B  $10,00 a consumidor final: es de los precios SIN desglose exacto (base 8,69 + IVA 1,30 = 9,99; la alternativa 8,70 + 1,30 = 10,00 rompe «IVA = base × 15 %»).
       Sirve para saber qué regla acepta realmente el SRI.

  Seguridad: la contraseña se pide sin mostrarla y solo se usa para iniciar sesión; no se imprime ni se guarda. No toca el certificado: ese ya está cifrado en el servidor.
  Lo que imprime no es secreto (estados, números de comprobante, clave de acceso y mensajes del SRI); puede pegarlo en el chat.
.EXAMPLE
  ./scripts/fase9-prueba-sri.ps1 -Gym wel-mvvkmfac -Email luciano-sala1991@gmail.com
  ./scripts/fase9-prueba-sri.ps1 -Gym wel-mvvkmfac -Email luciano-sala1991@gmail.com -Casos A
  # Si el SRI devuelve «45 SECUENCIAL REGISTRADO», empiece más arriba:
  ./scripts/fase9-prueba-sri.ps1 -Gym wel-mvvkmfac -Email luciano-sala1991@gmail.com -UltimoSecuencial 1000
#>
param(
    [Parameter(Mandatory)] [string] $Gym,
    [Parameter(Mandatory)] [string] $Email,
    [string] $ApiUrl = 'http://localhost:8080/api/v1',
    [ValidateSet('A', 'B', 'AB')] [string] $Casos = 'AB',
    # Cédula, nombre y apellido del comprador del caso A. Por defecto la cédula sale del RUC del emisor (RUC de persona natural = cédula + 001).
    [string] $CompradorCedula = '',
    [string] $CompradorNombre = 'Segundo Pedro',
    [string] $CompradorApellido = 'Salazar Diaz',
    [int] $EsperaMinutos = 6,
    # Último secuencial que el SRI YA tiene registrado para el punto de emisión (de otro sistema o de pruebas anteriores). El primer comprobante llevará el siguiente.
    # El SRI devuelve con el error 45 «SECUENCIAL REGISTRADO» cualquier número que ya conozca. Solo sube: si el punto ya está más arriba, no se toca.
    [int] $UltimoSecuencial = -1
)

$ErrorActionPreference = 'Stop'

function Read-Json([string] $Text) { if ([string]::IsNullOrWhiteSpace($Text)) { $null } else { $Text | ConvertFrom-Json } }

function Invoke-Api([string] $Method, [string] $Path, $Body = $null, [string] $Token = $null, [hashtable] $Extra = @{}) {
    $headers = @{} + $Extra
    if ($Token) { $headers['Authorization'] = "Bearer $Token" }
    $request = @{ Method = $Method; Uri = "$ApiUrl$Path"; Headers = $headers; ContentType = 'application/json; charset=utf-8'; UseBasicParsing = $true }
    # Bytes UTF-8 explícitos: Windows PowerShell 5.1 enviaría los acentos en otra codificación.
    if ($null -ne $Body) { $request['Body'] = [Text.Encoding]::UTF8.GetBytes(($Body | ConvertTo-Json -Depth 8)) }
    try {
        Invoke-RestMethod @request
    }
    catch {
        $detail = ''
        if ($_.Exception.Response) {
            $reader = New-Object IO.StreamReader($_.Exception.Response.GetResponseStream())
            $problem = Read-Json $reader.ReadToEnd()
            if ($problem) {
                $detail = "$($problem.code): $($problem.title)"
                # Errores de validación por campo (por ejemplo, una cédula inválida).
                if ($problem.errors) {
                    foreach ($field in $problem.errors.PSObject.Properties) { $detail += " [$($field.Name): $(@($field.Value) -join ' ')]" }
                }
            }
        }
        throw "Falló $Method $Path -> $detail"
    }
}

# ¿Cédula ecuatoriana válida? (módulo 10)
function Test-Cedula([string] $Value) {
    if ($Value -notmatch '^\d{10}$') { return $false }
    $province = [int]$Value.Substring(0, 2)
    if ($province -lt 1 -or $province -gt 24 -or [int][string]$Value[2] -gt 5) { return $false }
    $sum = 0
    for ($i = 0; $i -lt 9; $i++) {
        $product = [int][string]$Value[$i] * $(if ($i % 2 -eq 0) { 2 } else { 1 })
        $sum += $(if ($product -gt 9) { $product - 9 } else { $product })
    }
    ((10 - ($sum % 10)) % 10) -eq [int][string]$Value[9]
}

# Cédula ecuatoriana válida (módulo 10) para el comprador de práctica cuando no se indica una.
function New-Cedula([string] $Seed) {
    $body = $Seed.Substring(0, 9)
    $sum = 0
    for ($i = 0; $i -lt 9; $i++) {
        $product = [int][string]$body[$i] * $(if ($i % 2 -eq 0) { 2 } else { 1 })
        $sum += $(if ($product -gt 9) { $product - 9 } else { $product })
    }
    $body + ((10 - ($sum % 10)) % 10)
}

Write-Host "== Prueba SRI (ambiente de pruebas) · gimnasio $Gym ==" -ForegroundColor Cyan
$secure = Read-Host "Contraseña de $Email (no se muestra)" -AsSecureString
$plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure))
try {
    $token = (Invoke-Api POST '/auth/login' @{ tenantSlug = $Gym; email = $Email; password = $plain; useCookie = $false }).accessToken
}
finally { $plain = $null }

$settings = Invoke-Api GET '/invoicing/settings' -Token $token
if (-not $settings.canIssue) {
    $missing = ($settings.checks | Where-Object { -not $_.ok -and $_.blocking } | ForEach-Object { $_.code }) -join ', '
    throw "El gimnasio todavía no está listo para emitir. Falta: $missing"
}
$ruc = $settings.issuer.ruc
Write-Host "Emisor $($settings.issuer.legalName) · RUC $ruc · ambiente $($settings.issuer.environment) · certificado vigente hasta $([datetime]$settings.certificate.notAfter | Get-Date -Format 'yyyy-MM-dd')"
if ($settings.issuer.environment -ne 'Test') { throw 'El emisor no está en ambiente de PRUEBAS: esta prueba no se ejecuta en producción.' }

if ($UltimoSecuencial -ge 0) {
    $point = $settings.emissionPoints | Where-Object { $_.isActive } | Select-Object -First 1
    if (-not $point) { throw 'No hay un punto de emisión activo.' }
    if ($point.lastSequential -lt $UltimoSecuencial) {
        $null = Invoke-Api PUT "/invoicing/emission-points/$($point.id)/sequence" @{ lastIssued = $UltimoSecuencial } -Token $token
        Write-Host "Punto $($point.establishmentCode)-$($point.pointCode): último secuencial ajustado de $($point.lastSequential) a $UltimoSecuencial; el primer comprobante será el $($UltimoSecuencial + 1)."
    }
    else {
        Write-Host "Punto $($point.establishmentCode)-$($point.pointCode): ya está en $($point.lastSequential) (no se baja)."
    }
}

$branch = (Invoke-Api GET '/branches?pageSize=100' -Token $token).items | Select-Object -First 1
if (-not $branch) { throw 'El gimnasio no tiene sucursales.' }

if (-not $CompradorCedula) { $CompradorCedula = $ruc.Substring(0, 10) }
if ($Casos -like '*A*' -and -not (Test-Cedula $CompradorCedula)) {
    throw "La cédula del comprador del caso A ($CompradorCedula) no es válida. Si el RUC del emisor es de empresa, indique una cédula con -CompradorCedula."
}
$stamp = Get-Date -Format 'HHmmss'

# Crea el alumno de práctica o, si ya existe (el script se puede repetir), reutiliza el que tiene esa cédula.
function Get-OrCreate-Member([hashtable] $Body) {
    $found = (Invoke-Api GET "/members?search=$($Body.idNumber)&pageSize=10" -Token $token).items | Where-Object { $_.idNumber -eq $Body.idNumber } | Select-Object -First 1
    if ($found) { return $found }
    Invoke-Api POST '/members' $Body -Token $token
}

function New-Sale([string] $Label, [decimal] $Amount, [string] $MemberId) {
    $obligation = Invoke-Api POST '/payment-obligations' @{ memberId = $MemberId; concept = "Membresía de prueba SRI $Label"; amount = $Amount; taxKind = 'General' } -Token $token
    $null = Invoke-Api POST '/payments' @{
        memberId = $MemberId; method = 'Card'; amount = $Amount; reference = "SRI-$Label-$stamp"; allocations = @(@{ obligationId = $obligation.id; amount = $Amount })
    } -Token $token -Extra @{ 'Idempotency-Key' = [guid]::NewGuid().ToString() }
    $obligation.id
}

function Wait-Result([string] $Label, [string] $InvoiceId) {
    $deadline = (Get-Date).AddMinutes($EsperaMinutos)
    $seen = ''
    while ((Get-Date) -lt $deadline) {
        $invoice = Invoke-Api GET "/invoicing/invoices/$InvoiceId" -Token $token
        $last = $invoice.sriAttempts | Select-Object -First 1
        $line = "$($invoice.status)" + $(if ($last) { " · último intercambio: $($last.operation) $($last.outcome)" + $(if ($last.errorCode) { " ($($last.errorCode))" }) } else { '' })
        if ($line -ne $seen) { Write-Host "   [$Label] $line"; $seen = $line }
        if ($invoice.status -in @('Authorized', 'NotAuthorized', 'Rejected', 'Voided')) { return $invoice }
        if ($invoice.transmission -and $invoice.transmission.parked) { return $invoice }
        Start-Sleep -Seconds 5
    }
    Write-Host "   [$Label] Sigue sin resolverse tras $EsperaMinutos min (el Worker sigue reintentando)." -ForegroundColor Yellow
    Invoke-Api GET "/invoicing/invoices/$InvoiceId" -Token $token
}

function Show-Invoice([string] $Label, $Invoice) {
    $color = switch ($Invoice.status) { 'Authorized' { 'Green' } 'NotAuthorized' { 'Red' } 'Rejected' { 'Red' } default { 'Yellow' } }
    Write-Host "`n--- Caso $Label ---" -ForegroundColor $color
    Write-Host "Estado:           $($Invoice.status)"
    Write-Host "Número:           $($Invoice.number)"
    Write-Host "Clave de acceso:  $($Invoice.accessKey)"
    $c = $Invoice.calculation
    Write-Host ("Importes:         base {0} · IVA {1} · total {2} · diferencia de redondeo {3}" -f $c.subtotalWithoutTaxes, $c.totalTax, $c.total, $c.roundingDifference)
    if ($Invoice.authorization) { Write-Host "Autorización:     $($Invoice.authorization.number) ($($Invoice.authorization.authorizedAt))" }
    foreach ($a in ($Invoice.sriAttempts | Sort-Object at)) {
        Write-Host ("  {0} {1} -> {2}{3} ({4} ms)" -f $a.operation, $a.sriState, $a.outcome, $(if ($a.errorCode) { " [$($a.errorCode)]" } else { '' }), $a.durationMs)
        foreach ($m in $a.messages) {
            $detail = if ($m.detail) { " :: " + $m.detail.Substring(0, [Math]::Min(300, $m.detail.Length)) } else { '' }
            Write-Host ("     {0} [{1}] {2}{3}" -f $m.id, $m.type, $m.text, $detail)
        }
    }
    if ($Invoice.transmission -and $Invoice.transmission.lastError) { Write-Host "Cola: último error $($Invoice.transmission.lastError), intentos $($Invoice.transmission.attempts)" -ForegroundColor Yellow }
}

$results = @()

if ($Casos -like '*A*') {
    $member = Get-OrCreate-Member @{
        branchId = $branch.id; firstName = $CompradorNombre; lastName = $CompradorApellido; idType = 'Cedula'; idNumber = $CompradorCedula
        birthDate = '1991-01-01'; email = $Email; phone = '0999999999'; address = 'Cotocollao, Quito'
    }
    $obligationId = New-Sale 'A' 20.00 $member.id
    $issued = Invoke-Api POST '/invoicing/invoices' @{ sourceType = 'obligation'; sourceId = $obligationId; submit = $true } -Token $token
    Write-Host "Caso A emitido (comprobante $($issued.number)); esperando al Worker y al SRI…"
    $results += , @('A  $20,00 a nombre del titular', (Wait-Result 'A' $issued.id))
}

if ($Casos -like '*B*') {
    $cedula = New-Cedula ('171' + $stamp)
    $member = Get-OrCreate-Member @{
        branchId = $branch.id; firstName = 'Consumidor'; lastName = 'Final Prueba'; idType = 'Cedula'; idNumber = $cedula; birthDate = '1990-01-01'
    }
    $obligationId = New-Sale 'B' 10.00 $member.id
    $issued = Invoke-Api POST '/invoicing/invoices' @{ sourceType = 'obligation'; sourceId = $obligationId; submit = $true; buyerIdType = 'FinalConsumer' } -Token $token
    Write-Host "Caso B emitido (comprobante $($issued.number)); esperando al Worker y al SRI…"
    $results += , @('B  $10,00 a consumidor final (sin desglose exacto)', (Wait-Result 'B' $issued.id))
}

Write-Host "`n================ RESULTADO ================" -ForegroundColor Cyan
foreach ($r in $results) { Show-Invoice $r[0] $r[1] }
Write-Host "`nCopie todo lo anterior (no contiene contraseñas ni el certificado) y péguelo en el chat." -ForegroundColor Cyan
