import type { Role } from '../api/models';

/**
 * Misma regla que aplica la API: un rol solo es asignable si quien lo asigna ya posee todos sus permisos y, si el
 * rol abarca todas las sucursales, también accede a todas. Aquí sirve solo para no ofrecer opciones que el servidor
 * rechazaría; la API sigue siendo quien decide.
 */
export function canAssignRole(role: Role, has: (permission: string) => boolean, allBranches: boolean): boolean {
  return role.permissions.every(has) && (!role.grantsAllBranches || allBranches);
}
