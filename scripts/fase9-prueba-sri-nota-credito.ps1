<#
.SYNOPSIS
  Prueba REAL de NOTAS DE CRÉDITO contra el ambiente de PRUEBAS del SRI (Fase 9, bloque 10). Solo para desarrollo local.
.DESCRIPTION
  Entra a la API como propietario de un gimnasio ya configurado y EMITE notas de crédito, con envío al SRI (submit), sobre una factura que el SRI YA AUTORIZÓ en el ambiente de pruebas
  (por ejemplo las de la prueba del bloque 7). El Worker del stack las firma, las envía a celcer.sri.gob.ec y consulta su autorización; este script solo espera y muestra, por cada caso,
  el estado final y los mensajes que devolvió el SRI.

  Casos:
    Parcial  nota de crédito por una parte de la factura (importe -MontoParcial, con IVA incluido): ejercita el desglose de un importe parcial en base + IVA.
    Resto    nota de crédito por todo lo que quede de la factura: ejercita la base exacta y que las notas sumen la factura.

  Seguridad: la contraseña se pide sin mostrarla y solo se usa para iniciar sesión; no se imprime ni se guarda. No toca el certificado. Lo que imprime no es secreto (estados, números de
  comprobante, clave de acceso y mensajes del SRI); puede pegarlo en el chat. Solo corre si el emisor está en ambiente de PRUEBAS.
.EXAMPLE
  ./scripts/fase9-prueba-sri-nota-credito.ps1 -Gym wel-mvvkmfac -Email luciano-sala1991@gmail.com -Factura 001-001-000005001
  # Si el SRI devuelve «45 SECUENCIAL REGISTRADO» (ya tiene notas de crédito de ese punto), empiece más arriba:
  ./scripts/fase9-prueba-sri-nota-credito.ps1 -Gym wel-mvvkmfac -Email luciano-sala1991@gmail.com -Factura 001-001-000005001 -UltimaNotaCredito 1000
#>
param(
    [Parameter(Mandatory)] [string] $Gym,
    [Parameter(Mandatory)] [string] $Email,
    # Número de la factura AUTORIZADA que se va a acreditar (EEE-PPP-NNNNNNNNN).
    [Parameter(Mandatory)] [string] $Factura,
    [string] $ApiUrl = 'http://localhost:8080/api/v1',
    [ValidateSet('Parcial', 'Resto', 'Ambas')] [string] $Casos = 'Ambas',
    # Importe (con IVA incluido) de la nota parcial. Debe ser menor que lo que queda de la factura.
    [decimal] $MontoParcial = 5.00,
    [int] $EsperaMinutos = 6,
    # Última nota de crédito que el SRI YA tiene registrada para el punto de emisión (las notas llevan numeración propia, distinta de la de las facturas). Solo sube.
    [int] $UltimaNotaCredito = -1
)

$ErrorActionPreference = 'Stop'

function Read-Json([string] $Text) { if ([string]::IsNullOrWhiteSpace($Text)) { $null } else { $Text | ConvertFrom-Json } }

function Invoke-Api([string] $Method, [string] $Path, $Body = $null, [string] $Token = $null) {
    $headers = @{}
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
                if ($problem.errors) {
                    foreach ($field in $problem.errors.PSObject.Properties) { $detail += " [$($field.Name): $(@($field.Value) -join ' ')]" }
                }
            }
        }
        throw "Falló $Method $Path -> $detail"
    }
}

Write-Host "== Prueba de notas de crédito en el SRI (ambiente de pruebas) · gimnasio $Gym ==" -ForegroundColor Cyan
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
Write-Host "Emisor $($settings.issuer.legalName) · RUC $($settings.issuer.ruc) · ambiente $($settings.issuer.environment)"
if ($settings.issuer.environment -ne 'Test') { throw 'El emisor no está en ambiente de PRUEBAS: esta prueba no se ejecuta en producción.' }

if ($UltimaNotaCredito -ge 0) {
    $point = $settings.emissionPoints | Where-Object { $_.isActive } | Select-Object -First 1
    if (-not $point) { throw 'No hay un punto de emisión activo.' }
    if ($point.lastCreditNoteSequential -lt $UltimaNotaCredito) {
        $null = Invoke-Api PUT "/invoicing/emission-points/$($point.id)/sequence" @{ lastIssued = $UltimaNotaCredito; documentType = 'CreditNote' } -Token $token
        Write-Host "Punto $($point.establishmentCode)-$($point.pointCode): última nota de crédito ajustada de $($point.lastCreditNoteSequential) a $UltimaNotaCredito; la primera será la $($UltimaNotaCredito + 1)."
    }
    else {
        Write-Host "Punto $($point.establishmentCode)-$($point.pointCode): las notas de crédito ya están en $($point.lastCreditNoteSequential) (no se baja)."
    }
}

$found = (Invoke-Api GET "/invoicing/invoices?q=$Factura&documentType=Invoice&pageSize=5" -Token $token).items | Where-Object { $_.number -eq $Factura } | Select-Object -First 1
if (-not $found) { throw "No se encontró la factura $Factura en este gimnasio." }
$invoice = Invoke-Api GET "/invoicing/invoices/$($found.id)" -Token $token
if ($invoice.status -ne 'Authorized') { throw "La factura $Factura está en estado $($invoice.status): solo se acredita una factura AUTORIZADA por el SRI." }
if ($invoice.buyer.isFinalConsumer) { throw "La factura $Factura fue a consumidor final: el SRI exige identificar al receptor de una nota de crédito. Use otra factura, o emita la nota desde la pantalla indicando al receptor." }

$preview = Invoke-Api GET "/invoicing/invoices/$($invoice.id)/credit-preview" -Token $token
if (-not $preview.canIssue) { throw "No se puede emitir una nota de crédito sobre esta factura: $(($preview.issues | ForEach-Object { $_.code }) -join ', ')" }
Write-Host "Factura $Factura · $($invoice.buyer.legalName) · total $($preview.total) · ya acreditado $($preview.creditedTotal) · por acreditar $($preview.remainingTotal)"

function Wait-Result([string] $Label, [string] $NoteId) {
    $deadline = (Get-Date).AddMinutes($EsperaMinutos)
    $seen = ''
    while ((Get-Date) -lt $deadline) {
        $note = Invoke-Api GET "/invoicing/invoices/$NoteId" -Token $token
        $last = $note.sriAttempts | Select-Object -First 1
        $line = "$($note.status)" + $(if ($last) { " · último intercambio: $($last.operation) $($last.outcome)" + $(if ($last.errorCode) { " ($($last.errorCode))" }) } else { '' })
        if ($line -ne $seen) { Write-Host "   [$Label] $line"; $seen = $line }
        if ($note.status -in @('Authorized', 'NotAuthorized', 'Rejected', 'Voided')) { return $note }
        if ($note.transmission -and $note.transmission.parked) { return $note }
        Start-Sleep -Seconds 5
    }
    Write-Host "   [$Label] Sigue sin resolverse tras $EsperaMinutos min (el Worker sigue reintentando)." -ForegroundColor Yellow
    Invoke-Api GET "/invoicing/invoices/$NoteId" -Token $token
}

function Show-Note([string] $Label, $Note) {
    $color = switch ($Note.status) { 'Authorized' { 'Green' } 'NotAuthorized' { 'Red' } 'Rejected' { 'Red' } default { 'Yellow' } }
    Write-Host "`n--- Nota de crédito $Label ---" -ForegroundColor $color
    Write-Host "Estado:           $($Note.status)"
    Write-Host "Número:           $($Note.number)  (modifica la factura $($Note.creditNote.modifiedNumber))"
    Write-Host "Clave de acceso:  $($Note.accessKey)"
    $c = $Note.calculation
    Write-Host ("Importes:         base {0} · IVA {1} · total {2}" -f $c.subtotalWithoutTaxes, $c.totalTax, $c.total)
    if ($Note.authorization) { Write-Host "Autorización:     $($Note.authorization.number) ($($Note.authorization.authorizedAt))" }
    foreach ($a in ($Note.sriAttempts | Sort-Object at)) {
        Write-Host ("  {0} {1} -> {2}{3} ({4} ms)" -f $a.operation, $a.sriState, $a.outcome, $(if ($a.errorCode) { " [$($a.errorCode)]" } else { '' }), $a.durationMs)
        foreach ($m in $a.messages) {
            $detail = if ($m.detail) { " :: " + $m.detail.Substring(0, [Math]::Min(300, $m.detail.Length)) } else { '' }
            Write-Host ("     {0} [{1}] {2}{3}" -f $m.id, $m.type, $m.text, $detail)
        }
    }
    if ($Note.status -eq 'Authorized') {
        $file = Join-Path ([IO.Path]::GetTempPath()) "NotaCredito-$($Note.number).pdf"
        Invoke-WebRequest -Uri "$ApiUrl/invoicing/invoices/$($Note.id)/ride" -Headers @{ Authorization = "Bearer $token" } -OutFile $file -UseBasicParsing
        Write-Host "RIDE (PDF):       $file"
    }
}

$results = @()

if ($Casos -in @('Parcial', 'Ambas')) {
    if ($MontoParcial -ge $preview.remainingTotal) { throw "-MontoParcial ($MontoParcial) debe ser menor que lo que queda por acreditar ($($preview.remainingTotal))." }
    $lineId = $preview.lines[0].lineId
    $note = Invoke-Api POST '/invoicing/credit-notes' @{
        invoiceId = $invoice.id; reason = 'Devolución parcial de la membresía de prueba'; lines = @(@{ lineId = $lineId; amount = $MontoParcial }); submit = $true
    } -Token $token
    Write-Host "Nota parcial emitida ($($note.number)); esperando al Worker y al SRI…"
    $results += , @("parcial por $MontoParcial", (Wait-Result 'Parcial' $note.id))
}

if ($Casos -in @('Resto', 'Ambas')) {
    $note = Invoke-Api POST '/invoicing/credit-notes' @{ invoiceId = $invoice.id; reason = 'Devolución del resto de la membresía de prueba'; submit = $true } -Token $token
    Write-Host "Nota por el resto emitida ($($note.number)); esperando al Worker y al SRI…"
    $results += , @('por el resto de la factura', (Wait-Result 'Resto' $note.id))
}

Write-Host "`n================ RESULTADO ================" -ForegroundColor Cyan
foreach ($r in $results) { Show-Note $r[0] $r[1] }
Write-Host "`nCopie todo lo anterior (no contiene contraseñas ni el certificado) y péguelo en el chat." -ForegroundColor Cyan
