// Carga un volumen realista de datos en el gimnasio «volumen» para medir el rendimiento de analítica (solo desarrollo local).
// Uso: node scripts/analytics-volume.mjs [--members 20000]
// Crea el gimnasio por la API (4 sucursales, plan, instructor y clases) y rellena alumnos, membresías, pagos, asistencias y reservas
// directamente en PostgreSQL (como propietario de la base, con los disparadores desactivados solo durante la carga).
import { readFileSync, writeFileSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { fileURLToPath } from 'node:url';
const root = fileURLToPath(new URL('..', import.meta.url));
const env = Object.fromEntries(readFileSync(join(root, 'infra/compose/.env'), 'utf8').split(/\r?\n/).filter((l) => l.includes('=') && !l.startsWith('#')).map((l) => [l.slice(0, l.indexOf('=')), l.slice(l.indexOf('=') + 1)]));
const members = Number(process.argv[process.argv.indexOf('--members') + 1]) || 20000;
const api = 'http://localhost:8080/api/v1';
const slug = 'volumen';
const ownerPassword = 'Volumen-Clave-12345';

async function call(method, path, body, token) {
  const res = await fetch(api + path, { method, headers: { 'content-type': 'application/json', ...(token ? { authorization: `Bearer ${token}` } : {}) }, body: body ? JSON.stringify(body) : undefined });
  const text = await res.text();
  if (!res.ok) throw new Error(`${method} ${path} -> ${res.status} ${text}`);
  return text ? JSON.parse(text) : null;
}

const platform = (await call('POST', '/auth/login', { tenantSlug: 'platform', email: env.PLATFORM_ADMIN_EMAIL, password: env.PLATFORM_ADMIN_PASSWORD, useCookie: false })).accessToken;
try {
  await call('POST', '/platform/tenants', { slug, name: 'Gimnasio Volumen', timeZone: 'America/Guayaquil', locale: 'es', ownerEmail: `dueno@${slug}.test`, ownerFullName: 'Dueño Volumen', ownerPassword }, platform);
} catch (e) {
  if (!String(e).includes('409') && !String(e).includes('422')) throw e;
}
const token = (await call('POST', '/auth/login', { tenantSlug: slug, email: `dueno@${slug}.test`, password: ownerPassword, useCookie: false })).accessToken;
let branches = (await call('GET', '/branches?pageSize=100', null, token)).items;
for (const [code, name] of [['UIO', 'Quito'], ['GYE', 'Guayaquil'], ['CUE', 'Cuenca'], ['MCH', 'Machala']]) {
  if (!branches.some((b) => b.code === code)) {
    await call('POST', '/branches', { code, name, timeZone: 'America/Guayaquil', sriEstablishmentCode: String(branches.length + 1).padStart(3, '0'), address: null }, token);
    branches = (await call('GET', '/branches?pageSize=100', null, token)).items;
  }
}
let plans = (await call('GET', '/membership-plans?pageSize=100', null, token)).items;
for (const [name, price, months] of [['Mensual', 30, 1], ['Trimestral', 80, 3], ['Anual', 280, 12]]) {
  if (!plans.some((p) => p.name === name)) {
    await call('POST', '/membership-plans', { name, description: 'Volumen', price, durationValue: months, durationUnit: 'Month', allowedAccess: 'Sala', restrictions: null, commercialTerms: 'Sin devoluciones', taxKind: 'General', isActive: true, allowFreeze: true, maxFreezeDays: 15, branchIds: [] }, token);
  }
}
plans = (await call('GET', '/membership-plans?pageSize=100', null, token)).items;
const roles = await call('GET', '/roles', null, token);
const trainers = [];
for (const b of branches) {
  const u = await call('POST', '/users', { email: `inst-${b.code.toLowerCase()}-${Date.now().toString(36)}@${slug}.test`, fullName: `Instructor ${b.code}`, password: 'Instruye-Clave-12345', roleIds: [roles.find((r) => r.name === 'Entrenador').id], branchIds: [b.id] }, token);
  const t = await call('POST', '/trainers', { userId: u.id, specialties: ['Yoga'] }, token);
  trainers.push({ branch: b.id, trainer: t.id });
}
const tenantId = execFileSync('docker', ['exec', 'fitcore360-postgres-1', 'psql', '-U', 'fitcore', '-d', 'fitcore', '-Atc', `select id from organizations.tenants where slug='${slug}'`]).toString().trim();

const values = (rows) => rows.map((r) => `('${r.join("','")}')`).join(',');
const sql = `
set session_replication_role = replica;
create temp table _b(id uuid, n int);
insert into _b select id, row_number() over (order by code) from organizations.branches where tenant_id='${tenantId}';
create temp table _p(id uuid, name text, price numeric, months int, n int);
insert into _p select id, name, price, duration_value, row_number() over (order by name) from members.membership_plans where tenant_id='${tenantId}';
create temp table _t(branch_id uuid, trainer_id uuid);
`;
// Instructores: se asignan por orden de creación (el perfil guarda el usuario, no la sucursal).
const trainerRows = trainers.map((t) => `('${t.branch}','${t.trainer}')`).join(',');

const load = `
${sql}
insert into _t values ${trainerRows};

-- Limpieza de una carga anterior.
delete from classes.class_attendance where tenant_id='${tenantId}'; delete from classes.class_reservations where tenant_id='${tenantId}'; delete from classes.class_sessions where tenant_id='${tenantId}'; delete from classes.group_classes where tenant_id='${tenantId}';
delete from access.attendance where tenant_id='${tenantId}'; delete from billing.payment_allocations where tenant_id='${tenantId}'; delete from billing.payments where tenant_id='${tenantId}'; delete from billing.payment_obligations where tenant_id='${tenantId}';
delete from members.membership_events where tenant_id='${tenantId}'; delete from members.memberships where tenant_id='${tenantId}'; delete from members.members where tenant_id='${tenantId}';
-- Alumnos: ${members}, repartidos en las sucursales; altas a lo largo de 24 meses.
insert into members.members (id, tenant_id, branch_id, code, first_name, last_name, id_type, id_number, enrolled_on, status, created_at)
select gen_random_uuid(), '${tenantId}', (select id from _b where n = 1 + (g % 4)), 'V' || lpad(g::text, 6, '0'), 'Alumno' || g, 'Volumen', 'Cedula', lpad(g::text, 10, '0'),
       (current_date - (random() * 730)::int), case when random() < 0.08 then 'Inactive' else 'Active' end, now() - (random() * 730) * interval '1 day'
from generate_series(1, ${members}) g;

-- Membresías: 1 a 3 por alumno; las anteriores ya vencieron y encadenan renovaciones.
create temp table _m as
select m.id member_id, m.branch_id, m.enrolled_on, k, case when random() < 0.05 then 'Cancelled' else 'Active' end st, gen_random_uuid() id, (select id from _p where n = 1 + (abs(hashtext(m.id::text || k::text)) % 3)) plan_id
from members.members m cross join generate_series(0, 2) k
where m.tenant_id='${tenantId}' and (k = 0 or random() < 0.7);
insert into members.memberships (id, tenant_id, branch_id, member_id, plan_id, plan_name, plan_price, duration_value, duration_unit, tax_kind, tax_percent, allow_freeze, max_freeze_days,
                                 discount_amount, agreed_price, start_date, end_date, status, frozen_days_used, created_at, cancelled_at, cancel_reason)
select m.id, '${tenantId}', m.branch_id, m.member_id, m.plan_id, p.name, p.price, p.months, 'Month', 'General', 15, true, 15, 0, p.price,
       (m.enrolled_on + (m.k * 100)), (m.enrolled_on + (m.k * 100) + least(p.months, 3) * 30 - 1),
       m.st, 0,
       (m.enrolled_on + (m.k * 100))::timestamptz, case when m.st = 'Cancelled' then (m.enrolled_on + (m.k * 100) + 10)::timestamptz end, case when m.st = 'Cancelled' then 'Volumen' end
from _m m join _p p on p.id = m.plan_id;
insert into members.membership_events (id, tenant_id, membership_id, type, occurred_at)
select gen_random_uuid(), '${tenantId}', id, 'Activated', start_date::timestamptz from members.memberships where tenant_id='${tenantId}' and status <> 'Cancelled';

-- Obligaciones y pagos: uno por membresía (≈ ${members * 2}), más abonos; ~5 % queda pendiente.
create temp table _o as
select ms.id membership_id, ms.member_id, ms.branch_id, ms.agreed_price amount, ms.start_date, gen_random_uuid() oid, gen_random_uuid() pid, row_number() over () rn
from members.memberships ms where ms.tenant_id='${tenantId}' and ms.status <> 'Cancelled';
insert into billing.payment_obligations (id, tenant_id, branch_id, member_id, membership_id, reference, concept, issue_date, due_date, original_amount, discount_amount, payable_amount, subtotal, tax_amount, tax_kind, tax_percent, status, created_at)
select oid, '${tenantId}', branch_id, member_id, membership_id, 'OB-' || rn, 'Membresía', start_date, start_date + 5, amount, 0, amount, round(amount / 1.15, 2), amount - round(amount / 1.15, 2), 'General', 15,
       case when rn % 20 = 0 then 'Pending' else 'Paid' end, start_date::timestamptz
from _o;
insert into billing.payments (id, tenant_id, branch_id, member_id, receipt_number, method, method_detail, reference, cash_session_id, amount, status, paid_at, confirmed_at, reversed_at, reversal_reason, created_at)
select pid, '${tenantId}', branch_id, member_id, 'R-' || rn, (array['Cash','BankTransfer','Card','Other'])[1 + (rn % 4)::int], case when rn % 4 = 3 then 'Externo' end, case when rn % 4 in (1, 2) then 'REF-' || rn end, case when rn % 4 = 0 then gen_random_uuid() end, amount,
       case when rn % 50 = 1 then 'Reversed' when rn % 33 = 2 then 'PendingVerification' else 'Confirmed' end, start_date::timestamptz + interval '9 hours', case when rn % 33 = 2 and rn % 50 <> 1 then null else start_date::timestamptz + interval '9 hours' end,
       case when rn % 50 = 1 then start_date::timestamptz + interval '2 days' end, case when rn % 50 = 1 then 'Volumen' end, start_date::timestamptz
from _o where rn % 20 <> 0;
insert into billing.payment_allocations (id, tenant_id, payment_id, obligation_id, amount)
select gen_random_uuid(), '${tenantId}', pid, oid, amount from _o where rn % 20 <> 0;

-- Asistencia al gimnasio: ≈ 20 entradas por alumno activo en los últimos 12 meses.
insert into access.attendance (id, tenant_id, branch_id, member_id, check_in_at, check_out_at, check_out_by, check_out_method, method, is_exception, registered_by)
select gen_random_uuid(), '${tenantId}', m.branch_id, m.id, x.t, x.t + interval '1 hour', gen_random_uuid(), 'Manual', 'Manual', false, gen_random_uuid()
from members.members m cross join generate_series(1, 20) e cross join lateral (select now() - (random() * 365) * interval '1 day' - (random() * 14) * interval '1 hour' t) x where m.tenant_id='${tenantId}' and m.status = 'Active';

-- Clases: una por sucursal, 8 sesiones al día por sucursal durante 12 meses, 15 reservas confirmadas por sesión.
create temp table _c as select gen_random_uuid() id, b.id branch_id, t.trainer_id from _b b join _t t on t.branch_id = b.id;
insert into classes.group_classes (id, tenant_id, branch_id, name, activity, instructor_id, duration_minutes, capacity, level, min_booking_notice_minutes, max_booking_advance_days, cancellation_deadline_minutes, waitlist_enabled, waitlist_capacity, offer_window_minutes, require_active_membership, apply_payment_policy, is_active, created_at)
select id, '${tenantId}', branch_id, 'Yoga volumen', 'Yoga', trainer_id, 60, 20, 'AllLevels', 30, 30, 120, true, 5, 30, false, false, true, now() from _c;
create temp table _s as
select gen_random_uuid() id, c.id class_id, c.branch_id, c.trainer_id, ((current_date - d) + (6 + h * 2) * interval '1 hour')::timestamptz starts_at, random() < 0.04 cx
from _c c cross join generate_series(0, 364) d cross join generate_series(0, 7) h;
insert into classes.class_sessions (id, tenant_id, branch_id, class_id, instructor_id, starts_at, ends_at, original_starts_at, capacity, status, created_at, cancelled_at, cancel_reason)
select id, '${tenantId}', branch_id, class_id, trainer_id, starts_at, starts_at + interval '1 hour', starts_at, 20,
       case when cx then 'Cancelled' else 'Completed' end, starts_at - interval '7 days', case when cx then starts_at - interval '1 day' end, case when cx then 'Volumen' end from _s;
create temp table _r as
select gen_random_uuid() id, s.id session_id, s.branch_id, m.id member_id, s.starts_at, random() r
from _s s join lateral (select id from members.members where tenant_id='${tenantId}' and branch_id = s.branch_id order by random() limit 15) m on true;
insert into classes.class_reservations (id, tenant_id, branch_id, session_id, member_id, status, source, late_cancellation, created_at, cancelled_at, cancel_reason)
select id, '${tenantId}', branch_id, session_id, member_id, case when r < 0.1 then 'Cancelled' else 'Confirmed' end, 'Staff', false, starts_at - interval '2 days', case when r < 0.1 then starts_at - interval '1 day' end, case when r < 0.1 then 'Volumen' end from _r;
insert into classes.class_attendance (id, tenant_id, branch_id, session_id, reservation_id, member_id, outcome, created_at)
select gen_random_uuid(), '${tenantId}', branch_id, session_id, id, member_id, case when r < 0.3 then 'NoShow' else 'Attended' end, starts_at from _r where r >= 0.1;

analyze;
select 'members', count(*) from members.members where tenant_id='${tenantId}'
union all select 'memberships', count(*) from members.memberships where tenant_id='${tenantId}'
union all select 'payments', count(*) from billing.payments where tenant_id='${tenantId}'
union all select 'obligations', count(*) from billing.payment_obligations where tenant_id='${tenantId}'
union all select 'gym entries', count(*) from access.attendance where tenant_id='${tenantId}'
union all select 'sessions', count(*) from classes.class_sessions where tenant_id='${tenantId}'
union all select 'reservations', count(*) from classes.class_reservations where tenant_id='${tenantId}';
`;
const file = join(tmpdir(), 'analytics-volume.sql');
writeFileSync(file, load);
const out = execFileSync('docker', ['exec', '-i', 'fitcore360-postgres-1', 'psql', '-U', 'fitcore', '-d', 'fitcore', '-1', '-v', 'ON_ERROR_STOP=1'], { input: readFileSync(file), maxBuffer: 1 << 26 }).toString();
console.log(out);
console.log(`tenant ${tenantId}; propietario dueno@${slug}.test / ${ownerPassword}`);
