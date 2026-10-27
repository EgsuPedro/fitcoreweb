import { expect, test } from '@playwright/test';
import { nav, platformCredentials, signIn, signOut } from './support';

const run = Date.now().toString(36);
const slug = `e2e-${run}`;
const ownerEmail = `dueno@${slug}.test`;
const ownerPassword = 'Dueno-Clave-12345';
const staffEmail = `staff@${slug}.test`;
const staffPassword = 'Staff-Clave-12345';

test.describe('acceso', () => {
  test('la hoja de estilos se aplica y la política de seguridad no bloquea nada', async ({ page }) => {
    const violations: string[] = [];
    page.on('console', (message) => {
      if (/content security policy/i.test(message.text())) violations.push(message.text());
    });

    await page.goto('/login');
    const submit = page.getByRole('button', { name: 'Iniciar sesión' });
    await expect(submit).toBeVisible();

    // Un botón sin estilos tendría el fondo gris del navegador; con estilos usa el color de acento.
    const background = await submit.evaluate((el) => getComputedStyle(el).backgroundColor);
    const accent = await page.evaluate(() => getComputedStyle(document.documentElement).getPropertyValue('--accent').trim());
    expect(accent).not.toBe('');
    const probe = await page.evaluate((color) => {
      const el = document.createElement('i');
      el.style.color = color;
      document.body.append(el);
      const resolved = getComputedStyle(el).color;
      el.remove();
      return resolved;
    }, accent);
    expect(background).toBe(probe);
    expect(violations).toEqual([]);
  });

  test('credenciales incorrectas muestran un mensaje claro en español', async ({ page }) => {
    await signIn(page, 'no-existe', 'nadie@nada.test', 'Incorrecta-12345');

    await expect(page.getByRole('alert')).toHaveText('Gimnasio, correo o contraseña incorrectos.');
    await expect(page).toHaveURL(/\/login/);
  });

  test('sin sesión, una ruta protegida lleva al login y tras entrar regresa a ella', async ({ page }) => {
    const { email, password } = platformCredentials();

    await page.goto('/platform/tenants');
    await expect(page).toHaveURL(/\/login\?returnUrl=%2Fplatform%2Ftenants/);

    await page.getByLabel('Gimnasio').fill('platform');
    await page.getByLabel('Correo electrónico').fill(email);
    await page.getByLabel('Contraseña').fill(password);
    await page.getByRole('button', { name: 'Iniciar sesión' }).click();

    await expect(page).toHaveURL(/\/platform\/tenants$/);
    await expect(page.getByRole('heading', { name: 'Gimnasios' })).toBeVisible();
  });

  test('un returnUrl externo se ignora (no hay redirección abierta)', async ({ page, baseURL }) => {
    const { email, password } = platformCredentials();

    await page.goto('/login?returnUrl=https://sitio-malicioso.test/');
    await page.getByLabel('Gimnasio').fill('platform');
    await page.getByLabel('Correo electrónico').fill(email);
    await page.getByLabel('Contraseña').fill(password);
    await page.getByRole('button', { name: 'Iniciar sesión' }).click();

    await expect(page).toHaveURL(`${baseURL}/`);
  });
});

test.describe.serial('flujo completo: plataforma → gimnasio → personal', () => {
  test('la plataforma crea un gimnasio y solo ve su propia administración', async ({ page }) => {
    const { email, password } = platformCredentials();
    await signIn(page, 'platform', email, password);

    await expect(page.getByRole('heading', { name: /^Hola,/ })).toBeVisible();
    await expect(nav(page).getByRole('link', { name: 'Gimnasios' })).toBeVisible();
    await expect(nav(page).getByRole('link', { name: 'Sucursales' })).toHaveCount(0);
    await expect(nav(page).getByRole('link', { name: 'Usuarios' })).toHaveCount(0);

    await nav(page).getByRole('link', { name: 'Gimnasios' }).click();
    await page.getByRole('button', { name: 'Nuevo gimnasio' }).click();
    const dialog = page.getByRole('dialog');
    await dialog.locator('[formControlName="slug"]').fill(slug);
    await dialog.locator('[formControlName="name"]').fill(`Gimnasio ${run}`);
    await dialog.locator('[formControlName="ownerFullName"]').fill('Dueña de Prueba');
    await dialog.locator('[formControlName="ownerEmail"]').fill(ownerEmail);
    await dialog.locator('[formControlName="ownerPassword"]').fill(ownerPassword);
    await dialog.getByRole('button', { name: 'Guardar' }).click();

    await expect(page.getByRole('row', { name: new RegExp(slug) })).toBeVisible();
    await signOut(page);
  });

  test('una contraseña débil al crear el gimnasio se explica en español', async ({ page }) => {
    const { email, password } = platformCredentials();
    await signIn(page, 'platform', email, password);
    await nav(page).getByRole('link', { name: 'Gimnasios' }).click();
    await page.getByRole('button', { name: 'Nuevo gimnasio' }).click();

    const dialog = page.getByRole('dialog');
    await dialog.locator('[formControlName="slug"]').fill(`${slug}-debil`);
    await dialog.locator('[formControlName="name"]').fill('Débil');
    await dialog.locator('[formControlName="ownerFullName"]').fill('X');
    await dialog.locator('[formControlName="ownerEmail"]').fill('x@debil.test');
    await dialog.locator('[formControlName="ownerPassword"]').fill('corta');
    await dialog.getByRole('button', { name: 'Guardar' }).click();

    await expect(dialog.getByRole('alert')).toContainText('mínimo 10 caracteres');
  });

  test('el propietario ve todas las secciones de su gimnasio y no la de plataforma', async ({ page }) => {
    await signIn(page, slug, ownerEmail, ownerPassword);

    for (const name of ['Inicio', 'Sucursales', 'Usuarios', 'Roles y permisos', 'Auditoría']) {
      await expect(nav(page).getByRole('link', { name })).toBeVisible();
    }
    await expect(nav(page).getByRole('link', { name: 'Gimnasios' })).toHaveCount(0);
  });

  test('el propietario crea una sucursal, un rol y un usuario', async ({ page }) => {
    await signIn(page, slug, ownerEmail, ownerPassword);

    // Sucursal
    await nav(page).getByRole('link', { name: 'Sucursales' }).click();
    await page.getByRole('button', { name: 'Nueva sucursal' }).click();
    let dialog = page.getByRole('dialog');
    await dialog.locator('[formControlName="code"]').fill('MATRIZ');
    await dialog.locator('[formControlName="name"]').fill('Matriz Quito');
    await dialog.locator('[formControlName="sriEstablishmentCode"]').fill('001');
    await dialog.getByRole('button', { name: 'Guardar' }).click();
    await expect(page.getByRole('row', { name: /MATRIZ.*Matriz Quito/ })).toBeVisible();

    // Un código repetido se rechaza con el mensaje de la API traducido
    await page.getByRole('button', { name: 'Nueva sucursal' }).click();
    dialog = page.getByRole('dialog');
    await dialog.locator('[formControlName="code"]').fill('matriz');
    await dialog.locator('[formControlName="name"]').fill('Duplicada');
    await dialog.getByRole('button', { name: 'Guardar' }).click();
    await expect(dialog.getByRole('alert')).toHaveText('Ya existe una sucursal con ese código.');
    await dialog.getByRole('button', { name: 'Cancelar' }).click();

    // Rol propio con un solo permiso
    await nav(page).getByRole('link', { name: 'Roles y permisos' }).click();
    await page.getByRole('button', { name: 'Nuevo rol' }).click();
    dialog = page.getByRole('dialog');
    await dialog.locator('[formControlName="name"]').fill('Recepción Plus');
    await dialog.getByLabel('Ver sucursales').check();
    await dialog.getByRole('button', { name: 'Guardar' }).click();
    await expect(page.getByRole('row', { name: /Recepción Plus/ })).toBeVisible();

    // Usuario con ese rol y esa sucursal
    await nav(page).getByRole('link', { name: 'Usuarios' }).click();
    await page.getByRole('button', { name: 'Nuevo usuario' }).click();
    dialog = page.getByRole('dialog');
    await dialog.locator('[formControlName="fullName"]').fill('Persona de Recepción');
    await dialog.locator('[formControlName="email"]').fill(staffEmail);
    await dialog.locator('[formControlName="password"]').fill(staffPassword);
    await dialog.getByLabel('Recepción Plus').check();
    await dialog.getByLabel('MATRIZ — Matriz Quito').check();
    await dialog.getByRole('button', { name: 'Guardar' }).click();
    await expect(page.getByRole('row', { name: new RegExp(staffEmail) })).toBeVisible();
  });

  test('el rol Propietario es de solo lectura y los roles ajenos al usuario no se ofrecen', async ({ page }) => {
    await signIn(page, slug, ownerEmail, ownerPassword);
    await nav(page).getByRole('link', { name: 'Roles y permisos' }).click();

    const ownerRow = page.getByRole('row', { name: /Propietario/ });
    await expect(ownerRow.getByRole('button', { name: 'Ver' })).toBeVisible();
    await expect(ownerRow.getByRole('button', { name: 'Eliminar' })).toHaveCount(0);

    await ownerRow.getByRole('button', { name: 'Ver' }).click();
    const dialog = page.getByRole('dialog');
    await expect(dialog.getByText('siempre tiene todos los permisos')).toBeVisible();
    await expect(dialog.getByRole('button', { name: 'Guardar' })).toHaveCount(0);
  });

  test('la auditoría muestra los cambios realizados', async ({ page }) => {
    await signIn(page, slug, ownerEmail, ownerPassword);
    await nav(page).getByRole('link', { name: 'Auditoría' }).click();

    await expect(page.getByRole('cell', { name: 'Branch.Created', exact: true })).toBeVisible();
    await expect(page.getByRole('cell', { name: 'AppUser.Created', exact: true }).first()).toBeVisible();
    await expect(page.getByRole('cell', { name: 'AppRole.Created', exact: true }).first()).toBeVisible();
  });

  test('el personal solo ve lo que su rol permite y las rutas restringidas lo bloquean', async ({ page }) => {
    await signIn(page, slug, staffEmail, staffPassword);

    await expect(nav(page).getByRole('link', { name: 'Sucursales' })).toBeVisible();
    await expect(nav(page).getByRole('link', { name: 'Usuarios' })).toHaveCount(0);
    await expect(nav(page).getByRole('link', { name: 'Roles y permisos' })).toHaveCount(0);
    await expect(nav(page).getByRole('link', { name: 'Auditoría' })).toHaveCount(0);

    // Solo ve su sucursal y no puede crear ni editar
    await nav(page).getByRole('link', { name: 'Sucursales' }).click();
    await expect(page.getByRole('row', { name: /MATRIZ/ })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Nueva sucursal' })).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'Editar' })).toHaveCount(0);

    // Escribir la URL a mano no salta la restricción
    await page.goto('/users');
    await expect(page.getByRole('heading', { name: 'Sin acceso' })).toBeVisible();
  });

  test('la sesión sobrevive a recargar la página (cookie de refresh) y se cierra al salir', async ({ page }) => {
    await signIn(page, slug, staffEmail, staffPassword);
    await expect(page.getByRole('heading', { name: /^Hola,/ })).toBeVisible();

    await page.reload();
    await expect(page.getByRole('heading', { name: /^Hola,/ })).toBeVisible();

    await signOut(page);
    await page.goto('/branches');
    await expect(page).toHaveURL(/\/login/);
  });

  test('desactivar a un usuario cierra su sesión activa', async ({ browser }) => {
    const owner = await browser.newContext();
    const staff = await browser.newContext();
    const ownerPage = await owner.newPage();
    const staffPage = await staff.newPage();

    await signIn(staffPage, slug, staffEmail, staffPassword);
    await expect(staffPage.getByRole('heading', { name: /^Hola,/ })).toBeVisible();

    await signIn(ownerPage, slug, ownerEmail, ownerPassword);
    await nav(ownerPage).getByRole('link', { name: 'Usuarios' }).click();
    await ownerPage.getByRole('row', { name: new RegExp(staffEmail) }).getByRole('button', { name: 'Desactivar' }).click();
    await ownerPage.getByRole('dialog').getByRole('button', { name: 'Desactivar' }).click();
    await expect(ownerPage.getByRole('row', { name: new RegExp(staffEmail) })).toContainText('Inactivo');

    // La siguiente llamada del personal recibe 401, el refresh también falla y vuelve al login.
    await nav(staffPage).getByRole('link', { name: 'Sucursales' }).click();
    await expect(staffPage).toHaveURL(/\/login/);
    await expect(staffPage.getByText('Su sesión terminó')).toBeVisible();

    await owner.close();
    await staff.close();
  });

  test('el idioma se puede cambiar a inglés y se recuerda', async ({ page }) => {
    await signIn(page, slug, ownerEmail, ownerPassword);

    // Al cambiar de idioma también cambia el nombre accesible del menú ("Main menu").
    const menu = page.getByRole('navigation', { name: 'Main menu' });

    await page.getByRole('button', { name: 'EN' }).click();
    await expect(menu.getByRole('link', { name: 'Branches' })).toBeVisible();
    await expect(page.locator('html')).toHaveAttribute('lang', 'en');

    await page.reload();
    await expect(menu.getByRole('link', { name: 'Branches' })).toBeVisible();
    await expect(page.locator('html')).toHaveAttribute('lang', 'en');
  });
});
