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
| 2 | Módulo Invoicing y configuración tributaria: emisor, perfil, establecimientos y puntos de emisión, certificado cifrado y validado, permisos, auditoría y pantalla de configuración | Pendiente |
| 3 | Receptores y origen de la venta: tipos de identificación del SRI, reglas por tipo, contrato `IInvoiceSourceProvider` implementado por Comercial, snapshot | Pendiente |
| 4 | Motor del comprobante: líneas, descuentos, impuestos desde `tax_rates`, regla de redondeo, secuenciales atómicos, clave de acceso, borrador, idempotencia | Pendiente |
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
