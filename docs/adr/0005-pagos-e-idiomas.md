# ADR 0005: Pagos manuales primero e internacionalización

**Estado:** Aprobado (Fase 0)

## Pagos
- Al inicio, transferencias con comprobante manual: método, referencia, banco, fecha e imagen. El pago queda `pendiente de verificación` hasta que recepción o caja lo confirme; recién entonces se aplica a las obligaciones.
- La pasarela de tarjeta queda **en standby**: solo existirá el puerto `IPaymentGateway`, sin implementación.
- Los pagos son inmutables y se corrigen con reversas. Las operaciones financieras exigen `Idempotency-Key`.

## Idiomas
- Español (`es-EC`) por defecto, con soporte multi-idioma desde el inicio.
- La API devuelve códigos de error estables y cada cliente los traduce.
- Angular con Transloco o ngx-translate, Flutter con `intl` y archivos ARB.
- El locale es configurable por usuario y por tenant.
