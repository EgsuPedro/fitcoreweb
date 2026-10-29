import { expect, test, type APIRequestContext, type Page } from '@playwright/test';
import { nav, platformCredentials, signIn } from './support';

/**
 * Clases grupales contra API + PostgreSQL + Worker reales: el propietario crea una clase y programa una sesión, reserva a dos alumnos
 * (el segundo entra a la lista de espera), cancela la primera reserva y acepta el cupo ofrecido; después cancela la sesión y el
 * instructor recibe el aviso en su bandeja (lo genera el Worker a partir de la outbox).
 */
const run = Date.now().toString(36);
const slug = `cls-${run}`;
const ownerEmail = `dueno@${slug}.test`;
const ownerPassword = 'Dueno-Clave-12345';
const trainerEmail = `instructora@${slug}.test`;
const trainerPassword = 'Instruye-Clave-12345';

async function login(api: APIRequestContext, tenantSlug: string, email: string, password: string): Promise<string> {
  const response = await api.post('/api/v1/auth/login', { data: { tenantSlug, email, password, useCookie: false } });
  expect(response.ok(), await response.text()).toBeTruthy();
  return ((await response.json()) as { accessToken: string }).accessToken;
}

/** Mañana en formato AAAA-MM-DD (la hora de la sucursal es la de Ecuador, UTC-5). */
function tomorrowInEcuador(): string {
  const now = new Date(Date.now() - 5 * 3600 * 1000);
  now.setUTCDate(now.getUTCDate() + 1);
  return now.toISOString().slice(0, 10);
}

test.describe.serial('clases grupales', () => {
  let ownerPage: Page;

  test.beforeAll(async ({ playwright, baseURL, browser }) => {
    const api = await playwright.request.newContext({ baseURL });
    const platform = platformCredentials();
    const platformToken = await login(api, 'platform', platform.email, platform.password);
    const tenant = await api.post('/api/v1/platform/tenants', {
      headers: { Authorization: `Bearer ${platformToken}` },
      data: { slug, name: `Gimnasio ${run}`, timeZone: 'America/Guayaquil', locale: 'es', ownerEmail, ownerFullName: 'Dueña Clases', ownerPassword },
    });
    expect(tenant.ok(), await tenant.text()).toBeTruthy();

    const auth = { Authorization: `Bearer ${await login(api, slug, ownerEmail, ownerPassword)}` };
    const branch = await api.post('/api/v1/branches', { headers: auth, data: { code: 'UIO', name: 'Quito Centro', timeZone: 'America/Guayaquil', sriEstablishmentCode: '001', address: null } });
    expect(branch.ok(), await branch.text()).toBeTruthy();
    const branchId = ((await branch.json()) as { id: string }).id;

    const roles = (await (await api.get('/api/v1/roles', { headers: auth })).json()) as { id: string; name: string }[];
    const trainerRole = roles.find((r) => r.name === 'Entrenador')!;
    const user = await api.post('/api/v1/users', {
      headers: auth,
      data: { email: trainerEmail, fullName: 'Carla Instructora', password: trainerPassword, roleIds: [trainerRole.id], branchIds: [branchId] },
    });
    expect(user.ok(), await user.text()).toBeTruthy();
    const userId = ((await user.json()) as { id: string }).id;
    const profile = await api.post('/api/v1/trainers', { headers: auth, data: { userId, specialties: ['Yoga'] } });
    expect(profile.ok(), await profile.text()).toBeTruthy();

    for (const [first, last, id] of [['Mario', 'Reserva', '1712345675'], ['Lucía', 'Espera', '1710034065']] as const) {
      const member = await api.post('/api/v1/members', { headers: auth, data: { branchId, firstName: first, lastName: last, idType: 'Cedula', idNumber: id, phone: '0991234567' } });
      expect(member.ok(), await member.text()).toBeTruthy();
    }
    await api.dispose();

    ownerPage = await (await browser.newContext({ baseURL, locale: 'es-EC' })).newPage();
    await signIn(ownerPage, slug, ownerEmail, ownerPassword);
    await expect(nav(ownerPage)).toBeVisible();
  });

  test.afterAll(async () => {
    await ownerPage.context().close();
  });

  test('se crea una clase con sus reglas y se programa una sesión que aparece en el calendario', async () => {
    const page = ownerPage;
    await nav(page).getByRole('link', { name: 'Clases', exact: true }).click();
    await page.getByRole('button', { name: 'Nueva clase' }).click();
    const dialog = page.getByRole('dialog');
    const branchOption = await dialog.locator('[formControlName="branchId"] option', { hasText: 'Quito Centro' }).getAttribute('value');
    await dialog.locator('[formControlName="branchId"]').selectOption(branchOption!);
    await dialog.locator('[formControlName="name"]').fill('Yoga e2e');
    await expect(dialog.locator('[formControlName="instructorId"] option', { hasText: 'Carla Instructora' })).toHaveCount(1);
    const instructor = await dialog.locator('[formControlName="instructorId"] option', { hasText: 'Carla Instructora' }).getAttribute('value');
    await dialog.locator('[formControlName="instructorId"]').selectOption(instructor!);
    await dialog.locator('[formControlName="capacity"]').fill('1');
    // La política de pagos solo aplica si se exige membresía: se apagan en orden para esta clase abierta a todos.
    await dialog.locator('[formControlName="applyPaymentPolicy"]').uncheck();
    await dialog.locator('[formControlName="requireActiveMembership"]').uncheck();
    await dialog.getByRole('button', { name: 'Guardar' }).click();
    await expect(page.getByText('Clase creada.')).toBeVisible();
    await expect(page.getByRole('row', { name: /Yoga e2e/ })).toBeVisible();

    await nav(page).getByRole('link', { name: 'Horario' }).click();
    await page.getByRole('button', { name: 'Programar sesión' }).click();
    const form = page.getByRole('dialog');
    await form.locator('[formControlName="classId"]').selectOption({ label: 'Yoga e2e · Quito Centro' });
    await form.locator('[formControlName="date"]').fill(tomorrowInEcuador());
    await form.locator('[formControlName="time"]').fill('09:00');
    await form.getByRole('button', { name: 'Guardar' }).click();
    await expect(page.getByText('Sesión programada.')).toBeVisible();

    await page.getByRole('button', { name: 'Mes' }).click();
    await expect(page.locator('.cal-session', { hasText: 'Yoga e2e' })).toBeVisible();
  });

  test('se reserva, el segundo alumno espera, y al cancelar el primero el cupo se ofrece y se acepta', async () => {
    const page = ownerPage;
    await page.locator('.cal-session', { hasText: 'Yoga e2e' }).first().click();
    const panel = page.getByRole('dialog', { name: 'Yoga e2e' });

    const reserve = async (search: string, name: RegExp, waitlist: boolean) => {
      await panel.getByRole('tab', { name: 'Reservar' }).click();
      await panel.getByRole('searchbox').fill(search);
      await panel.getByRole('option', { name }).click();
      if (waitlist) {
        await panel.getByLabel('Si la sesión está llena, anotar en la lista de espera').check();
      }
      await panel.getByRole('button', { name: 'Reservar', exact: true }).click();
    };

    await reserve('Mario', /Mario Reserva/, false);
    await expect(page.getByText('Reserva registrada.')).toBeVisible();
    await expect(panel.getByRole('row', { name: /Mario Reserva/ })).toBeVisible();
    await expect(panel.getByText('1 de 1 ocupados')).toBeVisible();

    // La sesión está llena: sin pedirlo no entra a la fila; pidiéndolo, sí.
    await panel.getByRole('tab', { name: 'Reservar' }).click();
    await panel.getByRole('searchbox').fill('Lucía');
    await panel.getByRole('option', { name: /Lucía Espera/ }).click();
    await panel.getByRole('button', { name: 'Reservar', exact: true }).click();
    await expect(panel.getByText('La sesión está llena.')).toBeVisible();
    await panel.getByLabel('Si la sesión está llena, anotar en la lista de espera').check();
    await panel.getByRole('button', { name: 'Reservar', exact: true }).click();
    await expect(page.getByText('Reserva registrada.').last()).toBeVisible();

    // El resumen de la sesión confirma que la reserva en espera quedó registrada antes de mirar la lista.
    await expect(panel.getByText('1 en espera')).toBeVisible();
    await panel.getByRole('tab', { name: 'Lista de espera' }).click();
    await expect(panel.getByRole('row', { name: /Lucía Espera/ })).toBeVisible();

    // Se cancela la reserva del primero: el cupo se reserva para quien espera.
    await panel.getByRole('tab', { name: 'Participantes' }).click();
    await panel.getByRole('row', { name: /Mario Reserva/ }).getByRole('button', { name: 'Cancelar reserva' }).click();
    await page.getByRole('dialog', { name: 'Cancelar reserva' }).getByRole('button', { name: 'Cancelar reserva' }).click();
    await expect(page.getByText('Reserva cancelada.')).toBeVisible();

    await panel.getByRole('tab', { name: 'Lista de espera' }).click();
    await expect(panel.getByRole('row', { name: /Lucía Espera/ }).getByText(/Responde hasta las/)).toBeVisible();
    await panel.getByRole('row', { name: /Lucía Espera/ }).getByRole('button', { name: 'Aceptar' }).click();
    await expect(page.getByText('Cupo confirmado.')).toBeVisible();

    await panel.getByRole('tab', { name: 'Participantes' }).click();
    await expect(panel.getByRole('row', { name: /Lucía Espera/ })).toBeVisible();
    // La sesión es de mañana: todavía no se puede registrar asistencia.
    await expect(panel.getByRole('button', { name: 'Asistió' })).toHaveCount(0);
  });

  test('cancelar la sesión avisa al instructor en su bandeja (outbox + Worker)', async ({ browser, baseURL }) => {
    const page = ownerPage;
    const panel = page.getByRole('dialog', { name: 'Yoga e2e' });
    await panel.getByRole('button', { name: 'Cancelar sesión' }).click();
    const reason = page.getByRole('dialog', { name: 'Cancelar sesión' });
    await reason.getByRole('textbox').fill('Mantenimiento de la sala');
    await reason.getByRole('button', { name: 'Cancelar sesión' }).click();
    await expect(page.getByText('Sesión cancelada.')).toBeVisible();

    const trainerPage = await (await browser.newContext({ baseURL, locale: 'es-EC' })).newPage();
    await signIn(trainerPage, slug, trainerEmail, trainerPassword);
    await expect(nav(trainerPage).getByRole('link', { name: 'Horario' })).toBeVisible();
    await expect(nav(trainerPage).getByRole('link', { name: 'Clases', exact: true })).toBeVisible();

    // El Worker procesa la outbox cada pocos segundos: se reintenta hasta que llegue el aviso.
    await trainerPage.goto('/inbox');
    // Hay que esperar a que la sesión termine de restaurarse antes de recargar: recargar a mitad del refresh descarta la respuesta (que rota la cookie)
    // y el siguiente intento llega con una cookie ya usada, que el servidor revoca por seguridad.
    await expect(nav(trainerPage)).toBeVisible();
    await expect(async () => {
      await trainerPage.reload();
      await expect(trainerPage.getByText('Sesión cancelada', { exact: true }).first()).toBeVisible({ timeout: 2_000 });
    }).toPass({ timeout: 45_000, intervals: [3_000] });
    await expect(trainerPage.getByText(/Mantenimiento de la sala/)).toBeVisible();
    await expect(trainerPage.getByRole('link', { name: /Avisos: \d+ sin leer/ })).toBeVisible();

    // La bandeja tiene sus preferencias, y los avisos importantes no se pueden apagar.
    await expect(trainerPage.getByRole('heading', { name: 'Qué avisos quiero recibir' })).toBeVisible();
    await expect(trainerPage.getByRole('checkbox', { name: /Clase cancelada/ })).toBeDisabled();
    await trainerPage.getByRole('button', { name: 'Marcar todo como leído' }).click();
    await expect(trainerPage.getByText('Todos los avisos quedaron como leídos.')).toBeVisible();
    await trainerPage.context().close();
  });
});
