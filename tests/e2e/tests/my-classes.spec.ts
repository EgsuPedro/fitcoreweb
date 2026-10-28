import { expect, test, type APIRequestContext, type Page } from '@playwright/test';
import { platformCredentials, signIn } from './support';

/**
 * Portal del alumno contra API + PostgreSQL reales: una clase de un solo cupo; Ana reserva, Beto queda en la lista de espera,
 * Ana cancela y a Beto se le ofrece el cupo, que acepta. Cada alumno solo ve lo suyo.
 */
const run = Date.now().toString(36);
const slug = `mic-${run}`;
const ownerEmail = `dueno@${slug}.test`;
const ownerPassword = 'Dueno-Clave-12345';
const studentPassword = 'Alumno-Clave-12345';
const students = [
  { first: 'Ana', last: 'Portal', id: '1712345675', email: `ana@${slug}.test` },
  { first: 'Beto', last: 'Portal', id: '1710034065', email: `beto@${slug}.test` },
];

async function login(api: APIRequestContext, tenantSlug: string, email: string, password: string): Promise<string> {
  const response = await api.post('/api/v1/auth/login', { data: { tenantSlug, email, password, useCookie: false } });
  expect(response.ok(), await response.text()).toBeTruthy();
  return ((await response.json()) as { accessToken: string }).accessToken;
}

function tomorrowInEcuador(): string {
  const now = new Date(Date.now() - 5 * 3600 * 1000);
  now.setUTCDate(now.getUTCDate() + 1);
  return now.toISOString().slice(0, 10);
}

test.describe.serial('portal del alumno: clases', () => {
  let ana: Page;
  let beto: Page;

  test.beforeAll(async ({ playwright, baseURL, browser }) => {
    const api = await playwright.request.newContext({ baseURL });
    const platform = platformCredentials();
    const platformToken = await login(api, 'platform', platform.email, platform.password);
    const tenant = await api.post('/api/v1/platform/tenants', {
      headers: { Authorization: `Bearer ${platformToken}` },
      data: { slug, name: `Gimnasio ${run}`, timeZone: 'America/Guayaquil', locale: 'es', ownerEmail, ownerFullName: 'Dueña Portal', ownerPassword },
    });
    expect(tenant.ok(), await tenant.text()).toBeTruthy();
    const auth = { Authorization: `Bearer ${await login(api, slug, ownerEmail, ownerPassword)}` };

    const branch = await api.post('/api/v1/branches', { headers: auth, data: { code: 'UIO', name: 'Quito Centro', timeZone: 'America/Guayaquil', sriEstablishmentCode: '001', address: null } });
    expect(branch.ok(), await branch.text()).toBeTruthy();
    const branchId = ((await branch.json()) as { id: string }).id;

    const roles = (await (await api.get('/api/v1/roles', { headers: auth })).json()) as { id: string; name: string }[];
    const user = await api.post('/api/v1/users', {
      headers: auth,
      data: { email: `instructora@${slug}.test`, fullName: 'Carla Instructora', password: 'Instruye-Clave-12345', roleIds: [roles.find((r) => r.name === 'Entrenador')!.id], branchIds: [branchId] },
    });
    expect(user.ok(), await user.text()).toBeTruthy();
    const profile = await api.post('/api/v1/trainers', { headers: auth, data: { userId: ((await user.json()) as { id: string }).id, specialties: ['Yoga'] } });
    expect(profile.ok(), await profile.text()).toBeTruthy();
    const trainerId = ((await profile.json()) as { id: string }).id;

    const groupClass = await api.post('/api/v1/group-classes', {
      headers: auth,
      data: {
        branchId, name: 'Yoga portal', activity: 'Yoga', instructorId: trainerId, durationMinutes: 60, capacity: 1, level: 'AllLevels',
        minBookingNoticeMinutes: 30, maxBookingAdvanceDays: 30, cancellationDeadlineMinutes: 120, waitlistEnabled: true, waitlistCapacity: 5,
        offerWindowMinutes: 30, requireActiveMembership: false, applyPaymentPolicy: false,
      },
    });
    expect(groupClass.ok(), await groupClass.text()).toBeTruthy();
    const classId = ((await groupClass.json()) as { id: string }).id;
    const startsAt = new Date(`${tomorrowInEcuador()}T09:00:00-05:00`).toISOString();
    const session = await api.post('/api/v1/class-sessions', { headers: auth, data: { classId, startsAt } });
    expect(session.ok(), await session.text()).toBeTruthy();

    for (const student of students) {
      const member = await api.post('/api/v1/members', {
        headers: auth,
        data: { branchId, firstName: student.first, lastName: student.last, idType: 'Cedula', idNumber: student.id, email: student.email, phone: '0991234567', birthDate: '1992-03-04' },
      });
      expect(member.ok(), await member.text()).toBeTruthy();
      const memberId = ((await member.json()) as { id: string }).id;
      const access = await api.post(`/api/v1/members/${memberId}/access`, { headers: auth });
      const temporary = ((await access.json()) as { temporaryPassword: string }).temporaryPassword;
      const tempToken = await login(api, slug, student.email, temporary);
      const changed = await api.post('/api/v1/auth/change-password', { headers: { Authorization: `Bearer ${tempToken}` }, data: { currentPassword: temporary, newPassword: studentPassword } });
      expect(changed.ok(), await changed.text()).toBeTruthy();
    }
    await api.dispose();

    const open = async (email: string): Promise<Page> => {
      const page = await (await browser.newContext({ baseURL, locale: 'es-EC' })).newPage();
      await signIn(page, slug, email, studentPassword);
      await expect(page.getByRole('heading', { name: 'Mis clases' })).toBeVisible();
      return page;
    };
    ana = await open(students[0].email);
    beto = await open(students[1].email);
  });

  test.afterAll(async () => {
    for (const page of [ana, beto]) {
      await page.context().close();
    }
  });

  test('Ana ve la agenda de su sucursal y reserva el único cupo', async () => {
    const row = ana.getByRole('row', { name: /Yoga portal/ });
    await expect(row).toBeVisible();
    await row.getByRole('button', { name: 'Reservar' }).click();
    await expect(ana.getByText('Reserva registrada.')).toBeVisible();
    await expect(row.getByText('Confirmada')).toBeVisible();

    await ana.getByRole('tab', { name: 'Mis reservas' }).click();
    await expect(ana.getByRole('row', { name: /Yoga portal/ }).getByText('Confirmada')).toBeVisible();
  });

  test('Beto ve la clase llena y se anota en la lista de espera', async () => {
    await beto.reload();
    const row = beto.getByRole('row', { name: /Yoga portal/ });
    await expect(row.getByText('Llena')).toBeVisible();
    await row.getByRole('button', { name: 'Anotarme en la espera' }).click();
    await expect(beto.getByText('Quedó en la lista de espera.')).toBeVisible();

    await beto.getByRole('tab', { name: 'Mis reservas' }).click();
    const mine = beto.getByRole('row', { name: /Yoga portal/ });
    await expect(mine.getByText('En espera')).toBeVisible();
    await expect(mine.getByText('puesto 1')).toBeVisible();
  });

  test('Ana cancela y a Beto se le ofrece el cupo, que acepta', async () => {
    await ana.getByRole('row', { name: /Yoga portal/ }).getByRole('button', { name: 'Cancelar' }).click();
    const dialog = ana.getByRole('dialog', { name: 'Cancelar reserva' });
    await dialog.getByRole('button', { name: 'Cancelar reserva', exact: true }).click();
    await expect(ana.getByText('Reserva cancelada.')).toBeVisible();
    await expect(ana.getByRole('row', { name: /Yoga portal/ }).getByText('Confirmada')).toHaveCount(0);

    await beto.reload();
    await beto.getByRole('tab', { name: 'Mis reservas' }).click();
    const offered = beto.getByRole('row', { name: /Yoga portal/ });
    await expect(offered.getByText('Cupo ofrecido')).toBeVisible();
    await expect(offered.getByText(/responda hasta las/)).toBeVisible();
    await offered.getByRole('button', { name: 'Aceptar cupo' }).click();
    await expect(beto.getByText('Cupo confirmado.')).toBeVisible();
    await expect(beto.getByRole('row', { name: /Yoga portal/ }).getByText('Confirmada')).toBeVisible();

    // Ana no tiene nada próximo y su historial está vacío.
    await ana.getByRole('tab', { name: 'Historial' }).click();
    await expect(ana.getByText('Todavía no tiene clases registradas.')).toBeVisible();
  });
});
