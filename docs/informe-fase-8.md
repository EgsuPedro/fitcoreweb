# Informe de la Fase 8: sitio comercial, registro de gimnasios y onboarding

Decisiones y diseño: [ADR 0013](adr/0013-sitio-comercial-registro-y-onboarding.md). Validación manual paso a paso: [guía de pruebas](guia-pruebas-fase-8.md). Los números de este informe salen de ejecuciones reales (la fecha de la última regresión completa es 2026-10-30); nada está marcado como aprobado sin haberse ejecutado.

## 1. Qué se entregó
| # | Bloque | Resultado |
|---|---|---|
| 1 | Diagnóstico y ADR | Qué existía (alta de gimnasios, planes y suscripciones, login) y qué faltaba (correo, recuperación, invitaciones, onboarding, sitio público). |
| 2 | Correo | Puerto `IEmailSender` + **SMTP real** (MailKit) + cola con reintentos + plantillas es/en + Mailpit en desarrollo + tokens de un solo uso. |
| 3 | Planes públicos | `plans.is_public` explícito, `GET /public/plans[/{code}]`, publicar/ocultar en plataforma. |
| 4 | Registro | Solicitud pendiente → confirmación del correo → gimnasio, propietario, aceptaciones legales y suscripción; una prueba gratuita por propietario. |
| 5 | Recuperación de contraseña | Enlace de 1 hora y un solo uso, cierra sesiones, aviso de cambio. |
| 6 | Onboarding | Asistente persistente en el servidor (datos del gimnasio, sucursal principal con horario, pasos opcionales). |
| 7 | Invitaciones | Rol y sucursales definidos por quien invita, plazas del plan reservadas y re-comprobadas. |
| 8 | Sitio público | 9 páginas prerenderizadas con SEO, contacto y demostraciones, pantalla de plataforma para atenderlas. |
| 9 | Pantallas de cuenta | Registro, confirmación, recuperación, invitación y resumen del propietario. |
| 10 | Revisión final | Cobertura de las 20 pruebas obligatorias, accesibilidad, seguridad, rendimiento y documentación. |

## 2. Rutas
**Públicas (sin sesión):** `/` (portada; quien inició sesión ve su panel), `/funcionalidades`, `/planes`, `/nosotros`, `/preguntas-frecuentes`, `/contacto`, `/registro`, `/terminos`, `/privacidad` (prerenderizadas, indexables), y con enlace del correo y `noindex`: `/verificar-correo`, `/olvide-mi-clave`, `/restablecer-clave`, `/aceptar-invitacion`.
**Privadas (con sesión):** `/onboarding` (`organization.update`), `/invitations` (`users.create`), `/platform/leads` (`platform.leads.read`); el resto del panel no cambió. Un visitante que pide una dirección del panel va a `/login` y vuelve a ella.

## 3. Endpoints nuevos
Con sus permisos y protecciones en la [matriz de permisos](permissions-matrix.md#fase-8--rutas-públicas-asistente-e-invitaciones). Resumen: `GET /public/plans[/{code}]`, `POST /public/registrations` (+`/verify-email`, `/resend-verification`), `POST /public/invitations/preview|accept`, `POST /public/contact`, `POST /public/demo-requests`, `POST /auth/forgot-password`, `POST /auth/reset-password`, `GET/PUT/POST /onboarding/…`, `GET/POST /invitations[/{id}/revoke|resend]`, `PATCH /platform/plans/{id}/visibility`, `GET/PATCH /platform/contact-requests`.

## 4. Flujo de registro y verificación
1. `POST /public/registrations`: valida (formato, política de contraseña, identificador libre, plan público con prueba o precio 0, versiones legales vigentes). Si no hay SMTP responde **503** y no guarda nada. Si no, guarda una **solicitud pendiente** (contraseña hasheada), un token de confirmación (solo su SHA-256) y encola el correo. La respuesta es siempre `202`, exista o no el correo.
2. El Worker envía el correo por SMTP (reintentos 1 min, 5 min, 15 min, 1 h, 6 h; máximo 5).
3. `POST /public/registrations/verify-email`: en **una transacción** gasta el token, crea gimnasio (`PendingSetup`), roles, propietario (solo el rol Propietario), aceptación de términos y privacidad con versión y fecha, reclamación de la prueba gratuita y auditoría. Después abre la suscripción (en prueba o activa según el plan). Si ese segundo paso falla dos veces, el gimnasio se **suspende** y el registro queda `Failed` para revisión (es la única parte no atómica: usa otro módulo y otra conexión).
4. El propietario inicia sesión y completa el asistente; el estado `PendingSetup` no bloquea el uso.

## 5. Configuración requerida para el correo
Sin estos valores **no se envía ningún correo y el sistema lo dice** (503 en registro, recuperación e invitaciones; el formulario de contacto sí guarda la solicitud).

| Variable | Para qué |
|---|---|
| `SMTP_HOST`, `SMTP_PORT`, `SMTP_SECURITY` (`Auto`/`StartTls`/`SslOnConnect`/`None`), `SMTP_USER`, `SMTP_PASSWORD`, `SMTP_FROM` | Servidor SMTP genérico (cualquier proveedor). En desarrollo ya apuntan a Mailpit. |
| `DOMAIN` (producción) / `PUBLIC_SITE_URL` (desarrollo) | Dirección con la que se arman los enlaces de los correos. |
| `CONTACT_NOTIFY_EMAIL` | Correo del equipo comercial que recibe el aviso de cada contacto (opcional). |
| `REGISTER_RATE_LIMIT` | Límite por IP y minuto de registro/recuperación/contacto (10 por defecto). |
| `SITE_URL` (al compilar la web; el compose de producción la fija en `https://DOMINIO`) | Direcciones canónicas, `robots.txt` y `sitemap.xml`. |

Para que los correos no caigan en spam falta, en producción, un proveedor y configurar SPF/DKIM del dominio (ver [despliegue](despliegue.md)).

## 6. Integración con planes y suscripciones
- El precio, la moneda, la periodicidad y la prueba salen **siempre del servidor**; los campos `price`, `amount` y `currency` del cuerpo del registro se rechazan.
- Solo se contrata por registro un plan **público, activo, no heredado y con prueba o precio 0**. Un plan de pago sin prueba se pide a ventas: **registrarse nunca activa un cobro**.
- Una prueba gratuita por propietario (correo normalizado: minúsculas, sin `+etiqueta`, sin puntos en Gmail). Quien use correos totalmente distintos obtiene otra prueba: se mitiga con límites y revisión de la plataforma.
- Las invitaciones pendientes reservan plaza del límite de usuarios internos y se vuelve a comprobar al aceptar; el asistente respeta el límite de sucursales.

## 7. Estado del onboarding
Vive en `organizations.tenant_onboarding` (RLS). Obligatorios: datos del gimnasio y sucursal principal (primera del gimnasio, código `PRINCIPAL`). Opcionales: reglas de acceso y equipo. Cada paso es idempotente y retomable tras cerrar sesión. Los gimnasios anteriores al asistente nacen cerrados (`Ready`). `setup_status` es independiente del estado administrativo (`Active/Suspended`) y de la suscripción.

## 8. Migraciones creadas
Platform: `AddEmailQueueAndAccountTokens`, `AddPublicRegistration`, `AddOnboarding`, `AddInvitations`, `AddContactRequests` (10 en total). Subscriptions: `AddPlanIsPublic`. Todas con RLS forzada donde hay `tenant_id` y con restricciones CHECK/FK en SQL escrito a mano; la prueba `PlatformInfrastructureTests` comprueba que las 10 de Platform se aplican en una base nueva.

## 9. Pruebas ejecutadas (resultados reales)
| Suite | Resultado |
|---|---|
| Backend, integración (xUnit + PostgreSQL 17 en contenedores, rol limitado con RLS) | **845 pasan**, 0 fallan (antes de la fase: 664; +181) |
| Arquitectura | 28 pasan |
| Unitarias backend (SharedKernel 13, Analytics 42, Classes 57, Commercial 144, Subscriptions 134, Wellness 131) | 521 pasan |
| Frontend (vitest) | **116 pasan** (antes: 96) |
| Navegador real (Playwright contra API, Worker, PostgreSQL, Mailpit y la web compilada) | **100 pasan** de 100 (antes: 52) |
| Compilación de producción de la web | Prerenderiza las 9 rutas públicas; `check:i18n`: 1623 claves, todas en es y en |

Aclaración: en el informe del bloque 9 anoté «77 pasan» para el e2e completo; ese cifra era de una ejecución que se detuvo por un fallo de una prueba en serie (se saltaron 4 más). La ejecución completa de este bloque, con todo corregido, es la de 100 de 100.

### Las 20 pruebas obligatorias (trazabilidad)
Nombres reales de las pruebas (clase · método). Las de integración corren todas contra PostgreSQL 17 real.

| # | Requisito | Pruebas |
|---|---|---|
| 1 | Planes públicos activos | `PublicPlansTests` · `A_published_plan_is_visible_without_logging_in_with_its_real_terms`, `Public_responses_never_carry_internal_data`, `Only_plans_with_a_trial_or_a_price_of_zero_allow_self_service`; e2e `public-site` · «Planes muestra el catálogo real» |
| 2 | No mostrar planes no disponibles | `PublicPlansTests` · `A_new_active_plan_is_not_public_until_…`, `Hiding_a_plan_removes_it_from_the_site`, `Deactivating_a_public_plan_hides_it_…`, `The_legacy_plan_can_never_be_published_or_shown`, `Unknown_hidden_and_inactive_plans_answer_exactly_the_same`; `RegistrationTests` · `Plans_that_are_hidden_inactive_unknown_or_paid_without_trial_cannot_be_chosen` |
| 3 | Registrar una organización | `RegistrationTests` · `Registering_stores_a_pending_request_…`, `Confirming_the_email_creates_the_gym_the_owner_and_the_trial_subscription`, `Legal_acceptance_is_recorded_with_its_version_and_date`; e2e `account-screens` |
| 4 | Evitar duplicados de usuarios o gimnasios | `RegistrationTests` · `The_same_link_cannot_create_two_gyms`, `Simultaneous_confirmations_create_exactly_one_gym`, `An_identifier_already_used_by_a_gym_or_by_another_pending_registration_is_rejected`, `If_the_identifier_is_taken_before_confirming_nothing_is_created`, `A_new_registration_with_the_same_email_replaces_the_previous_one`; `InvitationTests` · `A_second_pending_invitation_for_the_same_email_or_an_existing_user_is_refused` |
| 5 | Validar correo y token de verificación | `RegistrationTests` · `Invalid_email_or_identifier_is_rejected_…`, `An_unknown_token_is_rejected_with_the_generic_error`, `The_password_is_never_stored_in_plain_text_and_the_token_only_as_a_hash`; `EmailInfrastructureTests` (tokens) |
| 6 | Rechazar tokens expirados o reutilizados | `RegistrationTests` · `An_expired_link_or_an_expired_request_cannot_create_a_gym`; `EmailInfrastructureTests` · `A_token_works_once_and_only_for_its_purpose`, `An_expired_or_unknown_token_is_rejected`, `Issuing_again_revokes_the_previous_token`, `Simultaneous_attempts_with_the_same_token_let_exactly_one_win`; `PasswordRecoveryTests` · `The_link_works_only_once`, `Unknown_or_expired_links_…`; `InvitationTests` · `The_link_works_only_once_even_with_simultaneous_attempts` |
| 7 | Impedir SuperAdmin en el registro | `RegistrationTests` · `Extra_fields_trying_to_grant_platform_roles_or_the_platform_gym_are_ignored_…`, `Invalid_email_or_identifier_is_rejected_…` (identificador `platform`), `Confirming_the_email_…` (solo rol Propietario y ningún permiso `platform.*`); `InvitationTests` · `Nobody_can_invite_someone_to_a_role_with_more_privileges_than_their_own` |
| 8 | Sucursal inicial sin duplicados | `OnboardingTests` · `Saving_the_branch_creates_the_main_branch_once_and_then_updates_it`, `If_the_gym_already_has_a_branch_the_step_updates_the_first_one_…` |
| 9 | Suscripción inicial según reglas comerciales | `RegistrationTests` · `Confirming_the_email_…` (Trialing y plan elegido), `A_free_plan_without_trial_needs_no_trial_claim_and_starts_active`, `A_second_trial_for_the_same_owner_is_refused_…`, `If_the_subscription_cannot_be_opened_the_gym_is_suspended_and_the_request_marked_failed` |
| 10 | Rechazar precios manipulados | `RegistrationTests` · `The_price_sent_by_the_browser_is_rejected`, `Plans_that_are_hidden_inactive_unknown_or_paid_without_trial_cannot_be_chosen` (plan de pago → ventas) |
| 11 | Respetar límites por plan | `InvitationTests` · `Pending_invitations_reserve_a_seat_…`, `Accepting_rechecks_the_limit_and_does_not_spend_the_link_when_there_is_no_seat`; `OnboardingTests` · `The_main_branch_step_respects_the_plan_branch_limit` |
| 12 | Recuperar una sesión de onboarding | `OnboardingTests` · `The_progress_is_kept_on_the_server_and_survives_a_new_login`; e2e `onboarding` (recarga la página y conserva el paso) |
| 13 | Sin acceso al onboarding de otro gimnasio | `OnboardingTests` · `Each_gym_only_sees_and_changes_its_own_setup`, `The_database_itself_isolates_the_progress_rows_by_gym` |
| 14 | Invitar solo al gimnasio autorizado | `InvitationTests` · `A_role_from_another_gym_or_an_unknown_branch_is_rejected`, `A_link_only_adds_the_person_to_the_gym_that_invited_them`, `Each_gym_only_sees_and_manages_its_own_invitations`, `The_database_itself_isolates_invitations_by_gym` |
| 15 | Invitaciones revocadas o vencidas | `InvitationTests` · `Unknown_expired_revoked_and_foreign_links_are_all_refused_the_same_way`, `Revoking_a_pending_invitation_kills_its_link_and_cannot_be_repeated`, `An_expired_invitation_shows_as_expired_and_can_be_replaced_…`, `An_invitation_from_someone_who_has_since_been_deactivated_is_not_honored`, `A_suspended_gym_cannot_take_in_new_staff` |
| 16 | Límites de frecuencia | `PublicPlansTests.Public_reads_are_rate_limited_per_ip`; `RegistrationTests` · `Registration_endpoints_are_rate_limited_per_ip`, `Too_many_registrations_for_one_email_in_an_hour_stop_silently`; `PasswordRecoveryTests` · `Recovery_endpoints_are_rate_limited_per_ip`, `Asking_again_too_soon_or_too_often_sends_nothing_more`; `ContactTests` · `The_forms_are_rate_limited_per_ip`, `Too_many_messages_from_one_email_in_an_hour_stop_silently`; `InvitationTests.There_is_a_daily_cap_on_invitations_per_gym` |
| 17 | Sin filtraciones entre gimnasios | `RegistrationTests.The_response_is_identical_whether_or_not_the_email_already_has_an_account`; `PasswordRecoveryTests` · `The_answer_is_identical_for_real_unknown_and_foreign_accounts`, `A_link_only_changes_the_account_it_was_issued_for_…`; `PublicPlansTests.Public_responses_never_carry_internal_data`; aislamiento de onboarding e invitaciones (filas 13 y 14) |
| 18 | Permisos del propietario | `OnboardingTests.Staff_without_permission_to_edit_the_gym_can_look_but_not_change`; `InvitationTests` · `Only_people_who_can_create_users_can_invite`, `Nobody_can_invite_…`; `PublicPlansTests.Only_the_platform_can_change_visibility`; `ContactTests.Only_the_platform_team_can_see_or_attend_requests`; `AuthorizationTests` (todo endpoint declara permiso) |
| 19 | Fallos del proveedor de correo | `EmailInfrastructureTests` · `A_failed_delivery_is_retried_later_and_then_given_up_after_five_attempts`, `An_invalid_recipient_fails_at_once_…`, `An_abandoned_send_is_recovered_when_its_lock_expires`, `Two_workers_never_send_the_same_email_twice`, `Without_smtp_configured_nothing_is_sent_…`, `The_smtp_sender_reports_an_unreachable_server_without_throwing`, **`The_smtp_sender_delivers_a_real_message_to_a_real_mail_server`** (Mailpit real); `RegistrationTests.Without_a_mail_server_registration_answers_503_and_stores_nothing`; `PasswordRecoveryTests.Without_a_mail_server_…`; `InvitationTests.Without_a_mail_server_nothing_is_invited` |
| 20 | Persistencia en PostgreSQL | Todas las pruebas de integración corren contra PostgreSQL 17 real (Testcontainers) con el rol limitado y RLS; en particular las de concurrencia (`SKIP LOCKED`, `UPDATE … RETURNING`, bloqueo de filas), RLS (`The_database_itself_isolates_…`), restricciones y `PlatformInfrastructureTests` (migraciones). No se usa proveedor en memoria |

## 10. Revisión de seguridad
Hecha con pruebas (no solo lectura):
- **Sin enumeración:** registro, reenvío, recuperación y contacto responden igual exista o no la cuenta (pruebas de la fila 17).
- **Tokens:** 32 bytes aleatorios, solo su SHA-256 en la base, un solo uso, consumo atómico, vencimiento, revocación al reemitir; no aparecen en auditoría (prueba por cada flujo) ni en la barra de direcciones (e2e).
- **Contraseñas:** la solicitud de registro guarda el hash y lo borra al terminar; ni la contraseña ni el token aparecen en la auditoría.
- **Aislamiento:** RLS forzada en `legal_acceptances`, `tenant_onboarding` e `invitations`, probada con el rol limitado; el gimnasio de una invitación sale del enlace y de la fila, nunca del navegador.
- **Cabeceras y CSP** de las páginas públicas probadas (nosniff, DENY, referrer, CSP, permisos); ninguna página pública produce errores de consola ni violaciones de CSP.
- **Entrada:** nada que escribe una persona entra sin codificar en los correos (HTML y encabezados) ni en JSON-LD; el cuerpo del registro rechaza precio/importe/moneda e ignora campos de roles (asignación masiva probada).
- **Antiabuso:** límites por IP, tope por correo y por cuenta, campo trampa. **No hay CAPTCHA.**
- **Sin verificar / pendiente:** pruebas de intrusión externas; revisión de dependencias nuevas (MailKit, `@angular/ssr`) más allá de `npm audit` del último `npm install` (0 vulnerabilidades en `tests/e2e`); el CI de GitHub nunca se ha ejecutado (no hay `gh`).

## 11. Accesibilidad, SEO y rendimiento (medido)
- **Accesibilidad:** axe (WCAG 2 A/AA y 2.1 A/AA) sobre las 14 páginas públicas y de cuenta, más contraste en **modo oscuro** en 5 páginas: **0 violaciones**. Se comprobó que axe de verdad detecta fallos (se le inyectó un botón sin nombre y una imagen sin texto alternativo). Enlace «saltar al contenido», navegación por teclado, FAQ con `<details>`, errores anunciados (`role="alert"`). **No sustituye** una revisión manual con lector de pantalla.
- **SEO:** título, descripción, Open Graph, Twitter, canónica y `index,follow` por página (probado leyendo el HTML sin JavaScript); datos estructurados `FAQPage`; `robots.txt` y `sitemap.xml` con `SITE_URL`; rutas privadas `noindex`.
- **Rendimiento (portada, con nginx y gzip):** HTML 2,7 kB; JS inicial ≈ 125 kB y CSS 9,4 kB comprimidos; imágenes 12 kB (logo) y 25 kB (mascota), reducidas de 145 kB y 215 kB. En total la portada descarga menos de 400 kB (una prueba lo exige) y **no** incluye el GIF de 7 MB. **No** se midió Lighthouse ni tiempos de carga reales (no hay herramienta instalada); son pesos, no tiempos.

## 12. Limitaciones conocidas y pendientes
- **Textos legales en borrador** (marcados «pendiente de revisión legal», solo en español): hay que reemplazarlos por texto revisado por un abogado antes de abrir al público, y cambiar `LEGAL_VERSION` y `Legal__*` a la vez.
- **Sin CAPTCHA** (punto de extensión pendiente) y **sin cobro automático**: un plan de pago se coordina con el equipo; no hay proveedor de pagos conectado.
- **Correo en producción:** falta elegir proveedor SMTP y configurar SPF/DKIM; hoy solo se probó con Mailpit y con un servidor SMTP real de pruebas en contenedor.
- **El segundo paso del registro** (abrir la suscripción) no es atómico con la creación del gimnasio; la compensación es suspender y marcar para revisión. No hay pantalla en plataforma para los registros `Failed` (se ven en la base y en los registros del servidor).
- **El filtro de roles del navegador** (`canAssignRole`) no conoce los permisos sensibles concedibles: la pantalla de Usuarios no ofrece al propietario Entrenador ni Nutricionista (la de Invitaciones muestra todos y decide el servidor).
- **CI de GitHub sin ejecutar:** el job e2e ahora levanta Worker, Mailpit y la web compilada; no se ha corrido en GitHub.
- **Una prueba gratuita por propietario no es infalible** frente a quien use correos distintos.
- El sitio público muestra los nombres de funciones y límites de los planes en español aunque el sitio esté en inglés (vienen del catálogo).
- **Siguen pendientes de antes** (no de esta fase): facturación electrónica del SRI, nómina, app móvil, hosting estable, copias de seguridad fuera del servidor, aviso por WhatsApp/SMS, proveedor de pagos real, inventario y punto de venta.

## 13. Criterios de entrega
Resumen de funcionalidades ✔ (sección 1) · archivos (commits `c5accaa` a este bloque, ver `git log`) ✔ · rutas ✔ (2) · endpoints ✔ (3) · flujo de registro y verificación ✔ (4) · configuración de correo ✔ (5) · integración con planes ✔ (6) · estado del onboarding ✔ (7) · pruebas y resultados reales ✔ (9) · limitaciones ✔ (12). No se afirma que se envían correos reales a internet, se procesan pagos ni se completan registros más allá de lo probado: el envío se probó contra un servidor SMTP real de pruebas (Mailpit), no contra un proveedor de producción.
