import { expect, test, type APIRequestContext, type Page } from '@playwright/test';
import { platformCredentials, signIn } from './support';

/** Vista de celular (la mayoría de las personas usa el sistema desde el teléfono): sin desplazamiento horizontal, barra superior completa y cuenta en el menú. */
const run = Date.now().toString(36);
const slug = `mov-${run}`;
const ownerEmail = `dueno@${slug}.test`;
const ownerPassword = 'Dueno-Clave-12345';

async function login(api: APIRequestContext, tenantSlug: string, email: string, password: string): Promise<string> {
  const response = await api.post('/api/v1/auth/login', { data: { tenantSlug, email, password, useCookie: false } });
  expect(response.ok(), await response.text()).toBeTruthy();
  return ((await response.json()) as { accessToken: string }).accessToken;
}

/** Cuánto se sale el contenido del ancho de la pantalla (0 = nada). */
const overflow = (page: Page) => page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);

for (const width of [360, 440]) {
  test.describe(`celular de ${width} px`, () => {
    test.use({ viewport: { width, height: 860 }, isMobile: true, hasTouch: true });

    let page: Page;

    test.beforeAll(async ({ playwright, baseURL, browser }) => {
      const api = await playwright.request.newContext({ baseURL });
      const platform = platformCredentials();
      const token = await login(api, 'platform', platform.email, platform.password);
      const tenantSlug = `${slug}-${width}`;
      const tenant = await api.post('/api/v1/platform/tenants', {
        headers: { Authorization: `Bearer ${token}` },
        data: { slug: tenantSlug, name: `Gimnasio con un nombre bastante largo ${run}`, timeZone: 'America/Guayaquil', locale: 'es', ownerEmail: `${width}-${ownerEmail}`, ownerFullName: 'Propietaria con Apellidos Muy Largos', ownerPassword },
      });
      expect(tenant.ok(), await tenant.text()).toBeTruthy();
      await api.dispose();

      page = await (await browser.newContext({ baseURL, locale: 'es-EC', viewport: { width, height: 860 }, isMobile: true, hasTouch: true })).newPage();
      await signIn(page, tenantSlug, `${width}-${ownerEmail}`, ownerPassword);
      await expect(page.getByRole('button', { name: 'Mostrar u ocultar el menú' })).toBeVisible();
    });

    test.afterAll(async () => {
      await page.context().close();
    });

    test('las pantallas principales no se salen del ancho y la barra superior cabe completa', async () => {
      for (const path of ['/', '/members', '/payments', '/analytics', '/branches', '/users', '/roles', '/my/subscription', '/branding']) {
        await page.goto(path);
        await page.waitForTimeout(400);
        expect(await overflow(page), `${path} se sale del ancho de ${width} px`).toBeLessThanOrEqual(1);
      }

      await page.goto('/');
      // Todo lo de la barra superior está dentro de la pantalla (nada cortado a la derecha).
      for (const locator of [page.getByRole('button', { name: 'Mostrar u ocultar el menú' }), page.locator('.lang-switch').getByRole('button', { name: 'ES', exact: true }), page.locator('.lang-switch').getByRole('button', { name: 'EN', exact: true })]) {
        const box = await locator.boundingBox();
        expect(box, 'botón de la barra superior').not.toBeNull();
        expect(box!.x + box!.width).toBeLessThanOrEqual(width);
      }
    });

    test('la cuenta (cambiar clave y cerrar sesión) está en el menú y funciona', async () => {
      await page.goto('/');
      // En celular no hay «Cerrar sesión» en la barra superior: vive al pie del menú.
      await expect(page.locator('.topbar').getByRole('button', { name: 'Cerrar sesión' })).toBeHidden();
      await page.getByRole('button', { name: 'Mostrar u ocultar el menú' }).click();
      const logout = page.locator('.sidebar-account').getByRole('button', { name: 'Cerrar sesión' });
      await logout.scrollIntoViewIfNeeded();
      await expect(page.locator('.sidebar-account').getByRole('link', { name: 'Mi contraseña' })).toBeVisible();
      await logout.click();
      await expect(page).toHaveURL(/\/login/);
    });
  });
}
