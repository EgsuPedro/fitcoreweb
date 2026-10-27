# ADR 0003: Mercado Ecuador, moneda USD y facturación SRI

**Estado:** Aprobado (Fase 0). Implementación de facturación en Fase 8.

## Decisión
- Moneda USD. `Money` redondea a 2 decimales y exige moneda ISO 4217.
- Zona horaria por defecto `America/Guayaquil`, configurable por sucursal (Galápagos usa `Pacific/Galapagos`).
- IVA como tabla de tarifas con vigencia, nunca como constante. Cada línea guarda la tarifa aplicada. **Validar la tarifa vigente con el SRI y un contador antes de la Fase 3.**
- Facturación electrónica con integración **directa** al SRI (XML firmado con certificado por tenant) detrás de un puerto, intercambiable por un proveedor intermedio.
- El perfil tributario (régimen, obligado a contabilidad, comprobantes que emite) es configurable por tenant.
- Datos de salud tratados como sensibles según la LOPDP: consentimiento explícito, cifrado y control de acceso estricto.

## IVA: tarifa confirmada y mecanismo de cambio (decisión del 2026-10-27)
El usuario confirmó con su contador que la tarifa general vigente es **15 %**. Como el Ejecutivo puede modificarla por decreto, no se codifica en ningún lado. En la Fase 3 se implementa así:
- Tabla global `tax_rates` (código, nombre, porcentaje, vigente desde, vigente hasta). Se siembra: **IVA 15 %** (general, por defecto), **IVA 0 %** y **No objeto / Exento**, con los códigos del SRI.
- Ante un decreto, el equipo de plataforma agrega una tarifa nueva con su fecha de inicio y cierra la anterior; no se edita ni se borra una tarifa ya usada. La fecha de vigencia no puede solaparse con otra del mismo tipo.
- Cada servicio, plan o producto referencia el **tipo** de impuesto (general, 0 %, exento); la tarifa se resuelve por la fecha del hecho generador.
- Cada línea de cobro **congela** la tarifa aplicada (porcentaje y código), de modo que un cambio posterior no altera comprobantes ya emitidos.
- La tarifa por defecto es solo el valor de partida; el contador del gimnasio puede marcar sus ítems como 0 % o exentos según su perfil tributario.

## Estado real
Nada de esto emite comprobantes válidos todavía. Hasta la Fase 8 solo se preparan los datos.
