# Evidencia de la prueba real contra el SRI (Fase 9, bloque 7)

**Resultado: el ambiente de pruebas real del SRI autorizó las dos facturas emitidas por FIT360, firmadas con un certificado real.** Es la evidencia que exige la fase (emisión y autorización reales, sin simulaciones).

| | |
|---|---|
| Fecha | 2026-10-31 (hora de Ecuador: 07:44) |
| Ambiente | Pruebas del SRI (`celcer.sri.gob.ec`, servicios `RecepcionComprobantesOffline` y `AutorizacionComprobantesOffline`) |
| Emisor | RUC 1723240493001, régimen General, obligado a contabilidad, establecimiento 001, punto de emisión 001 |
| Certificado | Firma electrónica real (FirmaSegura S.A.S.), cargada desde la pantalla, guardada cifrada (AES-256-GCM) y usada por el Worker; vigente hasta 2027-08-31 |
| Cómo se emitió | `scripts/fase9-prueba-sri.ps1` → API (`POST /invoicing/invoices` con `submit`) → cola → Worker (firma XAdES-BES, `validarComprobante`, `autorizacionComprobante`) |
| Versión del XML | Factura 1.1.0 |

## Comprobantes autorizados

| | Caso A | Caso B |
|---|---|---|
| Número | 001-001-000005001 | 001-001-000005002 |
| Comprador | Cédula (tipo 05), a nombre del titular | Consumidor final (tipo 07) |
| Precio cobrado (IVA incluido) | 20,00 | 10,00 |
| Base + IVA 15 % | 17,39 + 2,61 = **20,00** | 8,69 + 1,30 = **9,99** |
| Diferencia de redondeo | 0,00 | **0,01** (caso sin desglose exacto) |
| Recepción | `RECIBIDA` (1 011 ms) | `RECIBIDA` (298 ms) |
| Autorización | `AUTORIZADO` (149 ms) | `AUTORIZADO` (169 ms) |
| Fecha de autorización | 2026-10-31 07:44:24 (-05:00) | 2026-10-31 07:44:52 (-05:00) |
| Clave de acceso = n.º de autorización | `3110202601172324049300110010010000050015415957518` | `3110202601172324049300110010010000050023935628511` |
| Historial del comprobante | Created › Queued › Signed › PendingSubmission › Received › Authorized | igual |
| Mensajes del SRI | ninguno | ninguno |

## Qué demuestra
- **XML conforme**: el SRI aceptó el XML contra su esquema (validación 1 de la ficha) y las comprobaciones del emisor (validación 2: RUC activo, establecimiento activo, autorización para emitir comprobantes electrónicos y facturas).
- **Firma XAdES-BES válida** con RSA-SHA1 y su cadena de confianza (validación 4), con el certificado real. Confirma el perfil de la ficha 2.34 tal como está implementado.
- **Clave de acceso y secuencial correctos** (validación 3): números únicos, módulo 11 y estructura de 49 dígitos.
- **Cálculos aceptados** (validación 6), incluida la **regla de redondeo**: el caso B ($10,00) no admite un desglose exacto (base 8,69 + IVA 1,30 = 9,99; la alternativa 8,70 + 1,30 = 10,00 rompería «IVA = base × 15 %»). El SRI lo autorizó con la regla del sistema, así que **la decisión abierta del bloque 4 queda resuelta con evidencia**: se mantiene la regla actual (IVA = redondeo(base × tarifa), el comprobante nunca declara más de lo cobrado).
- **Código del IVA 15 % (4)**, tipos de identificación 05 y 07 y forma de pago (tarjeta de crédito, 19) aceptados.
- **Documento autorizado íntegro**: el comprobante que el SRI devuelve dentro de su autorización es **idéntico byte a byte** al XML firmado que se envió (7 999 caracteres). El SRI devuelve el texto del comprobante con los signos escapados (`&lt;`), no en CDATA; es el mismo contenido.
- **Cola asíncrona**: firma, envío, espera y consulta de autorización las hizo el Worker sin intervención; las respuestas reales del SRI son entendidas por el analizador (`AUTORIZADO`, `RECIBIDA`, hora con `-05:00`).

## Lo que ocurrió antes (y también quedó registrado)
Una primera emisión del mismo gimnasio (001-001-000000001 y 000000002) fue **devuelta con el error 45 «SECUENCIAL REGISTRADO»**: el SRI ya tenía esos números para ese punto en su ambiente de pruebas. Es el comportamiento esperado de la ficha; llevó a agregar el control del **último secuencial usado** (API, pantalla y script) y deja esas dos facturas como «Rechazada». Ver ADR 0014, sección 15.

## Lo que esta prueba NO cubre
- **Notas de crédito** (bloque 10): su prueba real está pendiente.
- Otros tipos de comprador (RUC 04, pasaporte 06, identificación del exterior 08), una venta con **descuento**, una con **IVA 0 %** y otros regímenes: se cubren con el XSD y las pruebas automáticas, no con el SRI real.
- **«RUC Proveedor»** (Anexo 26): estas emisiones se hicieron **sin** ese campo (el RUC del proveedor no estaba configurado) y el SRI las autorizó; la obligación rige 60 días después de publicada la resolución.
- **Producción**: no se emitió nada con validez tributaria.
- Un comprobante rechazado por error de numeración o no autorizado todavía no se puede reemplazar desde el sistema (estado «descartado», pendiente).
