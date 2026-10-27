import { Perm } from '../core/auth/permissions';

export interface NavItem {
  labelKey: string;
  path: string;
  icon: string;
  /** Permiso necesario para ver el elemento; sin permiso, siempre visible. */
  permission?: string;
}

export const NAV_ITEMS: readonly NavItem[] = [
  { labelKey: 'nav.home', path: '/', icon: '⌂' },
  { labelKey: 'nav.branches', path: '/branches', icon: '⌖', permission: Perm.BranchesRead },
  { labelKey: 'nav.users', path: '/users', icon: '☺', permission: Perm.UsersRead },
  { labelKey: 'nav.roles', path: '/roles', icon: '⚿', permission: Perm.RolesRead },
  { labelKey: 'nav.audit', path: '/audit', icon: '☰', permission: Perm.AuditRead },
  { labelKey: 'nav.tenants', path: '/platform/tenants', icon: '▦', permission: Perm.PlatformTenantsManage },
];

/** Elementos del menú que el usuario puede ver según sus permisos. */
export function visibleNavItems(items: readonly NavItem[], has: (permission: string) => boolean): NavItem[] {
  return items.filter((item) => !item.permission || has(item.permission));
}
