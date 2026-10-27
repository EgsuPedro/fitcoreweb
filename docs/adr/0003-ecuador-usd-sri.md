# ADR 0003: Mercado Ecuador, moneda USD y facturación SRI

**Estado:** Aprobado (Fase 0). Implementación de facturación en Fase 8.

## Decisión
- Moneda USD. `Money` redondea a 2 decimales y exige moneda ISO 4217.
- Zona horaria por defecto `America/Guayaquil`, configurable por sucursal (Galápagos usa `Pacific/Galapagos`).
- IVA como tabla de tarifas con vigencia, nunca como constante. Cada línea guarda la tarifa aplicada. **Validar la tarifa vigente con el SRI y un contador antes de la Fase 3.**
- Facturación electrónica con integración **directa** al SRI (XML firmado con certificado por tenant) detrás de un puerto, intercambiable por un proveedor intermedio.
- El perfil tributario (régimen, obligado a contabilidad, comprobantes que emite) es configurable por tenant.
- Datos de salud tratados como sensibles según la LOPDP: consentimiento explícito, cifrado y control de acceso estricto.

## Estado real
Nada de esto emite comprobantes válidos todavía. Hasta la Fase 8 solo se preparan los datos.
