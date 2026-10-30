# ADR 0014 — Facturación electrónica del SRI (Ecuador)

Estado: **propuesto el 2026-10-30 (bloque 1: diagnóstico y plan técnico); pendiente de la validación del usuario y de los datos para el ambiente de pruebas del SRI.** Corresponde a la «FASE 8» del prompt del usuario (nuestra numeración: **Fase 9**). Concreta lo adelantado en el [ADR 0003](0003-ecuador-usd-sri.md) (integración directa al SRI, XML firmado con certificado por gimnasio, detrás de un puerto).

> **Regla de cierre del prompt:** la fase **no se da por terminada** sin evidencia de una emisión y una autorización **reales** en el ambiente de pruebas del SRI. Todo lo demás se construye y se prueba localmente, pero ninguna factura se marcará «Autorizada» sin la respuesta real del SRI.

## 1. Diagnóstico del repositorio (qué hay, qué falta)
| Pieza | Estado hoy | Qué se hace |
|---|---|---|
| Facturación previa | **No existe.** `PaymentObligation` («lo que el alumno debe», `OB-000001`) y `Payment` (recibo interno `RC-000001`) se declaran explícitamente «no tributarios». | Se **reutilizan** como origen de la factura; no se duplican. |
| Origen de la venta | `PaymentObligation`: un concepto, importe con IVA incluido, `Subtotal`, `TaxAmount`, `TaxKind` (General/Zero/NotSubject/Exempt) y `TaxPercent` **congelada**; descuento a nivel de obligación; vínculo opcional a la membresía. Una obligación = **una línea**. | La factura toma una **copia (snapshot)** del origen. Hay que decidir cómo repartir descuentos y líneas (ver §5, riesgo de redondeo). |
| Pagos y formas de pago | `Payment` con medio (`Cash`, `BankTransfer`, `Card`, `Other`), asignaciones a obligaciones y estado (confirmado, revertido). Caja por sucursal. | Se mapea el medio a los códigos de forma de pago del SRI (configurable; `Card` no distingue crédito de débito). |
| Productos e inventario | **No hay** inventario ni punto de venta (pendiente de otra fase). Solo membresías, planes y promociones. | La factura cubre membresías, inscripciones, renovaciones y conceptos existentes. «Venta de productos» queda para cuando exista inventario; se deja el modelo de líneas preparado. |
| Clientes | `Member`: tipo (`Cedula`, `Ruc`, `Passport`, `Other`), número normalizado, nombres, correo, teléfono, dirección. `IdentificationValidator` valida cédula y RUC (módulo 10/11). | Se reutiliza. Faltan los tipos del SRI **consumidor final** y **identificación del exterior**, y las reglas de campos obligatorios por tipo (ver bloque 3). |
| IVA | Tabla global `tax_rates` con vigencia (IVA 15 % confirmado por el usuario el 2026-10-27). Cada obligación congela porcentaje y tipo. | Se reutiliza; el **código de porcentaje del SRI** se resuelve desde la tarifa (no se codifica en ningún lado). |
| Emisor y establecimientos | `Branch.SriEstablishmentCode` (3 dígitos, opcional). **No hay** RUC, razón social, punto de emisión, régimen, certificado, ni secuenciales. | Módulo nuevo con la configuración tributaria (bloque 2). |
| Logo | Logo por gimnasio en el almacén privado (`IFileStore`). | Se usa en el RIDE. |
| Correo | `IEmailSender`/cola con reintentos ya reales (Fase 8). **No admite adjuntos.** | Se amplía con adjuntos (XML y RIDE) sin romper lo existente. |
| PDF | **No hay** biblioteca de PDF (solo ClosedXML para Excel). | Elegir una (ver §6, decisión pendiente por la licencia). |
| XML y firma | **No hay** nada de XML tributario, XSD ni firma. | Se construye: generación, validación contra el XSD oficial y firma **XAdES-BES**. |
| Procesos en segundo plano | Worker con servicios periódicos y cola con `FOR UPDATE SKIP LOCKED`. | Se reutiliza el patrón para transmitir y consultar autorizaciones sin bloquear la API. |
| Permisos y auditoría | Catálogo `modulo.accion`, auditoría con RLS por gimnasio, `SecurityAudit`. | Permisos nuevos y auditoría de operaciones críticas. |
| Secretos | `Jwt:SigningKey` y SMTP por configuración; no hay almacén de secretos por gimnasio. | Cifrado en reposo del certificado y su contraseña (ver §4). |
| Arquitectura | Monolito modular; las pruebas de arquitectura **prohíben** referencias entre módulos. | Módulo `FitCore.Invoicing` propio; lo que necesita de Comercial llega por un **contrato en BuildingBlocks** (como ya se hizo con planes y suscripciones). |

## 2. Qué dice el SRI (verificado y por verificar)
Verificado en la página oficial de facturación electrónica del SRI (consulta del 2026-10-30): la **ficha técnica de comprobantes electrónicos, esquema offline, vigente es la versión 2.34 (actualizada a julio de 2026)**; la guía de la 2.26 indica los servicios web `RecepcionComprobantesOffline` y `AutorizacionComprobantesOffline` en `celcer.sri.gob.ec` (pruebas) y `cel.sri.gob.ec` (producción); en el esquema offline la **clave de acceso es el número de autorización** y los estados de la autorización son en procesamiento, autorizado y no autorizado.

**Por verificar en la ficha 2.34 y los XSD antes de programar (bloques 4 y 5):** versión exacta de cada XSD (factura, nota de crédito), campos obligatorios actuales (incluidos los del régimen RIMPE y los de agente de retención / contribuyente especial), tabla de códigos de impuesto y porcentaje de IVA (en particular el código del 15 %), códigos de forma de pago, tipos de identificación del comprador, algoritmo exacto de la clave de acceso (49 dígitos con dígito verificador módulo 11), perfil de la firma XAdES-BES (algoritmos de resumen y de firma aceptados) y direcciones de los servicios. **Ningún valor se tomará de memoria:** se leen de la ficha y de los XSD oficiales y quedan citados en el código y en este ADR.

## 3. Arquitectura propuesta
```
Comercial (obligación/pago) ──ISalesSource (BuildingBlocks)──▶  FitCore.Invoicing  ──SOAP──▶  SRI (pruebas / producción)
                                                    │  configuración tributaria · receptores · motor de comprobante
                                                    │  clave de acceso · XML+XSD · firma XAdES-BES · RIDE (PDF) · notas de crédito
                                                    └─ Worker: transmite, consulta autorización y reintenta (cola con SKIP LOCKED)
```
- **Módulo propio** `FitCore.Invoicing` con esquema `invoicing`, su `DbContext`, migraciones y RLS forzada por gimnasio (como Commercial y Subscriptions).
- **Puerto `ISriGateway`** (recepción, autorización) con una implementación real SOAP sobre `HttpClient` (sin WCF); intercambiable por un proveedor intermedio si algún día conviniera. No bloquea la API: la emisión crea el comprobante y el Worker lo transmite.
- **Contrato `IInvoiceSourceProvider`** (BuildingBlocks) que Comercial implementa: entrega, para una obligación o un pago, las líneas, descuentos, impuestos congelados, comprador y formas de pago. Invoicing guarda un **snapshot** (el comprobante nunca cambia aunque cambie el alumno o la tarifa).
- **Estados** (los del prompt, con los nombres del SRI donde existen): Borrador → PendienteFirma → Firmada → PendienteEnvío → Recibida → EnProcesamiento → **Autorizada** | NoAutorizada | Rechazada (devuelta); Anulada solo como **registro** de una anulación hecha por el procedimiento oficial. Transiciones validadas en el dominio; historial inmutable de eventos.
- **Idempotencia:** un índice único por (gimnasio, origen, tipo de comprobante) impide dos facturas de la misma obligación; la clave de acceso es única; el secuencial se asigna con una sola sentencia atómica por (gimnasio, establecimiento, punto de emisión, tipo).
- **Integridad:** el XML original y el firmado se guardan inmutables (sin UPDATE ni DELETE para el rol de la aplicación, como la auditoría); nada de borrado físico.
- **Notas de crédito** referencian la factura autorizada original; se firman, transmiten y autorizan igual.
- **Anulaciones:** según el SRI la anulación de un comprobante electrónico es un trámite del emisor en el portal del SRI, no un servicio web; el sistema **registra y da seguimiento** (con el motivo y quién lo pidió) pero **no puede anular por sí mismo**: lo documentaremos así.

## 4. Seguridad
- Certificado `.p12` y su contraseña **cifrados en reposo** con AES-256-GCM y cifrado por sobre (clave de datos aleatoria por certificado, protegida por una clave maestra `Invoicing:MasterKey` fuera de la base y fuera de git; con identificador de versión para poder rotarla). Nunca en texto plano, nunca en el frontend, registros ni respuestas; la API solo devuelve titular, emisor, vigencia y huella.
- La contraseña del certificado se pide una vez al cargarlo; se valida (abre el `.p12`, comprueba vigencia y que contenga clave privada) y se guarda cifrada.
- Permisos propios y auditoría de: configurar, cargar/quitar certificado, emitir, reintentar, descargar XML/RIDE, reenviar, nota de crédito, registrar anulación.
- Los XML y RIDE contienen datos personales: descarga con permiso, auditada y sin enlaces públicos.
- Las pruebas **no** usan certificados ni datos reales de producción.

## 5. Riesgos técnicos conocidos (a resolver con evidencia)
1. **Redondeo del IVA:** hoy el precio incluye IVA y el impuesto es el *residuo*; el SRI valida que `IVA = round(base × tarifa, 2)`. Con ciertos totales no hay una base que cumpla exactamente. Habrá que decidir la regla (p. ej. buscar la base de centavos vecina que cuadre, o ajustar la línea) y **probarla contra el SRI de pruebas**.
2. **Firma XAdES-BES:** no hay biblioteca .NET de confianza evidente; se implementará sobre `System.Security.Cryptography.Xml` con las propiedades XAdES del perfil del SRI. **Solo el SRI de pruebas valida de verdad** la firma: por eso la prueba real va temprano.
3. **Consumidor final:** el SRI limita el monto para «consumidor final» y exige datos de comprador en facturas mayores; se parametriza (no se codifica).
4. **Forma de pago con tarjeta:** el medio `Card` no distingue crédito de débito; se pedirá o se configurará.
5. **Disponibilidad del SRI:** el servicio cae a veces; la emisión debe quedar en cola y reintentarse sin duplicar.
6. **Régimen del gimnasio:** si es RIMPE «negocio popular» no emite factura electrónica sino nota de venta; el sistema debe impedir emitir si el perfil tributario no corresponde.
7. **Conservación:** los comprobantes deben conservarse el tiempo que exija la norma (a verificar con el contador); no se purgan.

## 6. Decisiones que necesito del usuario
| # | Decisión | Mi recomendación |
|---|---|---|
| 1 | **Datos para el ambiente de pruebas del SRI:** certificado de firma (.p12) con su contraseña, RUC, razón social, régimen, obligado a contabilidad, códigos de establecimiento y punto de emisión (y que el RUC esté habilitado para facturación electrónica en el SRI). | Sin esto se construye todo, pero **no se puede cerrar la fase**. Idealmente un RUC y certificado reservados para pruebas. |
| 2 | **Biblioteca de PDF** para el RIDE: PDFsharp/MigraDoc (MIT, sin restricciones) frente a QuestPDF (muy cómoda, pero su licencia gratuita tiene condiciones por ingresos). | **PDFsharp (MIT)** + **QRCoder** (MIT) para el código QR. |
| 3 | **Cuándo se factura:** botón «Facturar» por obligación/pago y, opcionalmente, automático al confirmar el pago (activable por gimnasio). | Empezar con el botón y la opción automática apagada por defecto. |
| 4 | **Nombre de permisos.** El prompt propone `billing.*`, pero en este sistema «billing» ya significa cobros de suscripción (`platform.billing.*`). | `invoicing.read`, `invoicing.issue`, `invoicing.send`, `invoicing.void`, `invoicing.credit-note`, `invoicing.configure`, `invoicing.download`, `invoicing.resend-email`. |
| 5 | **Productos:** no hay inventario; ¿se factura solo lo existente (membresías y conceptos) por ahora? | Sí; las ventas de productos llegan con la fase de inventario. |

## 7. Bloques de implementación
| # | Bloque | Estado |
|---|---|---|
| 1 | Diagnóstico y este ADR | Hecho (a la espera de validación) |
| 2 | Módulo Invoicing y configuración tributaria: emisor, perfil, establecimientos y puntos de emisión, certificado cifrado y validado, permisos, auditoría y pantalla de configuración | **Hecho** |
| 3 | Receptores y origen de la venta: tipos de identificación del SRI, reglas por tipo, contrato `IInvoiceSourceProvider` implementado por Comercial, snapshot | **Hecho** (códigos a contrastar con la ficha 2.34, ver §10) |
| 4 | Motor del comprobante: líneas, descuentos, impuestos desde `tax_rates`, regla de redondeo, secuenciales atómicos, clave de acceso, borrador, idempotencia | **Hecho** (códigos a contrastar con la 2.34 y el SRI de pruebas, ver §11) |
| 5 | XML conforme al XSD oficial, validación y firma XAdES-BES con verificación local | Pendiente |
| 6 | Cliente SOAP del SRI y procesamiento asíncrono en el Worker (recepción, autorización, estados, reintentos, indisponibilidad, mensajes) | Pendiente |
| 7 | **Prueba real en el ambiente de pruebas del SRI** con el certificado del usuario: emisión y autorización reales, evidencia | Pendiente (**depende de la decisión 1**) |
| 8 | RIDE en PDF (logo, QR, autorización) y correo con XML y RIDE adjuntos | Pendiente |
| 9 | Pantallas: panel de facturación, listado, detalle (XML, respuesta del SRI, historial, RIDE, reenvío) y emisión desde un cobro | Pendiente |
| 10 | Notas de crédito y registro de anulaciones, con su prueba real en el SRI de pruebas | Pendiente |
| 11 | Pruebas finales, seguridad, documentación e informe con la evidencia | Pendiente |

## 8. Archivos que se crearán o modificarán (estimado)
**Nuevos:** `src/backend/Modules/Invoicing/FitCore.Invoicing/*` (dominio, aplicación, persistencia, migraciones, cliente del SRI, XML, firma, RIDE), `BuildingBlocks/FitCore.Application.Core/InvoiceSource.cs` (contrato), `FitCore.Api/Endpoints/Invoicing*.cs`, pruebas `tests/FitCore.Invoicing.UnitTests` y de integración, `src/web/src/app/features/invoicing/*`, esquemas XSD oficiales versionados, `docs/informe-fase-9.md`.
**Modificados:** `Commercial` (implementa el contrato, sin cambiar su comportamiento), `IEmailSender`/cola (adjuntos), Worker (servicio de transmisión), catálogo de permisos y plantillas de rol, menú y rutas de Angular, compose y `.env` (clave maestra, ambiente del SRI), `README`/hoja de ruta.

## 9. Bloque 2 — implementación (módulo Invoicing y configuración tributaria)
- **Módulo** `FitCore.Invoicing` (esquema `invoicing`, historial propio `invoicing.__ef_migrations_history`, migración `InitialInvoicing`). No referencia a otros módulos (lo vigila el test de arquitectura); lo que necesite de Comercial llegará por contratos en BuildingBlocks (bloque 3).
- **Tablas** (todas con `tenant_id`, RLS forzada y auditoría): `issuer_profiles` (una por gimnasio: RUC, razón social, nombre comercial, matriz, régimen, obligado a contabilidad, contribuyente especial, agente de retención, ambiente, tipo de emisión, correo), `emission_points` (establecimiento + punto + dirección + sucursal opcional; únicos por gimnasio) y `signing_certificates` (uno activo por gimnasio).
- **Reglas**: el RUC se valida con el mismo algoritmo que Comercial (movido a `FitCore.SharedKernel.EcuadorIds`); los códigos son de 3 dígitos distintos de 000; el régimen RIMPE Negocio Popular no puede emitir factura electrónica (emite nota de venta); pasar a **Producción** exige confirmación explícita; si el punto de emisión se liga a una sucursal con código de establecimiento, deben coincidir.
- **Certificado**: se valida al cargarlo (contraseña, clave privada, RSA, uso para firmar, vigencia) y se guarda **cifrado con AES-256-GCM** usando la clave maestra `Invoicing:MasterKey` (variable `INVOICING_MASTER_KEY`, 32 bytes en base64, fuera de la base y de git; el contexto autenticado liga cada secreto a su gimnasio). La contraseña también se cifra. Nada de esto se devuelve por la API, se audita ni se registra en logs (el interceptor de auditoría lo redacta). Al retirar un certificado se borran el archivo y la contraseña (un CHECK de la base lo exige). Sin clave maestra, cargar un certificado responde 503 con un mensaje claro y la lista de verificación lo marca.
- **Verificación de preparación** (`IssuerReadiness`): bloqueantes (emisor, RUC, régimen, punto activo, clave maestra, certificado cargado y vigente) y avisos (correo, vencimiento en 30 días, certificado de otro RUC, ambiente de pruebas). Solo se puede emitir si pasan todas las bloqueantes; el servidor lo vuelve a comprobar al emitir (bloques 4 y siguientes).
- **Permisos** `invoicing.read|issue|send|void|credit-note|configure|download|resend-email`. Propietario y contador: todos; administrador de sucursal y cajero: leer, emitir, descargar y reenviar por correo; recepción: ninguno. Ver la [matriz](../permissions-matrix.md).
- **API**: `GET /invoicing/settings`, `PUT /invoicing/issuer`, `POST|PUT /invoicing/emission-points[/{id}]`, `PATCH …/{id}/status`, `PUT|DELETE /invoicing/certificate` (multipart, máximo 512 KB).
- **Pantalla** «Facturación electrónica» (`/invoicing/settings`, es/en): estado de la facturación, datos del emisor, puntos de emisión y certificado; la contraseña se limpia de la pantalla tras enviarla.
- **Pruebas**: 23 unitarias (cifrado, lector de certificados, códigos), arquitectura (31), integración de la API (permisos, RLS entre gimnasios, auditoría sin secretos, errores), 2 de Angular (validador de RUC) y 2 e2e que generan un .p12 de prueba con openssl (autofirmado, sin datos reales).
- **Pendiente para el bloque 7**: el certificado, RUC y datos reales de **pruebas del SRI** del usuario (decisión 1), cargados desde esta pantalla, nunca por chat ni en el repositorio.

## 10. Bloque 3 — implementación (receptores y origen de la venta)
**Fuentes leídas.** Del sitio del SRI se descargó y se leyó el texto de la *Ficha técnica, esquema offline* en dos versiones antiguas: **2.09 (sept. 2017)** y **2.20 (dic. 2020)**. La **2.34** (julio 2026), que es la vigente, **no se pudo descargar desde este entorno** (el servidor del SRI redirige al portal en línea). Por eso lo que sigue está citado de la **2.20** y queda **pendiente de contrastar con la 2.34** (y con el XSD) antes del bloque 7; la prueba real en el ambiente de pruebas del SRI es la validación definitiva. *Acción para el responsable:* guardar el PDF de la 2.34 en `docs/referencias-sri/` (descarga manual desde la página de facturación electrónica del SRI) para verificarlo contra este documento.

**Tabla 6 de la ficha (tipo de identificación del comprador), versión 2.20:** 04 RUC · 05 cédula · 06 pasaporte · 07 venta a consumidor final · 08 identificación del exterior. Reglas citadas: consumidor final se identifica con **13 nueves** (9999999999999); la identificación del exterior es la que otorga la administración tributaria del país de residencia fiscal; las notas de crédito, débito y retenciones **deben** identificar al receptor con RUC, cédula, pasaporte o identificación del exterior (**no** consumidor final). La razón social «CONSUMIDOR FINAL» aparece en los ejemplos oficiales del XML.

**Lo que NO está en la ficha y por eso es parámetro:** el monto máximo de la venta a consumidor final lo fija la normativa tributaria, no la ficha; se configura en `Invoicing:FinalConsumerMaxAmount` (USD con impuestos, por defecto 50,00) y **debe confirmarlo el contador del gimnasio**. Los máximos de longitud del XML se verificarán con el XSD oficial en el bloque 5; mientras tanto: razón social y dirección ≤ 300, identificación entre 3 y 20 caracteres (pasaporte y exterior).

**Contrato** `IInvoiceSourceProvider` (BuildingBlocks/Application.Core): `LoadAsync(sourceType, sourceId)` devuelve un `InvoiceSource` con comprador, líneas (importes con IVA incluido, base e impuesto congelados con la tarifa de la fecha de la obligación), pagos **confirmados** y estado (abierta, pagada, anulada). Respeta el alcance de sucursales de quien consulta (fuera de su alcance, `null`). Hoy lo implementa Comercial (`CommercialInvoiceSourceProvider`) para `obligation`; Invoicing no referencia Comercial (lo comprueba el test de arquitectura).

**Reglas del comprador** (`BuyerRules`, dominio de Invoicing): el documento registrado se traduce a su tipo del SRI (RUC→04, cédula→05, pasaporte→06); «Otro» no tiene equivalente y obliga a elegir (por ejemplo 08). Se puede elegir el tipo al emitir; el número se valida contra el tipo elegido (RUC y cédula con los módulos oficiales). Consumidor final **nunca es el valor por defecto** y solo se admite hasta el límite; no lleva datos personales. Impiden emitir: tipo sin definir, número inválido, nombre vacío o de más de 300, dirección de más de 300, origen sin cobrar o anulado, importe no positivo, moneda distinta de USD y emisor sin configurar. Solo avisan: falta de correo (no se podrá enviar el RIDE), correo inválido y falta de dirección.

**Snapshot.** `InvoiceBuyerSnapshot` es la copia que se congelará en el comprobante (bloque 4); no se vuelve a leer al alumno. Aquí queda definido y probado, pero todavía no se persiste porque aún no existe el comprobante.

**API:** `GET /invoicing/sources/obligations/{id}/preview?buyerIdType=` (permiso `invoicing.issue`): vista previa de lo que se facturaría, con el comprador resuelto, líneas, pagos, `issues` (bloqueantes o avisos) y `canIssue`. No guarda nada ni toca al SRI. La pantalla que la usa (emitir desde un cobro) es el bloque 9.

**Pruebas:** 26 unitarias nuevas de las reglas del comprador (49 en el proyecto) y 9 de integración (contrato Comercial→Facturación con datos reales, transferencias sin verificar, anuladas, límite de consumidor final, permisos, alcance de sucursal y aislamiento entre gimnasios).

## 11. Bloque 4 — implementación (motor del comprobante)
**Qué se leyó y qué no.** De la ficha técnica 2.20 (PDF oficial) se leyeron: la estructura de la **clave de acceso de 49 dígitos** (Tabla 1), el **módulo 11** con su ejemplo numérico (41261533 → 6, usado como vector de prueba), tipo de emisión (Tabla 2), tipos de comprobante (Tabla 3: 01 factura, 04 nota de crédito), ambiente (Tabla 4: 1 pruebas, 2 producción), código de impuesto IVA = 2 (Tabla 16), tarifas de IVA hasta 14 % (Tabla 17), formas de pago (Tabla 24) y la regla de que todo valor lleva como máximo 2 decimales (precio unitario y cantidad hasta 6). **No se pudo leer** la ficha vigente (2.34): el SRI redirige al portal al descargarla desde este entorno. El **IVA 15 % → código 4** no consta en la 2.20; consta en la Tabla 17 de versiones posteriores según el índice oficial del SRI y la reseña de la actualización 2.26, y **debe confirmarse con el PDF vigente y con el SRI de pruebas (bloque 7)**. El sistema nunca adivina un código: una tarifa o combinación desconocida impide emitir (`tax_rate_unsupported`).

**El comprobante nace con su número.** El «borrador» es la vista previa (sin guardar nada); emitir crea el comprobante en estado *pendiente de firma* con número `EEE-PPP-NNNNNNNNN` y clave de acceso ya asignados, en **una sola transacción**: (1) incremento atómico del secuencial (`INSERT … ON CONFLICT DO UPDATE … RETURNING` sobre `emission_sequences`, una fila por gimnasio + punto de emisión + tipo + ambiente), (2) clave de acceso con código numérico aleatorio de 8 dígitos, (3) comprobante, líneas, impuestos, formas de pago y evento «Created». Si algo falla, el número no se consume; quien llega segundo espera el candado de la fila, así que **no hay huecos ni repetidos** (probado con 10 emisiones y con 8 solicitudes simultáneas de la misma venta). Pasar a producción reinicia la numeración: los números de prueba no contaminan la serie real.

**Idempotencia.** Un índice único `(gimnasio, tipo de origen, origen, tipo de comprobante)` impide facturar dos veces una venta. Emitir de nuevo devuelve el mismo comprobante (200 en lugar de 201); si dos solicitudes compiten, una gana, la otra recibe el ganador y su número reservado se deshace con la transacción.

**Cálculo (la regla de redondeo, riesgo n.º 1 del ADR).** El gimnasio cobra con IVA incluido; el SRI quiere base, descuento y precio sin impuestos y valida que el IVA sea la tarifa aplicada a la base (error 52 «error en diferencias» si los cálculos no cuadran; la ficha no publica la tolerancia). `InvoiceCalculator.SplitGross` busca la base de centavos tal que **IVA = redondeo(base × tarifa)** y base + IVA = cobrado. Medido recorriendo todos los precios de 0,01 a 2.000,00 con 15 %: **173.913 se desglosan exactamente y 26.087 (13,04 %) no admiten ninguna base exacta** (al subir la base un centavo, el total sube uno o dos). En ese 13 % el comprobante suma **un centavo menos** de lo cobrado (nunca más: no se declara una venta mayor a la recibida), la diferencia queda en `rounding_difference` y las formas de pago se ajustan para sumar el total del comprobante. **Decisión abierta:** no sabemos si el SRI acepta la alternativa «IVA = cobrado − base» (que cuadra el total pero rompe IVA = base × tarifa); en el bloque 7 se emitirá un comprobante exacto y uno de esos casos para decidir con evidencia. Descuento: `precio unitario × cantidad − descuento = base de la línea`, siempre exacto. Solo se factura una unidad por línea (hoy una obligación = una línea).

**Formas de pago.** Efectivo → 01; transferencia y «otro» → 20; tarjeta → 19 (crédito) o 16 (débito) según la **tarjeta habitual que elija el gimnasio** en su configuración (el módulo de pagos no distingue). La correspondencia código ↔ nombre se tomó del orden de la Tabla 24 (el texto extraído del PDF desordena las columnas): verificar con la versión vigente.

**Integridad en la base (migración `AddInvoices`).** `invoices`, `invoice_lines`, `invoice_taxes`, `invoice_payments`, `invoice_events` y `emission_sequences`, todas con RLS forzada. Restricciones: estados, ambiente, totales coherentes (total = base + IVA, > 0), consumidor final con identificación fija y, sobre todo, **la clave de acceso debe coincidir con el propio registro** (fecha, tipo, RUC, ambiente, establecimiento, punto, secuencial y código numérico). Un disparador solo deja cambiar el estado del comprobante (código FC130); sus detalles y eventos no se editan ni se borran, y el rol de la aplicación no tiene DELETE.

**Snapshot.** El comprobante copia emisor, comprador y punto de emisión: si el alumno cambia después sus datos, el comprobante no cambia (probado).

**API.** `POST /invoicing/invoices` (permiso `invoicing.issue`), `GET /invoicing/invoices` (listado con estado, número, comprador o clave; paginado, más nuevo primero) y `GET /invoicing/invoices/{id}` (`invoicing.read`); la vista previa ahora incluye el cálculo, el punto de emisión elegido y si la venta ya está facturada. **Punto de emisión:** el pedido o, si no, el activo de la sucursal de la venta y, si no hay, uno activo sin sucursal; nunca el de otra sucursal. Todo respeta el alcance de sucursales.

**Pruebas:** 91 unitarias en el proyecto de facturación (clave de acceso con el vector oficial, recorrido exhaustivo del redondeo, descuentos, códigos, errores) y 20 de integración nuevas (emisión, idempotencia, concurrencia, secuenciales, ambientes, puntos de emisión, comprador, inmutabilidad en la base, permisos, alcance, aislamiento, listado, auditoría).
