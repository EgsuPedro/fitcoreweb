import { expect, test, type APIRequestContext, type Page } from '@playwright/test';
import { nav, platformCredentials, signIn } from './support';

/**
 * Flujo comercial completo contra API + PostgreSQL reales: alumno → caja → inscripción → cobro en efectivo → asistencia → cierre de caja.
 * Los datos de apoyo (gimnasio, sucursal, plan y caja) se crean por API; lo que se verifica se hace desde la interfaz.
 */
const run = Date.now().toString(36);
const slug = `com-${run}`;
const ownerEmail = `dueno@${slug}.test`;
const ownerPassword = 'Dueno-Clave-12345';
const studentEmail = `lucia@${slug}.test`;
const STUDENT_NEW_PASSWORD = 'Nueva-Clave-2026x';
const STAFF = { email: `recep@${slug}.test`, password: 'Recep-Clave-12345' };

async function login(api: APIRequestContext, tenantSlug: string, email: string, password: string): Promise<string> {
  const response = await api.post('/api/v1/auth/login', { data: { tenantSlug, email, password, useCookie: false } });
  expect(response.ok(), await response.text()).toBeTruthy();
  return ((await response.json()) as { accessToken: string }).accessToken;
}

test.describe.serial('flujo comercial', () => {
  // Una sola sesión del propietario para todo el flujo: iniciar sesión en cada prueba agotaría el límite de intentos de acceso por minuto.
  let ownerPage: Page;

  test.beforeAll(async ({ playwright, baseURL, browser }) => {
    const api = await playwright.request.newContext({ baseURL });
    const platform = platformCredentials();
    const platformToken = await login(api, 'platform', platform.email, platform.password);
    const tenant = await api.post('/api/v1/platform/tenants', {
      headers: { Authorization: `Bearer ${platformToken}` },
      data: { slug, name: `Gimnasio ${run}`, timeZone: 'America/Guayaquil', locale: 'es', ownerEmail, ownerFullName: 'Dueña Comercial', ownerPassword },
    });
    expect(tenant.ok(), await tenant.text()).toBeTruthy();

    const token = await login(api, slug, ownerEmail, ownerPassword);
    const auth = { Authorization: `Bearer ${token}` };
    const branch = await api.post('/api/v1/branches', { headers: auth, data: { code: 'UIO', name: 'Quito Centro', timeZone: 'America/Guayaquil', sriEstablishmentCode: '001', address: null } });
    expect(branch.ok(), await branch.text()).toBeTruthy();
    const branchId = ((await branch.json()) as { id: string }).id;

    const plan = await api.post('/api/v1/membership-plans', {
      headers: auth,
      data: {
        name: 'Mensual E2E', description: null, price: 34.5, durationValue: 1, durationUnit: 'Month', allowedAccess: null, restrictions: null,
        commercialTerms: null, taxKind: 'General', isActive: true, validFrom: null, validUntil: null, allowFreeze: true, maxFreezeDays: 10, branchIds: [],
      },
    });
    expect(plan.ok(), await plan.text()).toBeTruthy();

    const register = await api.post('/api/v1/cash-registers', { headers: auth, data: { branchId, name: 'Caja E2E', allowMultipleOpenSessions: false } });
    expect(register.ok(), await register.text()).toBeTruthy();
    await api.dispose();

    ownerPage = await (await browser.newContext({ baseURL, locale: 'es-EC' })).newPage();
    await signIn(ownerPage, slug, ownerEmail, ownerPassword);
    await expect(nav(ownerPage)).toBeVisible();
  });

  test.afterAll(async () => {
    await ownerPage.context().close();
  });

  test('registrar un alumno valida la cédula antes de enviar y guarda con la API real', async () => {
    const page = ownerPage;
    await nav(page).getByRole('link', { name: 'Alumnos', exact: true }).click();
    await page.getByRole('button', { name: 'Nuevo alumno' }).click();

    const dialog = page.getByRole('dialog');
    await dialog.locator('[formControlName="firstName"]').fill('Lucía');
    await dialog.locator('[formControlName="lastName"]').fill('Paredes');
    await dialog.locator('[formControlName="phone"]').fill('0991234567');
    await dialog.locator('[formControlName="email"]').fill(studentEmail);
    await dialog.locator('[formControlName="idNumber"]').fill('1712345670');
    await dialog.getByRole('button', { name: 'Guardar' }).click();
    // Cédula con dígito verificador inválido: no llega a la API y el diálogo sigue abierto.
    await expect(dialog).toBeVisible();

    await dialog.locator('[formControlName="idNumber"]').fill('1712345675');
    await dialog.getByRole('button', { name: 'Guardar' }).click();
    await expect(page.getByText('Alumno guardado.')).toBeVisible();
    await expect(page.getByRole('row', { name: /Lucía Paredes/ })).toBeVisible();
  });

  test('abrir la caja, inscribir al alumno y cobrar en efectivo deja el saldo en cero', async () => {
    const page = ownerPage;

    await nav(page).getByRole('link', { name: 'Caja' }).click();
    await page.getByRole('row', { name: /Caja E2E/ }).getByRole('button', { name: 'Ver caja' }).click();
    await page.getByRole('button', { name: 'Abrir caja' }).click();
    const openDialog = page.getByRole('dialog');
    await openDialog.locator('[formControlName="amount"]').fill('10');
    await openDialog.getByRole('button', { name: 'Abrir caja' }).click();
    await expect(page.getByText('Caja abierta.')).toBeVisible();

    await nav(page).getByRole('link', { name: 'Alumnos', exact: true }).click();
    await page.getByRole('link', { name: 'Lucía Paredes' }).first().click();
    await page.getByRole('button', { name: 'Inscribir alumno' }).click();
    const enroll = page.getByRole('dialog');
    const planValue = await enroll.locator('[formControlName="planId"] option', { hasText: 'Mensual E2E' }).getAttribute('value');
    await enroll.locator('[formControlName="planId"]').selectOption(planValue!);
    await enroll.getByRole('button', { name: 'Inscribir alumno' }).click();
    await expect(page.getByText('Alumno inscrito.')).toBeVisible();

    // La membresía nace pendiente de pago, con el saldo completo ($34,50 con IVA incluido).
    const membership = page.getByRole('row', { name: /Mensual E2E/ });
    await expect(membership.getByText('Pendiente de pago')).toBeVisible();
    await expect(membership.getByText('$34,50').first()).toBeVisible();
    await membership.getByRole('link', { name: 'Ver' }).click();
    await expect(page.getByText('Pendiente de pago').first()).toBeVisible();
    await page.getByRole('button', { name: 'Registrar pago' }).click();

    const pay = page.getByRole('dialog');
    await pay.locator('[formControlName="method"]').selectOption('Cash');
    await pay.getByRole('checkbox').first().check();
    await pay.getByRole('button', { name: 'Revisar y confirmar' }).click();
    await expect(pay.getByText('¿Registrar este pago?')).toBeVisible();
    await pay.getByRole('button', { name: 'Pagar' }).click();
    await expect(page.getByText(/Pago registrado\. Recibo RC-/)).toBeVisible();

    // Con el primer pago confirmado la membresía se activa y ya no queda saldo.
    await page.reload();
    await expect(page.getByText('Activa').first()).toBeVisible();
  });

  test('asistencia: valida el acceso, registra la entrada y el alumno aparece dentro', async () => {
    const page = ownerPage;
    await nav(page).getByRole('link', { name: 'Alumnos', exact: true }).click();
    const code = (await page.getByRole('row', { name: /Lucía Paredes/ }).locator('strong').first().innerText()).trim();

    await nav(page).getByRole('link', { name: 'Asistencia' }).click();
    await page.getByLabel('Código del alumno o código QR').fill(code);
    await page.getByRole('button', { name: 'Validar acceso' }).click();
    await expect(page.getByText('Puede entrar')).toBeVisible();
    await page.getByRole('button', { name: 'Registrar entrada' }).click();
    await expect(page.getByText('Entrada registrada: Lucía Paredes.')).toBeVisible();
    await expect(page.getByRole('heading', { name: /Dentro ahora \(1\)/ })).toBeVisible();

    await page.getByLabel('Código del alumno o código QR').fill(code);
    await page.getByRole('button', { name: 'Registrar salida' }).click();
    await expect(page.getByText('Salida registrada: Lucía Paredes.')).toBeVisible();
    await expect(page.getByRole('heading', { name: /Dentro ahora \(0\)/ })).toBeVisible();
  });

  test('cerrar la caja pide revisar y confirmar en un modal propio, y el cierre queda en el historial', async ({ page }) => {
    // Cualquier diálogo nativo del navegador sería un fallo: la confirmación debe ser un modal de la aplicación.
    page.on('dialog', (native) => {
      throw new Error(`Diálogo nativo inesperado: ${native.message()}`);
    });

    await signIn(page, slug, ownerEmail, ownerPassword);
    await nav(page).getByRole('link', { name: 'Caja' }).click();
    await page.getByRole('row', { name: /Caja E2E/ }).getByRole('button', { name: 'Ver caja' }).click();
    await page.getByRole('button', { name: 'Cerrar caja' }).click();

    const close = page.getByRole('dialog');
    // Apertura 10 + cobro en efectivo 34,50 = 44,50 esperados.
    await close.locator('[formControlName="counted"]').fill('44.5');
    await expect(close.getByText('Cuadra exacto')).toBeVisible();
    await close.getByRole('button', { name: 'Revisar y confirmar' }).click();
    await expect(close.getByText(/¿Cerrar la caja con/)).toBeVisible();
    await close.getByRole('button', { name: 'Volver' }).click();
    await expect(close.locator('[formControlName="counted"]')).toBeVisible();
    await close.getByRole('button', { name: 'Revisar y confirmar' }).click();
    await close.getByRole('button', { name: 'Cerrar caja' }).click();

    await expect(page.getByText('Caja cerrada.')).toBeVisible();
    await expect(page.getByRole('heading', { name: 'Cierres anteriores' })).toBeVisible();
    await expect(page.getByRole('row', { name: /Cerrada/ })).toBeVisible();
  });

  test('avisos de WhatsApp: se configuran, el alumno da su consentimiento y puede revocarlo', async () => {
    const page = ownerPage;
    await nav(page).getByRole('link', { name: 'Notificaciones' }).click();
    await expect(page.getByText(/Modo de prueba/)).toBeVisible();

    // Un teléfono inválido lo rechaza el servidor y el aviso de éxito no aparece.
    await page.getByLabel('Teléfonos del personal que recibe el aviso previo').fill('abc');
    await page.getByRole('button', { name: 'Guardar' }).click();
    await expect(page.getByRole('alert')).toContainText('Revise los datos ingresados');
    await expect(page.getByText('Configuración de avisos guardada.')).toHaveCount(0);

    await page.getByLabel('Activar los avisos por vencimiento').check();
    await page.getByLabel('Teléfonos del personal que recibe el aviso previo').fill('0987654321');
    await page.getByRole('button', { name: 'Guardar' }).click();
    await expect(page.getByText('Configuración de avisos guardada.')).toBeVisible();
    await page.reload();
    await expect(page.getByLabel('Activar los avisos por vencimiento')).toBeChecked();
    await expect(page.getByLabel('Teléfonos del personal que recibe el aviso previo')).toHaveValue('+593987654321');
    await expect(page.getByText('Todavía no se ha generado ningún aviso.')).toBeVisible();

    await nav(page).getByRole('link', { name: 'Alumnos', exact: true }).click();
    await page.getByRole('link', { name: 'Lucía Paredes' }).first().click();
    await expect(page.getByText(/no ha autorizado recibir mensajes/)).toBeVisible();
    await page.getByRole('button', { name: 'Registrar autorización' }).click();
    await page.getByRole('dialog').getByRole('button', { name: 'Registrar autorización' }).click();
    await expect(page.getByText('Autorización registrada.')).toBeVisible();
    await expect(page.getByText(/autorizó recibir mensajes de WhatsApp/)).toBeVisible();

    await page.getByRole('button', { name: 'Revocar autorización' }).click();
    await page.getByRole('dialog').getByRole('button', { name: 'Revocar autorización' }).click();
    await expect(page.getByText('Autorización revocada.')).toBeVisible();
    await expect(page.getByText(/no ha autorizado recibir mensajes/)).toBeVisible();
  });

  test('acceso del alumno: clave temporal, cambio obligatorio y portal con solo sus datos', async ({ browser, baseURL }) => {
    const page = ownerPage;
    await nav(page).getByRole('link', { name: 'Alumnos', exact: true }).click();
    await page.getByRole('link', { name: 'Lucía Paredes' }).first().click();
    await page.getByRole('button', { name: 'Dar acceso' }).click();
    await page.getByRole('dialog').getByRole('button', { name: 'Dar acceso' }).click();

    const credentials = page.getByRole('dialog');
    await expect(credentials.getByText(studentEmail)).toBeVisible();
    const temporary = (await credentials.locator('.temp-password').innerText()).trim();
    expect(temporary).toHaveLength(14);
    await credentials.getByRole('button', { name: 'Cerrar' }).last().click();
    await expect(page.getByText('El alumno tiene acceso a su portal con su correo.')).toBeVisible();

    // El alumno entra con la clave temporal y solo puede cambiarla.
    const studentPage = await (await browser.newContext({ baseURL, locale: 'es-EC' })).newPage();
    await signIn(studentPage, slug, studentEmail, temporary);
    await expect(studentPage).toHaveURL(/change-password/);
    await studentPage.goto('/members');
    await expect(studentPage).toHaveURL(/change-password/);

    await studentPage.getByLabel('Clave temporal').fill(temporary);
    await studentPage.locator('[formControlName="newPassword"]').fill(STUDENT_NEW_PASSWORD);
    await studentPage.getByLabel('Repita la contraseña nueva').fill('otra-distinta-123');
    await studentPage.getByRole('button', { name: 'Cambiar contraseña' }).click();
    await expect(studentPage.getByText('Las contraseñas no coinciden.')).toBeVisible();
    await studentPage.getByLabel('Repita la contraseña nueva').fill(STUDENT_NEW_PASSWORD);
    await studentPage.getByRole('button', { name: 'Cambiar contraseña' }).click();
    await expect(studentPage.getByText('Contraseña cambiada. Inicie sesión con la nueva.')).toBeVisible();

    // Con la clave nueva ve su portal, sin ningún elemento de administración, y las rutas del personal lo bloquean.
    await signIn(studentPage, slug, studentEmail, STUDENT_NEW_PASSWORD);
    await expect(studentPage.getByRole('heading', { name: 'Hola, Lucía' })).toBeVisible();
    await expect(studentPage.getByText('Mensual E2E')).toBeVisible();
    for (const hidden of ['Alumnos', 'Caja', 'Cobros', 'Usuarios', 'Auditoría']) {
      await expect(nav(studentPage).getByRole('link', { name: hidden })).toHaveCount(0);
    }
    await studentPage.goto('/members');
    await expect(studentPage).toHaveURL(/forbidden/);
    await studentPage.context().close();
  });

  test('un usuario sin permisos comerciales no ve las pantallas ni puede abrir sus rutas', async ({ page, playwright, baseURL }) => {
    const api = await playwright.request.newContext({ baseURL });
    const token = await login(api, slug, ownerEmail, ownerPassword);
    const auth = { Authorization: `Bearer ${token}` };
    const role = await api.post('/api/v1/roles', { headers: auth, data: { name: 'Solo lectura E2E', description: null, grantsAllBranches: true, permissions: ['members.read'] } });
    expect(role.ok(), await role.text()).toBeTruthy();
    const roleId = ((await role.json()) as { id: string }).id;
    const user = await api.post('/api/v1/users', {
      headers: auth,
      data: { email: STAFF.email, fullName: 'Recepción Limitada', password: STAFF.password, roleIds: [roleId], branchIds: [] },
    });
    expect(user.ok(), await user.text()).toBeTruthy();
    await api.dispose();

    await signIn(page, slug, STAFF.email, STAFF.password);
    await expect(nav(page).getByRole('link', { name: 'Alumnos', exact: true })).toBeVisible();
    for (const hidden of ['Caja', 'Cobros', 'Cartera', 'Asistencia', 'Planes', 'Membresías', 'Notificaciones']) {
      await expect(nav(page).getByRole('link', { name: hidden })).toHaveCount(0);
    }
    await page.goto('/cash');
    await expect(page).toHaveURL(/forbidden/);
    // Sin members.create no se ofrece el botón de alta.
    await page.goto('/members');
    await expect(page.getByRole('button', { name: 'Nuevo alumno' })).toHaveCount(0);
  });
});
