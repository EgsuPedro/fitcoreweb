import { expect, test, type Page } from '@playwright/test';
import { nav, platformCredentials, signIn } from './support';

/**
 * Pantallas de cuenta contra API, Worker, PostgreSQL y Mailpit reales: registro de un gimnasio, confirmación del correo, resumen del propietario, recuperación de
 * contraseña e invitación de personal. Cada enlace sale del correo que de verdad envió el sistema (Mailpit en E2E_MAILPIT_URL, por defecto http://localhost:8025).
 */
const run = Date.now().toString(36);
const planCode = `cta-${run}`;
const gymName = `Gimnasio Cuenta ${run}`;
const slug = `gimnasio-cuenta-${run}`;
const ownerEmail = `dueno@${slug}.test`;
const ownerPassword = 'Dueno-Clave-12345';
const newPassword = 'Dueno-Nueva-67890';
const guestEmail = `entrenador@${slug}.test`;
const guestPassword = 'Invitado-Clave-123';
const mailpit = process.env['E2E_MAILPIT_URL'] ?? 'http://localhost:8025';

/** Primer enlace con «token=» del correo más reciente para ese destinatario que contenga la ruta pedida. */
async function linkFromEmail(to: string, path: string): Promise<string> {
  for (let attempt = 0; attempt < 40; attempt++) {
    const found = await fetch(`${mailpit}/api/v1/search?query=${encodeURIComponent(`to:${to}`)}`);
    const list = (await found.json()) as { messages: { ID: string }[] };
    for (const message of list.messages) {
      const detail = (await (await fetch(`${mailpit}/api/v1/message/${message.ID}`)).json()) as { Text: string };
      const match = new RegExp(`https?://[^\\s]*${path}\\?token=[A-Za-z0-9_\\-.%]+`).exec(detail.Text);
      if (match) {
        return match[0];
      }
    }
    await new Promise((resolve) => setTimeout(resolve, 1000));
  }
  throw new Error(`No llegó a Mailpit un correo para ${to} con ${path}.`);
}

/** El enlace del correo apunta a la dirección pública configurada (en desarrollo, localhost:8081); se abre contra el servidor de la prueba. */
const local = (link: string) => new URL(link).pathname + new URL(link).search;

async function logIn(page: Page, email: string, password: string): Promise<void> {
  await page.getByLabel('Correo electrónico').fill(email);
  await page.getByLabel('Contraseña').fill(password);
  await page.getByRole('button', { name: 'Iniciar sesión' }).click();
}

test.describe.serial('pantallas de cuenta', () => {
  let page: Page;

  test.beforeAll(async ({ playwright, baseURL, browser }) => {
    const api = await playwright.request.newContext({ baseURL });
    const platform = platformCredentials();
    const login = await api.post('/api/v1/auth/login', { data: { tenantSlug: 'platform', email: platform.email, password: platform.password, useCookie: false } });
    const headers = { Authorization: `Bearer ${((await login.json()) as { accessToken: string }).accessToken}` };
    const plan = await api.post('/api/v1/platform/plans', {
      headers,
      data: {
        code: planCode, name: `Plan Cuenta ${run}`, isActive: true, currency: 'USD', billingPeriod: 'Monthly', price: 30, trialDays: 14, features: ['members', 'branches'],
        limits: [{ code: 'branches', value: 2 }, { code: 'internal_users', value: 5 }, { code: 'members', value: null }, { code: 'storage_mb', value: 500 }],
      },
    });
    expect(plan.ok(), await plan.text()).toBeTruthy();
    const planId = ((await plan.json()) as { id: string }).id;
    expect((await api.patch(`/api/v1/platform/plans/${planId}/visibility`, { headers, data: { isPublic: true } })).ok()).toBeTruthy();
    await api.dispose();
    page = await (await browser.newContext({ baseURL, locale: 'es-EC' })).newPage();
  });

  test.afterAll(async () => {
    await page.context().close();
  });

  test('el registro valida, sugiere el identificador y avisa que se envió el correo', async () => {
    await page.goto(`/registro?plan=${planCode}`);
    await expect(page.getByRole('heading', { name: 'Crear mi gimnasio', level: 1 })).toBeVisible();
    await expect(page.locator('select[formcontrolname=planCode]')).toHaveValue(planCode);

    await page.getByRole('button', { name: 'Crear mi gimnasio' }).click();
    await expect(page.getByText('La contraseña no cumple los requisitos.')).toHaveCount(0); // vacía: solo «obligatorio»
    await expect(page.getByText('Debe aceptar los términos y la política de privacidad.')).toBeVisible();

    await page.locator('input[formControlName=ownerFullName]').fill('Dueña de Cuenta');
    await page.locator('input[formControlName=email]').fill(ownerEmail);
    await page.locator('input[formControlName=gymName]').fill(gymName);
    await expect(page.locator('input[formControlName=slug]')).toHaveValue(slug); // sugerido desde el nombre
    await page.locator('input[formControlName=password]').fill('corta');
    await page.getByLabel(/Acepto los/).check();
    await page.getByRole('button', { name: 'Crear mi gimnasio' }).click();
    await expect(page.getByText('La contraseña no cumple los requisitos.')).toBeVisible();

    await page.locator('input[formControlName=password]').fill(ownerPassword);
    await page.getByRole('button', { name: 'Crear mi gimnasio' }).click();
    await expect(page.getByRole('heading', { name: 'Revise su correo', level: 1 })).toBeVisible();
    await expect(page.getByText(ownerEmail)).toBeVisible();
    await expect(page.getByRole('button', { name: /Reenviar en \d+ s/ })).toBeDisabled();
  });

  test('un identificador ocupado se señala en su campo', async () => {
    await page.goto(`/registro?plan=${planCode}`);
    await page.locator('input[formControlName=ownerFullName]').fill('Otra Persona');
    await page.locator('input[formControlName=email]').fill(`otra@${slug}.test`);
    await page.locator('input[formControlName=gymName]').fill(gymName);
    await page.locator('input[formControlName=password]').fill(ownerPassword);
    await page.getByLabel(/Acepto los/).check();
    // Hasta confirmar el correo nadie tiene el identificador; se ocupa con la persona anterior ya confirmada (siguiente prueba) y aquí se verifica el formato.
    await page.locator('input[formControlName=slug]').fill('-mal-formato-');
    await page.getByRole('button', { name: 'Crear mi gimnasio' }).click();
    await expect(page.getByText('Use 3 a 40 caracteres: minúsculas, números y guiones.')).toBeVisible();
  });

  test('el enlace del correo crea el gimnasio, se quita de la barra de direcciones y no sirve dos veces', async () => {
    const link = await linkFromEmail(ownerEmail, '/verificar-correo');
    await page.goto(local(link));
    await expect(page.getByText('¡Su gimnasio está creado!')).toBeVisible();
    await expect(page.getByText(slug).first()).toBeVisible();
    expect(page.url()).not.toContain('token=');

    // El mismo enlace otra vez: ya se usó.
    await page.goto(local(link));
    await expect(page.getByText('El enlace no es válido o ya se usó. Solicite uno nuevo.')).toBeVisible();
    await expect(page.getByRole('button', { name: 'Reenviar el correo' })).toBeVisible();
  });

  test('el identificador ya confirmado no se puede volver a registrar', async () => {
    await page.goto(`/registro?plan=${planCode}`);
    await page.locator('input[formControlName=ownerFullName]').fill('Otra Persona');
    await page.locator('input[formControlName=email]').fill(`otra@${slug}.test`);
    await page.locator('input[formControlName=gymName]').fill(gymName);
    await page.locator('input[formControlName=password]').fill(ownerPassword);
    await page.getByLabel(/Acepto los/).check();
    await page.getByRole('button', { name: 'Crear mi gimnasio' }).click();
    await expect(page.getByText('Ese identificador ya está en uso. Elija otro.')).toBeVisible();
  });

  test('desde «iniciar sesión» el propietario entra con el identificador ya escrito y ve el resumen de su gimnasio', async () => {
    await page.goto(`/login?gimnasio=${slug}`);
    await expect(page.getByLabel('Gimnasio')).toHaveValue(slug);
    await logIn(page, ownerEmail, ownerPassword);
    await expect(nav(page)).toBeVisible();

    await expect(page.getByText('Su gimnasio está pendiente de configuración.')).toBeVisible();
    const panel = page.getByRole('region', { name: 'Resumen de su gimnasio' });
    await expect(panel.getByText('0 de 2 pasos obligatorios hechos')).toBeVisible();
    await expect(panel.getByText(`Plan Cuenta ${run}`)).toBeVisible();
    await expect(panel.getByText(/Prueba gratuita hasta/)).toBeVisible();
    await expect(panel.getByText('0 invitaciones pendientes')).toBeVisible();
  });

  test('«olvidé mi contraseña»: llega el correo, una clave débil no gasta el enlace y la nueva sirve', async () => {
    await page.getByRole('button', { name: 'Cerrar sesión' }).click();
    await expect(page).toHaveURL(/\/login/);
    await page.getByRole('link', { name: '¿Olvidó su contraseña?' }).click();
    await page.locator('input[formControlName=tenantSlug]').fill(slug);
    await page.locator('input[formControlName=email]').fill(ownerEmail);
    await page.getByRole('button', { name: 'Enviarme el enlace' }).click();
    await expect(page.getByText('Si los datos corresponden a una cuenta, enviamos un enlace a su correo.')).toBeVisible();

    const link = await linkFromEmail(ownerEmail, '/restablecer-clave');
    await page.goto(local(link));
    await expect(page.getByRole('heading', { name: 'Elegir una contraseña nueva' })).toBeVisible();
    expect(page.url()).not.toContain('token=');

    await page.locator('input[formControlName=password]').fill('debil');
    await page.locator('input[formControlName=confirm]').fill('otra');
    await page.getByRole('button', { name: 'Guardar contraseña' }).click();
    await expect(page.getByText('La contraseña no cumple los requisitos.')).toBeVisible();
    await expect(page.getByText('Las contraseñas no coinciden.')).toBeVisible();

    await page.locator('input[formControlName=password]').fill(newPassword);
    await page.locator('input[formControlName=confirm]').fill(newPassword);
    await page.getByRole('button', { name: 'Guardar contraseña' }).click();
    await expect(page.getByText(/su contraseña se cambió y se cerraron las sesiones abiertas/)).toBeVisible();

    await page.getByRole('link', { name: 'Iniciar sesión' }).first().click();
    await page.getByLabel('Gimnasio').fill(slug);
    await logIn(page, ownerEmail, ownerPassword);
    await expect(page.getByText('Credenciales inválidas').or(page.getByRole('alert')).first()).toBeVisible(); // la anterior ya no sirve
    await page.getByLabel('Contraseña').fill(newPassword);
    await page.getByRole('button', { name: 'Iniciar sesión' }).click();
    await expect(nav(page)).toBeVisible();
  });

  test('la invitación muestra el gimnasio y el rol, pide una clave válida y deja entrar a la persona', async ({ browser, baseURL }) => {
    await nav(page).getByRole('link', { name: 'Invitaciones', exact: true }).click();
    await page.getByRole('button', { name: 'Invitar a una persona' }).click();
    await page.locator('input[formControlName=email]').fill(guestEmail);
    await page.getByLabel('Rol').selectOption({ label: 'Entrenador' });
    await page.getByRole('button', { name: 'Enviar invitación' }).click();
    await expect(page.getByText('Invitación enviada.')).toBeVisible();

    const link = await linkFromEmail(guestEmail, '/aceptar-invitacion');
    const ctx = await browser.newContext({ baseURL, locale: 'es-EC' });
    const guest = await ctx.newPage();
    await guest.goto(local(link));
    await expect(guest.getByRole('heading', { name: 'Aceptar invitación', level: 1 })).toBeVisible();
    await expect(guest.getByText(`«${gymName}»`).first()).toBeVisible();
    await expect(guest.getByText('Entrenador').first()).toBeVisible();
    expect(guest.url()).not.toContain('token=');

    await guest.locator('input[formControlName=fullName]').fill('Entrenador Invitado');
    await guest.locator('input[formControlName=password]').fill('debil');
    await guest.locator('input[formControlName=confirm]').fill('debil');
    await guest.getByRole('button', { name: 'Crear mi cuenta' }).click();
    await expect(guest.getByText('La contraseña no cumple los requisitos.')).toBeVisible();

    await guest.locator('input[formControlName=password]').fill(guestPassword);
    await guest.locator('input[formControlName=confirm]').fill(guestPassword);
    await guest.getByRole('button', { name: 'Crear mi cuenta' }).click();
    await expect(guest.getByText('¡Listo! Su cuenta está creada.')).toBeVisible();

    await guest.locator('main').getByRole('link', { name: 'Iniciar sesión' }).click();
    await expect(guest.getByLabel('Gimnasio')).toHaveValue(slug);
    await logIn(guest, guestEmail, guestPassword);
    await expect(nav(guest)).toBeVisible();
    await expect(guest.getByRole('region', { name: 'Resumen de su gimnasio' })).toHaveCount(0); // un entrenador no administra el gimnasio

    // La invitación ya aceptada no se puede volver a usar.
    await guest.context().clearCookies();
    await guest.goto(local(link));
    await expect(guest.getByText(/La invitación no es válida, ya se usó o venció/)).toBeVisible();
    await ctx.close();
  });

  test('las pantallas de cuenta caben en un celular de 360 px', async ({ browser, baseURL }) => {
    const ctx = await browser.newContext({ baseURL, viewport: { width: 360, height: 740 }, locale: 'es-EC' });
    const phone = await ctx.newPage();
    for (const path of [`/registro?plan=${planCode}`, '/olvide-mi-clave', '/restablecer-clave', '/aceptar-invitacion', '/verificar-correo']) {
      await phone.goto(path);
      await phone.waitForLoadState('networkidle');
      const overflow = await phone.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
      expect(overflow, `${path} se sale ${overflow}px`).toBeLessThanOrEqual(1);
    }
    await ctx.close();
  });
});
