import { NAV_ITEMS, visibleNavItems } from './nav';

describe('visibleNavItems', () => {
  const paths = (permissions: string[]) =>
    visibleNavItems(NAV_ITEMS, (permission) => permissions.includes(permission)).map((item) => item.path);

  it('sin permisos solo se ve el inicio', () => {
    expect(paths([])).toEqual(['/']);
  });

  it('muestra solo las secciones permitidas', () => {
    expect(paths(['branches.read', 'users.read'])).toEqual(['/', '/branches', '/users']);
  });

  it('el administrador de plataforma ve gimnasios pero no las pantallas de un gimnasio', () => {
    expect(paths(['platform.tenants.manage', 'organization.read'])).toEqual(['/', '/platform/tenants']);
  });

  it('un propietario ve todo menos la administración de plataforma', () => {
    const owner = ['branches.read', 'users.read', 'roles.read', 'audit.read'];
    expect(paths(owner)).toEqual(['/', '/branches', '/users', '/roles', '/audit']);
  });
});
