# Pendientes (lista viva)

Se agrega aquí lo que queda abierto mientras se avanza; se revisa al cerrar cada fase. Marcar con `[x]` y fecha cuando se resuelva.

## Fase 9 — Facturación electrónica

- [ ] Notas de crédito: emisión y prueba real en el SRI de pruebas (bloque 10).
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
