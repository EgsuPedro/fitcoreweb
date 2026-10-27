# ADR 0002: Multiempresa con `tenant_id` y RLS

**Estado:** Aprobado (Fase 0). Implementación en Fase 2.

## Decisión
- Base y esquema compartidos con `tenant_id` en toda tabla de negocio.
- El tenant sale **solo del token**, nunca de ruta, cuerpo ni cabecera.
- Defensa en capas:
  1. Filtro global de consultas en EF Core y interceptor de `SaveChanges`.
  2. Row-Level Security en PostgreSQL (`SET LOCAL app.tenant_id`).
  3. FK compuestas `(tenant_id, id)` para impedir relaciones cruzadas.
- Un recurso de otro tenant responde **404**, no 403.
- Aislamiento por sucursal: `branch_id` más la tabla `user_branches`.

## Consecuencias
Bajo costo operativo. Un cliente enterprise puede migrarse a su propia base sin cambiar el modelo.
