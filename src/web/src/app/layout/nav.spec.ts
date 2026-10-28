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

  it('el instructor ve «Mis clases» solo si no puede ver todas las clases', () => {
    expect(paths(['analytics.dashboard.read', 'reports.instructor.read'])).toEqual(['/', '/analytics', '/analytics/my-classes']);
    expect(paths(['analytics.dashboard.read', 'reports.instructor.read', 'reports.operations.read'])).toEqual(['/', '/analytics', '/analytics/operations']);
  });

  it('cada reporte de analítica pide su propio permiso', () => {
    expect(paths(['reports.financial.read'])).toEqual(['/', '/analytics/financial']);
    expect(paths(['reports.compare', 'reports.export'])).toEqual(['/', '/analytics/branches', '/analytics/exports']);
  });

  it('un propietario ve todo menos la administración de plataforma', () => {
    const owner = ['branches.read', 'users.read', 'roles.read', 'audit.read'];
    expect(paths(owner)).toEqual(['/', '/branches', '/users', '/roles', '/audit']);
  });
});
