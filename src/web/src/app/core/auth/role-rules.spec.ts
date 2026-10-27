import type { Role } from '../api/models';
import { canAssignRole } from './role-rules';

const role = (permissions: string[], grantsAllBranches = false): Role => ({
  id: 'r1',
  name: 'Rol',
  description: null,
  isSystem: false,
  grantsAllBranches,
  permissions,
});

describe('canAssignRole', () => {
  const has = (held: string[]) => (permission: string) => held.includes(permission);

  it('permite roles cuyos permisos ya se poseen', () => {
    expect(canAssignRole(role(['branches.read']), has(['branches.read', 'users.read']), false)).toBe(true);
  });

  it('rechaza roles con permisos que no se poseen', () => {
    expect(canAssignRole(role(['branches.read', 'users.create']), has(['branches.read']), true)).toBe(false);
  });

  it('un rol que abarca todas las sucursales exige acceder a todas', () => {
    expect(canAssignRole(role([], true), has([]), false)).toBe(false);
    expect(canAssignRole(role([], true), has([]), true)).toBe(true);
  });

  it('un rol sin permisos siempre es asignable', () => {
    expect(canAssignRole(role([]), has([]), false)).toBe(true);
  });
});
