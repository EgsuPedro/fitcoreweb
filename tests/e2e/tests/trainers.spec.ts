import { expect, test, type APIRequestContext, type Page } from '@playwright/test';
import { nav, platformCredentials, signIn } from './support';

/**
 * Entrenadores contra API + PostgreSQL reales: el propietario habilita a un entrenador y le asigna un alumno; el entrenador entra
 * y solo ve su propio trabajo (no el listado de alumnos del gimnasio) y escribe una observación que el propietario no puede leer.
 */
const run = Date.now().toString(36);
const slug = `trn-${run}`;
const ownerEmail = `dueno@${slug}.test`;
const ownerPassword = 'Dueno-Clave-12345';
const trainerEmail = `entrena@${slug}.test`;
const trainerPassword = 'Entrena-Clave-12345';

async function login(api: APIRequestContext, tenantSlug: string, email: string, password: string): Promise<string> {
  const response = await api.post('/api/v1/auth/login', { data: { tenantSlug, email, password, useCookie: false } });
  expect(response.ok(), await response.text()).toBeTruthy();
  return ((await response.json()) as { accessToken: string }).accessToken;
}

test.describe.serial('entrenadores', () => {
  let ownerPage: Page;
  let memberName: string;

  test.beforeAll(async ({ playwright, baseURL, browser }) => {
    const api = await playwright.request.newContext({ baseURL });
    const platform = platformCredentials();
    const platformToken = await login(api, 'platform', platform.email, platform.password);
    const tenant = await api.post('/api/v1/platform/tenants', {
      headers: { Authorization: `Bearer ${platformToken}` },
      data: { slug, name: `Gimnasio ${run}`, timeZone: 'America/Guayaquil', locale: 'es', ownerEmail, ownerFullName: 'Dueña Entrenadores', ownerPassword },
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
      data: { email: trainerEmail, fullName: 'Carla Entrenadora', password: trainerPassword, roleIds: [trainerRole.id], branchIds: [branchId] },
    });
    expect(user.ok(), await user.text()).toBeTruthy();

    memberName = 'Mario Asignado';
    const member = await api.post('/api/v1/members', {
      headers: auth,
      data: { branchId, firstName: 'Mario', lastName: 'Asignado', idType: 'Cedula', idNumber: '1712345675', phone: '0991234567' },
    });
    expect(member.ok(), await member.text()).toBeTruthy();
    await api.dispose();

    ownerPage = await (await browser.newContext({ baseURL, locale: 'es-EC' })).newPage();
    await signIn(ownerPage, slug, ownerEmail, ownerPassword);
    await expect(nav(ownerPage)).toBeVisible();
  });

  test.afterAll(async () => {
    await ownerPage.context().close();
  });

  test('el propietario habilita a un entrenador, define su disponibilidad y le asigna un alumno', async () => {
    const page = ownerPage;
    await nav(page).getByRole('link', { name: 'Entrenadores' }).click();
    await page.getByRole('button', { name: 'Habilitar entrenador' }).click();
    const dialog = page.getByRole('dialog');
    const option = await dialog.locator('[formControlName="userId"] option', { hasText: 'Carla Entrenadora' }).getAttribute('value');
    await dialog.locator('[formControlName="userId"]').selectOption(option!);
    await dialog.locator('[formControlName="specialties"]').fill('Fuerza, Movilidad, fuerza');
    await dialog.getByRole('button', { name: 'Guardar' }).click();
    await expect(page.getByText('Entrenador habilitado.')).toBeVisible();
    await expect(page.getByRole('row', { name: /Carla Entrenadora/ }).getByText('Movilidad')).toBeVisible();

    await page.getByRole('link', { name: 'Carla Entrenadora' }).first().click();
    await page.locator('[formControlName="start"]').fill('06:00');
    await page.locator('[formControlName="end"]').fill('10:00');
    await page.getByRole('button', { name: 'Agregar franja' }).click();
    await page.getByRole('button', { name: 'Guardar disponibilidad' }).click();
    await expect(page.getByText('Disponibilidad guardada.')).toBeVisible();

    await page.getByRole('button', { name: 'Asignar alumno' }).click();
    const assign = page.getByRole('dialog');
    await assign.getByRole('searchbox').fill('Mario');
    await assign.getByRole('option', { name: /Mario Asignado/ }).click();
    await assign.getByRole('button', { name: 'Asignar alumno' }).click();
    await expect(page.getByText('Alumno asignado.')).toBeVisible();
    await expect(page.getByRole('row', { name: new RegExp(memberName) })).toBeVisible();
  });

  test('el entrenador solo ve su trabajo, escribe una observación y el propietario no puede leerla', async ({ browser, baseURL }) => {
    const trainerPage = await (await browser.newContext({ baseURL, locale: 'es-EC' })).newPage();
    await signIn(trainerPage, slug, trainerEmail, trainerPassword);
    // Sin permiso de alumnos ni de asistencia: el menú del entrenador es mínimo y su ruta lo confirma.
    await expect(nav(trainerPage).getByRole('link', { name: 'Entrenadores' })).toBeVisible();
    for (const hidden of ['Alumnos', 'Asistencia', 'Caja', 'Cobros', 'Usuarios']) {
      await expect(nav(trainerPage).getByRole('link', { name: hidden })).toHaveCount(0);
    }
    await trainerPage.goto('/members');
    await expect(trainerPage).toHaveURL(/forbidden/);

    await trainerPage.goto('/trainers');
    await trainerPage.getByRole('link', { name: 'Carla Entrenadora' }).first().click();
    await expect(trainerPage.getByRole('row', { name: new RegExp(memberName) })).toBeVisible();
    await expect(trainerPage.getByRole('button', { name: 'Asignar alumno' })).toHaveCount(0);

    await trainerPage.getByRole('row', { name: new RegExp(memberName) }).getByRole('button', { name: 'Observaciones del entrenador' }).click();
    const dialog = trainerPage.getByRole('dialog');
    await dialog.getByLabel('Nueva observación').fill('Molestia leve en el hombro derecho; evitar press por encima de la cabeza.');
    await dialog.getByRole('button', { name: 'Guardar observación' }).click();
    await expect(trainerPage.getByText('Observación guardada.')).toBeVisible();
    await expect(dialog.getByText('Molestia leve en el hombro derecho')).toBeVisible();
    await trainerPage.context().close();

    // El propietario ve la ficha del alumno, pero no la sección de observaciones ni su contenido.
    await nav(ownerPage).getByRole('link', { name: 'Alumnos' }).click();
    await ownerPage.getByRole('link', { name: memberName }).first().click();
    await expect(ownerPage.getByRole('heading', { name: 'Entrenador del alumno' })).toBeVisible();
    await expect(ownerPage.getByRole('heading', { name: 'Observaciones del entrenador' })).toHaveCount(0);
    await expect(ownerPage.getByText('Molestia leve en el hombro')).toHaveCount(0);
  });
});
