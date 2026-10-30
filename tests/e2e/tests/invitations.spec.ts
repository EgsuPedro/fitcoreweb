import { expect, test, type Page } from '@playwright/test';
import { nav, platformCredentials, signIn } from './support';

/**
 * Invitaciones de personal contra API, Worker, PostgreSQL y Mailpit reales: el propietario invita desde su pantalla, el correo llega de verdad,
 * la persona acepta con el enlace y entra con la contraseña que eligió. Necesita el buzón de desarrollo en E2E_MAILPIT_URL (por defecto http://localhost:8025).
 */
const run = Date.now().toString(36);
const slug = `inv-${run}`;
const ownerEmail = `dueno@${slug}.test`;
const ownerPassword = 'Dueno-Clave-12345';
const guestEmail = `entrenadora@${slug}.test`;
const revokedEmail = `cancelado@${slug}.test`;
const mailpit = process.env['E2E_MAILPIT_URL'] ?? 'http://localhost:8025';

async function invitationToken(email: string): Promise<string> {
  for (let attempt = 0; attempt < 30; attempt++) {
    const found = await fetch(`${mailpit}/api/v1/search?query=${encodeURIComponent(`to:${email}`)}`);
    const list = (await found.json()) as { messages: { ID: string }[] };
    if (list.messages.length > 0) {
      const detail = (await (await fetch(`${mailpit}/api/v1/message/${list.messages[0].ID}`)).json()) as { Text: string };
      const match = /aceptar-invitacion\?token=([A-Za-z0-9_\-.%]+)/.exec(detail.Text);
      if (match) {
        return decodeURIComponent(match[1]);
      }
    }
    await new Promise((resolve) => setTimeout(resolve, 1000));
  }
  throw new Error('No llegó el correo de invitación a Mailpit.');
}

test.describe.serial('invitaciones de personal', () => {
  let page: Page;

  test.beforeAll(async ({ playwright, baseURL, browser }) => {
    const api = await playwright.request.newContext({ baseURL });
    const platform = platformCredentials();
    const login = await api.post('/api/v1/auth/login', { data: { tenantSlug: 'platform', email: platform.email, password: platform.password, useCookie: false } });
    const token = ((await login.json()) as { accessToken: string }).accessToken;
    const tenant = await api.post('/api/v1/platform/tenants', {
      headers: { Authorization: `Bearer ${token}` },
      data: { slug, name: `Gimnasio ${run}`, timeZone: 'America/Guayaquil', locale: 'es', ownerEmail, ownerFullName: 'Dueña Invitaciones', ownerPassword },
    });
    expect(tenant.ok(), await tenant.text()).toBeTruthy();
    await api.dispose();

    page = await (await browser.newContext({ baseURL, locale: 'es-EC' })).newPage();
    await signIn(page, slug, ownerEmail, ownerPassword);
    await expect(nav(page)).toBeVisible();
  });

  test.afterAll(async () => {
    await page.context().close();
  });

  async function invite(email: string): Promise<void> {
    await page.getByRole('button', { name: 'Invitar a una persona' }).click();
    await page.getByLabel('Correo').fill(email);
    await page.getByLabel('Rol').selectOption({ label: 'Entrenador' });
    await page.getByRole('button', { name: 'Enviar invitación' }).click();
    await expect(page.getByText('Invitación enviada.')).toBeVisible();
  }

  test('el propietario invita y la invitación aparece pendiente; sin rol no se envía', async () => {
    await nav(page).getByRole('link', { name: 'Invitaciones', exact: true }).click();
    await expect(page.getByRole('heading', { name: 'Invitaciones', level: 1 })).toBeVisible();

    await page.getByRole('button', { name: 'Invitar a una persona' }).click();
    await page.getByLabel('Correo').fill(guestEmail);
    await page.getByRole('button', { name: 'Enviar invitación' }).click();
    await expect(page.getByText('Revise el correo y el rol.')).toBeVisible();
    await page.getByRole('button', { name: 'Cancelar' }).click();

    await invite(guestEmail);
    const row = page.getByRole('row', { name: new RegExp(guestEmail) });
    await expect(row).toContainText('Pendiente');
    await expect(row).toContainText('Entrenador');
  });

  test('una segunda invitación al mismo correo se rechaza con un mensaje claro', async () => {
    await page.getByRole('button', { name: 'Invitar a una persona' }).click();
    await page.getByLabel('Correo').fill(guestEmail);
    await page.getByLabel('Rol').selectOption({ label: 'Entrenador' });
    await page.getByRole('button', { name: 'Enviar invitación' }).click();
    await expect(page.getByText('Ya hay una invitación pendiente para ese correo. Reenvíela o revóquela.')).toBeVisible();
    await page.getByRole('button', { name: 'Cancelar' }).click();
  });

  test('la persona recibe el correo, acepta con el enlace y entra con la contraseña que eligió', async ({ playwright, baseURL }) => {
    const token = await invitationToken(guestEmail);
    const api = await playwright.request.newContext({ baseURL });
    const accepted = await api.post('/api/v1/public/invitations/accept', { data: { token, fullName: 'Entrenadora Invitada', password: 'Clave-Invitada-2026' } });
    expect(accepted.ok(), await accepted.text()).toBeTruthy();
    await api.dispose();

    await page.reload();
    await expect(page.getByRole('row', { name: new RegExp(guestEmail) })).toContainText('Aceptada');

    const guest = await (await page.context().browser()!.newContext({ baseURL, locale: 'es-EC' })).newPage();
    await signIn(guest, slug, guestEmail, 'Clave-Invitada-2026');
    await expect(nav(guest)).toBeVisible();
    await expect(nav(guest).getByRole('link', { name: 'Invitaciones', exact: true })).toHaveCount(0); // un entrenador no invita
    await guest.context().close();
  });

  test('una invitación pendiente se puede revocar y su enlace deja de servir', async ({ playwright, baseURL }) => {
    await invite(revokedEmail);
    const token = await invitationToken(revokedEmail);
    await page.getByRole('row', { name: new RegExp(revokedEmail) }).getByRole('button', { name: 'Revocar' }).click();
    await page.getByRole('dialog').getByRole('button', { name: 'Revocar' }).click();
    await expect(page.getByText('Invitación revocada.')).toBeVisible();
    await expect(page.getByRole('row', { name: new RegExp(revokedEmail) })).toContainText('Revocada');

    const api = await playwright.request.newContext({ baseURL });
    const late = await api.post('/api/v1/public/invitations/accept', { data: { token, fullName: 'Tarde', password: 'Clave-Invitada-2026' } });
    expect(late.status()).toBe(422);
    await api.dispose();
  });
});
