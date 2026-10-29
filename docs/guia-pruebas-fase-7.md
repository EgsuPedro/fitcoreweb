# Guía de validación manual — Fase 7 (administración SaaS y suscripciones)

Permite revisar a mano, pantalla por pantalla y con datos reales, todo lo que entrega la fase. Resultado esperado de cada paso entre «→». Informe y límites: [informe-fase-7.md](informe-fase-7.md).

## 0. Preparación
1. Entorno levantado con el código actual (API, Worker y Web reconstruidos):
   ```powershell
   docker compose -f infra/compose/docker-compose.dev.yml --env-file infra/compose/.env up --build -d
   ```
2. Dos sesiones en dos navegadores (o una ventana privada):
   - **Plataforma:** gimnasio `platform`, correo y clave del administrador de plataforma (`PLATFORM_ADMIN_EMAIL` / `PLATFORM_ADMIN_PASSWORD` de `infra/compose/.env`).
   - **Gimnasio:** el propietario del gimnasio de prueba que creará en el paso 2.
3. Los gimnasios que ya existían antes de la fase quedaron con el plan **Plan heredado** (`legacy`): todas las funciones, sin límites y sin cobro. No se rompió nada; verifíquelo entrando como propietario de cualquiera de ellos: todo funciona igual y «Mi suscripción» muestra ese plan.

## 1. Planes comerciales (plataforma → «Planes comerciales»)
1. Verá `legacy` (inactivo, no se vende). **Nuevo plan**: código `pro`, nombre «Pro», mensual, **30 USD**, 10 días de prueba, todas las funciones, límites: sucursales **2**, usuarios *(vacío)*, alumnos **5**. → aparece con versión 1.
2. **Nuevo plan**: código `basico`, «Básico», mensual, **10 USD**, 0 días de prueba, solo `members`, `branches` y `cash_payments`, sucursales **1**, alumnos **3**.
3. Edite «Pro» y cambie el precio a 35: → aparece la **versión 2**; la 1 sigue ahí. Vuelva a 30 si quiere conservar las cifras de esta guía.

## 2. Alta de un gimnasio con plan y prueba (plataforma → «Gimnasios»)
1. **Nuevo gimnasio**: identificador `suscrip`, plan **Pro**, marque «Empezar con la prueba gratuita del plan», dueño con su correo y clave. → el gimnasio aparece; «Ver organización» lleva a su ficha.
2. En la ficha: contrato **En prueba**, termina en 10 días; «Derechos y consumo» con sucursales 0/2, alumnos 0/5 (medir con «Medir ahora»); modo de acceso **Completo**.

## 3. Límites que se hacen cumplir (gimnasio, como propietario)
1. Cree 2 sucursales. → la tercera se rechaza con el mensaje del plan (límite 2) y qué hacer.
2. Cree 5 alumnos. → el sexto se rechaza con el mismo tipo de mensaje.
3. Pruebe un alta simultánea abriendo dos pestañas y enviando a la vez el último cupo: solo una pasa (también está probado con 8 en paralelo).
4. «Mi suscripción» → **Uso del plan**: las barras muestran 2/2 y 5/5, con aviso de límite alcanzado.

## 4. Cobros comerciales (plataforma → ficha de la organización)
1. **Emitir factura**: → documento `FC-AAAA-NNNNNN` por 30 USD, «Comprobante interno de cobro. No es un comprobante tributario».
2. En «Cobros comerciales → Facturas» aparece Pendiente. **Registrar pago**: transferencia, importe **29** → se rechaza (el importe debe ser exacto). Con 30 y una referencia → la factura queda **Pagada** y la suscripción **Activa** con su primer período de pago.
3. Repita el pago con la misma referencia en otra factura → se rechaza (referencia duplicada). Escriba un número de tarjeta (por ejemplo `4111 1111 1111 1111`) en la referencia → se rechaza: no se guardan datos de tarjeta.
4. **Reembolsar** 5 USD en la pestaña «Pagos» → la factura queda «Reembolso parcial» y la suscripción **no** se cancela.
5. **Registrar intento fallido** en otra factura abierta → queda «Con intento fallido», sigue abierta y **no** activa nada.

## 5. Vencimientos, gracia y modo restringido
La forma más rápida de ver el modo restringido es la suspensión comercial; el vencimiento real se prueba con el proceso de vencimientos.
1. Ficha de la organización → **Suspender (comercial)** con motivo. → estado Suspendida, modo **Restringido**.
2. Como propietario del gimnasio: la sesión **sigue abierta**; aparece el banner rojo; puede consultar y exportar; **cobrar a un alumno, operar la caja y registrar asistencia siguen funcionando**; crear una sucursal, un alumno o un usuario → se rechaza con «Su suscripción está restringida…»; una clase nueva → se rechaza.
3. **Reactivar** → vuelve a Activa y todo se habilita. (La **suspensión administrativa** del gimnasio, en «Gimnasios», es distinta: esa sí cierra el acceso.)
4. Para el vencimiento real: deje correr el Worker (cada 15 minutos) o pulse **Ejecutar vencimientos ahora** en «Cobros comerciales». Un período vencido sin pago pasa a **En mora** (todo sigue funcionando, con banner amarillo y fecha de fin de gracia) y, pasada la gracia (7 días), a **Vencida** (restringido).

## 6. Cambio de plan y cancelación (plataforma)
1. **Cambiar de plan** a «Básico» (más barato) → se **programa** para el fin del período (sigue en Pro hasta entonces). A «Pro» (más caro) → entra **de inmediato**, sin cobrar la diferencia.
2. Con el consumo por encima de los límites nuevos nada se borra: solo se bloquean las altas hasta bajar.
3. **Cancelar** al final del período → sigue activa hasta esa fecha, con «Cancelación programada»; **Retirar cancelación** la anula.

## 7. Portal «Mi suscripción» (gimnasio)
1. Menú **Mi suscripción** (propietario y administrador). **Resumen:** plan, precio, período, próximo vencimiento, factura por pagar, modo de acceso.
2. **Planes:** solo los vendibles, con «Es una mejora / Es una baja». **Pedir este plan** → «Solicitud enviada». El contrato **no** cambia.
3. **Solicitudes:** queda Pendiente. Puede **Retirar**; pedir otra igual → se rechaza (ya hay una pendiente).
4. **Pedir la cancelación** exige el motivo; **Pedir la reactivación** solo aparece si está suspendida, vencida, cancelada o con cancelación programada.
5. **Plataforma → «Solicitudes de gimnasios»:** aparecen las pendientes. **Aprobar** ejecuta la acción (con las mismas reglas de arriba); **Rechazar** exige una razón. → en el portal del gimnasio, «Solicitudes» muestra Aprobada o Rechazada con la razón.
6. **Avisos:** tras suspender, emitir facturas o pasar a mora, y ejecutar vencimientos, aparecen en la pestaña **Avisos** (y el contador). «Marcar como leído» funciona; no hay correos ni SMS. Ejecutar vencimientos otra vez **no duplica** los avisos.
7. Pruebe con un empleado sin el permiso `subscription.read` (por ejemplo, recepción): no ve el menú ni el banner, y la API le responde 403.

## 8. Panel global (plataforma)
1. **Organizaciones:** lista con plan, estado, vencimiento y consumo (con la fecha de medición); filtros por estado, plan y vencimiento. Nunca aparece el gimnasio `platform`.
2. Ficha: **Notas** (máximo 1000 caracteres, un número de tarjeta se rechaza; el gimnasio no las ve), **Historial** de la suscripción y **Acciones administrativas** (quién hizo qué y cuándo).
3. **Métricas SaaS:** organizaciones, pruebas, mora, facturas vencidas, **MRR** (contratado por mes) y **ARR**, cobrado / reembolsado / neto / pendiente, conversión de pruebas. La fórmula del MRR está escrita en pantalla: contratado ≠ cobrado.

## 9. Proveedor de pagos y webhooks
1. «Cobros comerciales → **Proveedor de pagos**»: sin secreto configurado dice **Sin proveedor** y los webhooks responden 503.
2. Para probar el verificador defina un secreto (mínimo 16 caracteres) en `infra/compose/.env` — `PAYMENT_WEBHOOK_SECRET=…` — y reinicie la API: `docker compose -f infra/compose/docker-compose.dev.yml --env-file infra/compose/.env up -d api`. La pantalla pasa a **Configurado** (nunca muestra el secreto).
3. Envíe un evento firmado (cambie `FC-…` por el número de una factura abierta de 30 USD y use su secreto):
   ```powershell
   $secret = 'SU-SECRETO-DE-16-O-MAS'
   $body = '{"id":"evt_prueba_1","type":"payment.succeeded","occurredAt":"' + (Get-Date).ToUniversalTime().ToString('o') + '","data":{"invoiceNumber":"FC-2026-000001","providerReference":"ch_prueba_1","amount":30,"currency":"USD"}}'
   $t = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
   $hmac = [System.Security.Cryptography.HMACSHA256]::new([Text.Encoding]::UTF8.GetBytes($secret))
   $sig = ($hmac.ComputeHash([Text.Encoding]::UTF8.GetBytes("$t.$body")) | ForEach-Object { $_.ToString('x2') }) -join ''
   Invoke-RestMethod -Method Post -Uri http://localhost:8080/api/v1/webhooks/payments -ContentType 'application/json' -Body $body -Headers @{ 'X-FitCore-Signature' = "t=$t,v1=$sig" }
   ```
   → `outcome: Applied`; la factura queda Pagada con medio **Tarjeta** y el proveedor; la suscripción, activa.
4. Repita **exactamente** el mismo comando → `duplicate: true`, no se cobra otra vez.
5. Cambie un dígito de la firma, o el importe del cuerpo sin volver a firmar, o use una marca de tiempo de hace una hora → **401** idéntico en todos los casos.
6. Firme un evento con importe 29 sobre una factura abierta de 30 → 200 con `outcome: Rejected`; la factura sigue abierta. Aparece en «Proveedor de pagos → eventos» como **Rechazado** con su motivo («El importe o la moneda no coinciden»).
7. Un `payment.failed` deja la factura «Con intento fallido» **sin activar** la suscripción.
8. Retire el secreto de `.env` y reinicie la API cuando termine: vuelve a **Sin proveedor**.

## 10. Verificaciones transversales
- **Pagos de alumnos intactos:** cobre a un alumno antes y después de un ciclo comercial completo (pasos 4 a 6): sus pagos, obligaciones y membresías no cambian, y cobrar a un alumno no altera ninguna factura comercial.
- **Aislamiento:** con dos gimnasios, el propietario de uno nunca ve facturas, avisos ni solicitudes del otro; la plataforma ve conteos, no datos de alumnos.
- **Historial de precios:** suba el precio de «Pro» (paso 1.3); el gimnasio ya contratado, su factura emitida y su pago conservan el precio de su momento.
