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
  { labelKey: 'nav.members', path: '/members', icon: '☻', permission: Perm.MembersRead },
  { labelKey: 'nav.trainers', path: '/trainers', icon: '♞', permission: Perm.TrainersRead },
  { labelKey: 'nav.attendance', path: '/attendance', icon: '✔', permission: Perm.AttendanceRead },
  { labelKey: 'nav.memberships', path: '/memberships', icon: '❖', permission: Perm.MembershipsRead },
  { labelKey: 'nav.payments', path: '/payments', icon: '$', permission: Perm.PaymentsRead },
  { labelKey: 'nav.receivables', path: '/receivables', icon: '≋', permission: Perm.ObligationsRead },
  { labelKey: 'nav.cash', path: '/cash', icon: '▣', permission: Perm.CashRead },
  { labelKey: 'nav.notifications', path: '/notifications', icon: '✉', permission: Perm.NotificationsRead },
  { labelKey: 'nav.plans', path: '/plans', icon: '☷', permission: Perm.PlansRead },
  { labelKey: 'nav.promotions', path: '/promotions', icon: '%', permission: Perm.PromotionsRead },
  { labelKey: 'nav.settings', path: '/settings', icon: '⚙', permission: Perm.PlansRead },
  { labelKey: 'nav.branches', path: '/branches', icon: '⌖', permission: Perm.BranchesRead },
  { labelKey: 'nav.users', path: '/users', icon: '☺', permission: Perm.UsersRead },
  { labelKey: 'nav.roles', path: '/roles', icon: '⚿', permission: Perm.RolesRead },
  { labelKey: 'nav.audit', path: '/audit', icon: '☰', permission: Perm.AuditRead },
  { labelKey: 'nav.taxRates', path: '/platform/tax-rates', icon: '‰', permission: Perm.PlatformTaxesManage },
  { labelKey: 'nav.tenants', path: '/platform/tenants', icon: '▦', permission: Perm.PlatformTenantsManage },
];

/** Elementos del menú que el usuario puede ver según sus permisos. */
export function visibleNavItems(items: readonly NavItem[], has: (permission: string) => boolean): NavItem[] {
  return items.filter((item) => !item.permission || has(item.permission));
}
