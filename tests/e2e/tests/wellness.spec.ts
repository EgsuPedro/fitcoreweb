import { expect, test, type APIRequestContext, type Page } from '@playwright/test';
import { nav, platformCredentials, signIn } from './support';

/**
 * Entrenamiento, nutrición y progreso contra API + PostgreSQL reales. El entrenador arma una rutina, se la asigna a su alumno y
 * registra evaluaciones y objetivos; el alumno registra su sesión, ve su evolución y gestiona su consentimiento y sus fotos;
 * la nutricionista arma un plan y el alumno registra su comida; y el propietario no ve nada de esto (datos sensibles).
 */
const run = Date.now().toString(36);
const slug = `wel-${run}`;
const ownerEmail = `dueno@${slug}.test`;
const ownerPassword = 'Dueno-Clave-12345';
const trainerEmail = `entrena@${slug}.test`;
const trainerPassword = 'Entrena-Clave-12345';
const nutriEmail = `nutri@${slug}.test`;
const nutriPassword = 'Nutri-Clave-12345';
const studentEmail = `alumno@${slug}.test`;
const studentPassword = 'Alumno-Clave-12345';

// PNG de 1x1 píxel: el navegador lo dibuja de verdad.
const PNG = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==', 'base64');

async function login(api: APIRequestContext, tenantSlug: string, email: string, password: string): Promise<string> {
  const response = await api.post('/api/v1/auth/login', { data: { tenantSlug, email, password, useCookie: false } });
  expect(response.ok(), await response.text()).toBeTruthy();
  return ((await response.json()) as { accessToken: string }).accessToken;
}

test.describe.serial('bienestar', () => {
  let ownerPage: Page;
  let trainerPage: Page;
  let studentPage: Page;
  let nutriPage: Page;
  const memberName = 'Diana Wellness';

  test.beforeAll(async ({ playwright, baseURL, browser }) => {
    const api = await playwright.request.newContext({ baseURL });
    const platform = platformCredentials();
    const platformToken = await login(api, 'platform', platform.email, platform.password);
    const tenant = await api.post('/api/v1/platform/tenants', {
      headers: { Authorization: `Bearer ${platformToken}` },
      data: { slug, name: `Gimnasio ${run}`, timeZone: 'America/Guayaquil', locale: 'es', ownerEmail, ownerFullName: 'Dueña Bienestar', ownerPassword },
    });
    expect(tenant.ok(), await tenant.text()).toBeTruthy();

    const auth = { Authorization: `Bearer ${await login(api, slug, ownerEmail, ownerPassword)}` };
    const branch = await api.post('/api/v1/branches', { headers: auth, data: { code: 'UIO', name: 'Quito Centro', timeZone: 'America/Guayaquil', sriEstablishmentCode: '001', address: null } });
    expect(branch.ok(), await branch.text()).toBeTruthy();
    const branchId = ((await branch.json()) as { id: string }).id;

    const roles = (await (await api.get('/api/v1/roles', { headers: auth })).json()) as { id: string; name: string }[];
    const roleId = (name: string) => roles.find((r) => r.name === name)!.id;
    const trainerUser = await api.post('/api/v1/users', {
      headers: auth,
      data: { email: trainerEmail, fullName: 'Carla Entrenadora', password: trainerPassword, roleIds: [roleId('Entrenador')], branchIds: [branchId] },
    });
    expect(trainerUser.ok(), await trainerUser.text()).toBeTruthy();
    const nutriUser = await api.post('/api/v1/users', {
      headers: auth,
      data: { email: nutriEmail, fullName: 'Nora Nutricionista', password: nutriPassword, roleIds: [roleId('Nutricionista')], branchIds: [branchId] },
    });
    expect(nutriUser.ok(), await nutriUser.text()).toBeTruthy();

    const profile = await api.post('/api/v1/trainers', { headers: auth, data: { userId: ((await trainerUser.json()) as { id: string }).id } });
    expect(profile.ok(), await profile.text()).toBeTruthy();

    const member = await api.post('/api/v1/members', {
      headers: auth,
      data: { branchId, firstName: 'Diana', lastName: 'Wellness', idType: 'Cedula', idNumber: '1712345675', email: studentEmail, phone: '0991234567', birthDate: '1990-05-10' },
    });
    expect(member.ok(), await member.text()).toBeTruthy();
    const memberId = ((await member.json()) as { id: string }).id;
    const assigned = await api.post(`/api/v1/trainers/${((await profile.json()) as { id: string }).id}/members`, { headers: auth, data: { memberId } });
    expect(assigned.ok(), await assigned.text()).toBeTruthy();

    // El acceso del alumno: clave temporal y cambio obligatorio resueltos por API (esos pasos ya los cubre otra prueba).
    const access = await api.post(`/api/v1/members/${memberId}/access`, { headers: auth });
    const temporary = ((await access.json()) as { temporaryPassword: string }).temporaryPassword;
    const tempToken = await login(api, slug, studentEmail, temporary);
    const changed = await api.post('/api/v1/auth/change-password', { headers: { Authorization: `Bearer ${tempToken}` }, data: { currentPassword: temporary, newPassword: studentPassword } });
    expect(changed.ok(), await changed.text()).toBeTruthy();
    await api.dispose();

    const open = async (email: string, password: string): Promise<Page> => {
      const page = await (await browser.newContext({ baseURL, locale: 'es-EC' })).newPage();
      await signIn(page, slug, email, password);
      await expect(nav(page)).toBeVisible();
      return page;
    };
    ownerPage = await open(ownerEmail, ownerPassword);
    trainerPage = await open(trainerEmail, trainerPassword);
    nutriPage = await open(nutriEmail, nutriPassword);
    studentPage = await open(studentEmail, studentPassword);
  });

  test.afterAll(async () => {
    for (const page of [ownerPage, trainerPage, nutriPage, studentPage]) {
      await page.context().close();
    }
  });

  test('el entrenador arma una rutina, se la asigna a su alumno y registra evaluación y objetivo', async () => {
    const page = trainerPage;
    await nav(page).getByRole('link', { name: 'Rutinas' }).click();
    await page.getByRole('button', { name: 'Nueva rutina' }).click();
    await page.getByLabel('Nombre', { exact: true }).first().fill('Fuerza base');
    await page.getByLabel('Objetivo', { exact: true }).fill('Ganar fuerza general');
    await page.getByLabel('Nombre', { exact: true }).nth(1).fill('Día A');
    await page.getByRole('button', { name: 'Agregar ejercicio' }).click();
    await page.getByLabel('Ejercicio', { exact: true }).fill('Sentadilla con barra');
    await page.getByLabel('Ejercicio', { exact: true }).blur();
    await page.getByLabel('Series', { exact: true }).fill('3');
    await page.getByLabel('Repeticiones', { exact: true }).fill('10');
    await page.getByLabel('Carga sugerida (kg)').fill('40');
    await page.getByRole('button', { name: 'Guardar' }).click();
    await expect(page.getByText('Guardado.')).toBeVisible();
    await expect(page.getByRole('row', { name: /Fuerza base/ })).toBeVisible();

    // El entrenador no tiene permiso de leer la ficha del alumno: llega a su seguimiento desde su propia pantalla.
    await nav(page).getByRole('link', { name: 'Entrenadores' }).click();
    await page.getByRole('link', { name: 'Carla Entrenadora' }).first().click();
    await page.getByRole('link', { name: 'Seguimiento' }).click();
    await expect(page.getByRole('heading', { name: memberName })).toBeVisible();

    await page.getByRole('button', { name: 'Asignar rutina' }).click();
    const assign = page.getByRole('dialog');
    await assign.locator('select').selectOption({ label: 'Fuerza base (v1)' });
    await assign.getByRole('button', { name: 'Asignar rutina' }).click();
    await expect(page.getByText('Rutina asignada.')).toBeVisible();
    await expect(page.getByRole('heading', { name: 'Rutina vigente' })).toBeVisible();
    await expect(page.locator('summary strong', { hasText: 'Sentadilla con barra' })).toBeVisible();

    await page.getByRole('tab', { name: 'Evaluaciones' }).click();
    await page.getByLabel('Peso (kg)').fill('90');
    await page.getByLabel('Estatura (cm)').fill('175');
    await page.getByRole('button', { name: 'Guardar evaluación' }).click();
    await expect(page.getByText('Evaluación guardada.')).toBeVisible();
    await expect(page.getByRole('row', { name: /29,4/ })).toBeVisible(); // IMC 90 / 1,75²

    await page.getByRole('tab', { name: 'Objetivos' }).click();
    await page.getByRole('button', { name: 'Nuevo objetivo' }).click();
    const goal = page.getByRole('dialog');
    await goal.getByLabel('Título').fill('Bajar a 80 kg');
    await goal.getByLabel('Meta', { exact: true }).fill('80');
    await goal.getByRole('button', { name: 'Guardar' }).click();
    await expect(page.getByText('Objetivo creado.')).toBeVisible();
    await expect(page.getByRole('progressbar', { name: 'Bajar a 80 kg' })).toBeVisible();
  });

  test('el alumno registra su sesión, ve su evolución y gestiona su consentimiento y sus fotos', async () => {
    const page = studentPage;
    await page.goto('/');
    await expect(page.getByRole('heading', { name: 'Mi entrenamiento y progreso' })).toBeVisible();
    await expect(page.locator('summary strong', { hasText: 'Sentadilla con barra' })).toBeVisible();

    await page.getByRole('button', { name: 'Registrar sesión' }).click();
    const dialog = page.getByRole('dialog');
    await dialog.getByRole('checkbox', { name: /Sentadilla con barra/ }).check();
    await dialog.getByLabel('Repeticiones').fill('10');
    await dialog.getByLabel('Carga (kg)').fill('40');
    await dialog.getByRole('button', { name: 'Guardar sesión' }).click();
    await expect(page.getByText('Sesión registrada.')).toBeVisible();
    await expect(page.getByText(/1\/1/)).toBeVisible();

    await page.getByRole('tab', { name: 'Evaluaciones' }).click();
    await expect(page.getByRole('img', { name: /Evolución de Peso/ })).toBeVisible();
    // El alumno solo lee: no ve el formulario de nueva evaluación.
    await expect(page.getByRole('button', { name: 'Guardar evaluación' })).toHaveCount(0);

    await page.getByRole('tab', { name: 'Objetivos' }).click();
    await expect(page.getByRole('progressbar', { name: 'Bajar a 80 kg' })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Nuevo objetivo' })).toHaveCount(0);

    await page.getByRole('tab', { name: 'Fotos' }).click();
    await expect(page.getByText('Sin consentimiento')).toBeVisible();
    await expect(page.getByRole('button', { name: 'Subir foto' })).toHaveCount(0);
    // Sin aceptar la información no se puede dar el consentimiento.
    await expect(page.getByRole('button', { name: 'Dar consentimiento' })).toBeDisabled();
    await page.getByRole('checkbox', { name: /doy mi consentimiento/ }).check();
    await page.getByRole('button', { name: 'Dar consentimiento' }).click();
    await expect(page.getByText('Consentimiento registrado.')).toBeVisible();

    await page.locator('input[type="file"]').setInputFiles({ name: 'progreso.png', mimeType: 'image/png', buffer: PNG });
    await page.getByRole('button', { name: 'Subir foto' }).click();
    await expect(page.getByText('Foto subida.')).toBeVisible();
    const photo = page.locator('.photo-grid img');
    await expect(photo).toHaveCount(1);
    // La imagen se muestra desde un blob local (nunca una URL pública del servidor).
    await expect(photo).toHaveAttribute('src', /^blob:/);

    // El entrenador asignado la ve; el propietario, ni la sección.
    await trainerPage.reload();
    await trainerPage.getByRole('tab', { name: 'Fotos' }).click();
    await expect(trainerPage.locator('.photo-grid img')).toHaveCount(1);

    await page.getByRole('button', { name: 'Retirar consentimiento' }).click();
    await expect(page.getByRole('dialog')).toContainText('Se borrarán de forma definitiva las 1 fotos');
    await page.getByRole('dialog').getByRole('button', { name: 'Retirar y borrar fotos' }).click();
    await expect(page.getByText('Consentimiento retirado y fotos borradas.')).toBeVisible();
    await expect(page.locator('.photo-grid img')).toHaveCount(0);
    await expect(page.getByText('Retirado', { exact: true })).toBeVisible();
  });

  test('la nutricionista arma un plan, se lo asigna y el alumno registra su comida', async () => {
    const page = nutriPage;
    await nav(page).getByRole('link', { name: 'Planes nutricionales' }).click();
    await page.getByRole('button', { name: 'Nuevo plan' }).click();
    await page.getByLabel('Nombre', { exact: true }).first().fill('Plan equilibrado');
    await page.getByLabel('Objetivo', { exact: true }).fill('Mantenimiento');
    await page.getByLabel('Calorías objetivo (kcal)').fill('2000');
    await page.getByLabel('Nombre', { exact: true }).nth(1).fill('Desayuno');
    await page.getByLabel('Alimento').fill('Avena');
    await page.getByLabel('Cantidad').fill('50');
    await page.getByLabel('Unidad').fill('g');
    await page.getByRole('button', { name: 'Guardar' }).click();
    await expect(page.getByText('Guardado.')).toBeVisible();

    await nav(page).getByRole('link', { name: 'Alumnos' }).click();
    await page.getByRole('link', { name: memberName }).first().click();
    await page.getByRole('button', { name: 'Editar alergias y restricciones' }).click();
    const profile = page.getByRole('dialog');
    await profile.locator('textarea').first().fill('Maní');
    await profile.getByRole('button', { name: 'Guardar' }).click();
    await expect(page.getByText('Alergias y restricciones guardadas.')).toBeVisible();
    await expect(page.getByText('Maní')).toBeVisible();

    await page.getByRole('button', { name: 'Asignar plan' }).click();
    const assign = page.getByRole('dialog');
    await assign.locator('select').selectOption({ label: 'Plan equilibrado (v1)' });
    await assign.getByRole('button', { name: 'Asignar plan' }).click();
    await expect(page.getByText('Plan asignado.')).toBeVisible();
    // La nutricionista no ve entrenamiento, evaluaciones ni fotos del alumno.
    await expect(page.getByRole('tab', { name: 'Fotos' })).toHaveCount(0);
    await expect(page.getByRole('tab', { name: 'Evaluaciones' })).toHaveCount(0);

    const student = studentPage;
    await student.goto('/');
    await student.getByRole('tab', { name: 'Nutrición' }).click();
    await expect(student.getByText('Maní')).toBeVisible();
    await expect(student.getByText('Avena')).toBeVisible();
    await student.getByRole('button', { name: 'Cumplida' }).click();
    await expect(student.getByText('Registro guardado.')).toBeVisible();
    await expect(student.getByText('Cumplida', { exact: true })).toBeVisible();
  });

  test('el propietario no ve nutrición, evaluaciones, objetivos ni fotos: son datos sensibles', async () => {
    const page = ownerPage;
    await nav(page).getByRole('link', { name: 'Alumnos' }).click();
    await page.getByRole('link', { name: memberName }).first().click();
    await expect(page.getByRole('heading', { name: 'Entrenador del alumno' })).toBeVisible();
    // Las rutinas no son un dato sensible: el propietario las ve. Nutrición, evaluaciones, objetivos y fotos, no.
    await expect(page.getByRole('tab')).toHaveText(['Entrenamiento']);
    await expect(page.getByText('Maní')).toHaveCount(0);
    await expect(nav(page).getByRole('link', { name: 'Planes nutricionales' })).toHaveCount(0);
  });
});
