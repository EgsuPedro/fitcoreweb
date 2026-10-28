# Informe de la Fase 4 — Entrenamiento, nutrición, evaluaciones y progreso

Corresponde a la «FASE 3» del prompt del usuario (nuestra numeración: 4). Módulo `FitCore.Wellness`. Decisiones y reglas: [ADR 0009](adr/0009-modulo-wellness.md). Fuera de la fase: Flutter, facturación electrónica SRI, nómina y contabilidad.

## Qué se entrega
| Bloque | Contenido | Estado |
|---|---|---|
| 1 | Diagnóstico del repositorio y dependencias | Hecho |
| 2a | Acceso del alumno al portal (clave temporal con cambio obligatorio, endpoints `/me`) | Hecho |
| 2b | Módulo `Wellness`: entrenadores, disponibilidad, asignaciones con historial, observaciones profesionales | Hecho |
| 3 | Biblioteca de ejercicios (catálogo base de 42 + propios del gimnasio) | Hecho |
| 4 | Rutinas versionadas, asignaciones, sesiones de entrenamiento y progreso por ejercicio | Hecho |
| 5 | Nutrición: planes versionados, perfil alimentario y alergias, registro de comidas | Hecho |
| 6 | Evaluaciones físicas (IMC, historial, comparación, correcciones) | Hecho |
| 7 | Objetivos, adherencia al entrenamiento y a la nutrición | Hecho |
| 8 | Fotos de progreso privadas y consentimiento (`IFileStore`) | Hecho |
| 9 | Angular: constructores de planes, panel del alumno y del personal, gráficos | Hecho |
| 10 | Regresión, README, guía de ejecución e informe | Hecho |

## Privacidad: lo que condiciona todo el módulo
- **Permisos sensibles** (nutrición, alergias, evaluaciones, objetivos, fotos, observaciones) **no** se dan por defecto al Propietario ni al AdministradorSucursal: se derivan automáticamente de `PermissionDefinition.Sensitive` (`RoleTemplates.OwnerExcluded`). Un administrador que los necesite debe concedérselos a un rol de forma explícita (queda auditado).
- **Entrenador:** rutinas, evaluaciones, objetivos y fotos de sus alumnos; no nutrición. **Nutricionista:** nutrición. **Alumno:** solo lo suyo.
- **Alcance por alumno** (`MemberScope`): uno mismo; o personal con acceso a la sucursal y `wellness.members.all`, o entrenador asignado; en cualquier otro caso, 404 (no se revela que existe).
- **Auditoría del hecho, no del contenido:** cada lectura de personal sobre datos sensibles registra quién, cuándo y sobre qué alumno, nunca el contenido.
- **Fotos:** disco privado tras `IFileStore`, claves aleatorias, solo JPEG/PNG verificados por firma, metadatos EXIF eliminados sin recomprimir, servidas únicamente por la API con `Cache-Control: private, no-store`. Consentimiento en bitácora solo-anexado con versión del aviso; un menor solo consiente con su representante registrado por el personal. Retirar el consentimiento borra las fotos y sus archivos; un candado consultivo en base de datos evita que una subida simultánea sobreviva a la revocación.

## Migraciones (esquema `wellness`)
`InitialWellness` → `AddTrainersSecurity` → `AddExercises` → `AddExercisesSecurity` → `AddTraining` → `AddTrainingSecurity` → `AddNutrition` → `AddNutritionSecurity` → `AddAssessments` → `AddAssessmentsSecurity` → `AddGoals` → `AddGoalsSecurity` → `AddPhotos` → `AddPhotosSecurity`.
Todas con RLS forzada por gimnasio, claves compuestas (tenant, id), versiones y trazas solo-anexado (sin UPDATE/DELETE para el rol de la aplicación) y triggers de respaldo.

## Endpoints (bajo `/api/v1`)
- **Entrenadores:** `GET/POST /trainers`, `GET /trainers/me`, `GET/PUT /trainers/{id}`, `PATCH …/status`, `GET/PUT …/availability`, `GET/POST …/members`, `POST …/members/{memberId}/unassign`, `GET /members/{id}/trainer-assignments`, `GET/POST /members/{id}/trainer-notes`.
- **Ejercicios:** `GET/POST /exercises`, `GET/PUT /exercises/{id}`, `PATCH …/status`, `POST …/duplicate`; catálogo base en `/platform/exercises`.
- **Rutinas:** `GET/POST /training-plans`, `GET/PUT /training-plans/{id}`, `GET …/versions`, `POST …/duplicate`, `PATCH …/status`, `POST …/assignments`, `POST /training-assignments/{id}/end`, `GET /members/{id}/training-plan|training-assignments|workout-sessions|exercise-progress`, `GET …/workout-sessions/compare`, `POST /workout-sessions`, `GET /workout-sessions/{id}`, `POST …/trainer-comment`.
- **Nutrición:** `GET/POST /nutrition-plans`, `GET/PUT /nutrition-plans/{id}`, `GET …/versions`, `POST …/duplicate`, `PATCH …/status`, `POST …/assignments`, `POST /nutrition-assignments/{id}/end`, `GET /members/{id}/nutrition-plan|nutrition-assignments|meal-logs`, `GET/POST /members/{id}/dietary-profile`, `POST /meal-logs`.
- **Evaluaciones y objetivos:** `GET/POST /members/{id}/assessments`, `GET …/assessments/compare|progress`, `GET/POST /members/{id}/goals`, `POST /goals/{id}/close`, `GET /members/{id}/training-adherence|nutrition-adherence`.
- **Fotos:** `GET /members/{id}/photo-consent`, `POST …/photo-consent/grant|revoke`, `GET/POST /members/{id}/progress-photos` (multipart), `GET /progress-photos/{id}/content`, `DELETE /progress-photos/{id}`.
- **Acceso del alumno:** `POST /members/{id}/access`, `POST …/access/reset`, `PATCH …/access`, `GET /me/member|memberships|payments|attendance`.

## Pantallas (Angular, español por defecto, inglés disponible)
- **Personal:** *Entrenadores* (perfil, disponibilidad, alumnos, observaciones, enlace *Seguimiento*), *Ejercicios*, *Rutinas* y *Planes nutricionales* (constructores con versiones y duplicado), ficha del alumno con pestañas Entrenamiento, Nutrición, Evaluaciones, Objetivos y Fotos según el permiso de quien mira.
- **Alumno:** su portal con la rutina vigente y registro de sesión, su plan y registro de comidas, evolución con gráficos, objetivos con avance y fotos con gestión del consentimiento.
- Un único componente (`fc-member-wellness`) sirve a ambos; la API aplica las mismas reglas.

## Pruebas (resultados de la regresión final)
Regresión completa del 2026-10-28, en local, contra PostgreSQL real:

| Suite | Resultado |
|---|---|
| Unitarias SharedKernel / Commercial / Wellness | 13 / 144 / 131 correctas |
| Arquitectura (límites entre módulos, filtros de tenant, endpoints con autorización) | 17 correctas |
| Integración (API + PostgreSQL con Testcontainers, 4 colecciones en paralelo) | 388 correctas, 0 fallos (8 min 18 s) |
| Angular: Vitest | 60 correctas |
| Angular: claves de traducción es/en | 770 claves, todas presentes |
| Angular:  de producción | correcto |
| e2e Playwright (navegador real, API y base reales) | 30 correctas (1,6 min) |

Las pruebas de integración incluyen el aislamiento entre gimnasios (RLS), los permisos sensibles por rol, el alcance por alumno, la inmutabilidad en base de datos y la carrera entre retirar el consentimiento y subir fotos (6 subidas contra 1 retiro: no queda ninguna fila ni archivo).

## Pendiente y límites conocidos
- Pantalla para **comparar sesiones lado a lado** (la API ya la expone) y selector de ejercicios con búsqueda en el constructor de rutinas (hoy campo con lista).
- El **Propietario** ve las rutinas (no son datos sensibles por decisión); ocultarlas es un cambio de permisos, no de código.
- **Nutricionista–alumno:** hoy el alcance es por sucursal y `wellness.members.all`; no hay asignación nutricionista–alumno (límite documentado).
- Si el borrado de un archivo falla tras retirar el consentimiento, la fila ya no existe y el archivo huérfano queda registrado en el log para limpieza manual.
- Sin recordatorios de entrenamiento y sin edición del perfil por el propio alumno.
- Heredado de fases anteriores: envío real de WhatsApp (falta la cuenta de Meta) y recuperación de contraseña (falta proveedor de correo).
- Las fotos usan disco local (volumen Docker `fitcore_files`); para producción multi-servidor conviene implementar `IFileStore` sobre almacenamiento de objetos (el puerto ya existe).
