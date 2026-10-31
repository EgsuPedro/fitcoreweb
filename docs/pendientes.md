# Pendientes (lista viva)

Se agrega aquí lo que queda abierto mientras se avanza; se revisa al cerrar cada fase. Marcar con `[x]` y fecha cuando se resuelva.

## Fase 9 — Facturación electrónica

- [ ] **Prueba real de notas de crédito en el SRI de pruebas** (la ejecuta el usuario con `scripts/fase9-prueba-sri-nota-credito.ps1`; sin ella la fase 9 no se puede dar por terminada para notas de crédito).
- [ ] Notas de crédito: el RIDE y las pantallas se probaron con el SRI simulado; confirmar con la autorización real que el SRI acepta el desglose parcial (base + IVA) y el redondeo.
- [ ] Nota de crédito por «valor» sin línea (ajuste libre) y notas de crédito sobre facturas anteriores a este sistema: hoy solo se acredita por línea de una factura emitida aquí.
- [ ] Notas de débito y anulación de comprobantes (estado `Voided` sin uso hoy).
- [ ] «RUC Proveedor» (Anexo 26): el usuario lo definirá más adelante (`INVOICING_PROVIDER_RUC`).
- [ ] RIMPE Negocio Popular: hoy no se puede emitir (el usuario es régimen General).
- [ ] Más pruebas reales en el SRI de pruebas: comprador con RUC y con pasaporte, descuento, IVA 0 %.
- [ ] Propagar los permisos de facturación a los roles Contador y Cajero de gimnasios ya existentes (las plantillas solo aplican a gimnasios nuevos).
- [ ] `INVOICING_MASTER_KEY` definida para API **y** Worker en producción (la misma).
- [ ] Correo de comprobantes: plantilla con diseño (hoy es texto plano) y SMTP de producción con SPF/DKIM.
- [ ] Probar el RIDE con un logo real del gimnasio (solo se probó con un PNG de prueba).
- [ ] Pantalla para reintentar correos de comprobantes fallidos (hoy se ven en el detalle y se cuentan en el panel; el reenvío manual crea otro envío).
- [ ] Elegir el punto de emisión al emitir (hoy se usa el de la sucursal de la venta).
- [ ] Facturar también desde la pantalla de Cobros (hoy solo desde la ficha del alumno) y evaluar la facturación automática al cobrar.
- [ ] Aviso al emisor cuando un comprobante queda devuelto, no autorizado o estacionado (hoy solo se ve en el panel).

- [ ] Guardia contra envíos accidentales al SRI real desde gimnasios de prueba (por ejemplo, avisar en pantalla cuando el RUC del emisor no coincide con el del certificado o no existe en el catastro).

## Transversales (no olvidar)

- [ ] Nómina (alcance sin definir).
- [ ] App móvil (recomendado: PWA).
- [ ] Alojamiento estable, copias de seguridad y monitoreo.
- [ ] Textos legales definitivos.
- [ ] CAPTCHA en formularios públicos.
- [ ] Verificar el CI de GitHub (nunca se ha ejecutado).
- [ ] Pantalla para registros de correo `Failed` de la plataforma.
- [ ] WhatsApp / SMS.
- [ ] Proveedor de pagos real.
- [ ] Inventario / punto de venta.
