import { Perm } from '../core/auth/permissions';

export interface NavItem {
  labelKey: string;
  path: string;
  icon: string;
  /** Permiso necesario para ver el elemento; sin permiso, siempre visible. */
  permission?: string;
  /** Se oculta a quien tiene este permiso (por ejemplo, «Mis clases» del instructor no se muestra a quien ya ve todas las clases). */
  unlessPermission?: string;
}

export const NAV_ITEMS: readonly NavItem[] = [
  { labelKey: 'nav.home', path: '/', icon: '⌂' },
  { labelKey: 'nav.members', path: '/members', icon: '☻', permission: Perm.MembersRead },
  { labelKey: 'nav.exercises', path: '/exercises', icon: '♜', permission: Perm.ExercisesRead },
  { labelKey: 'nav.trainingPlans', path: '/training-plans', icon: '☰', permission: Perm.TrainingPlansRead },
  { labelKey: 'nav.nutritionPlans', path: '/nutrition-plans', icon: '❦', permission: Perm.NutritionPlansRead },
  { labelKey: 'nav.trainers', path: '/trainers', icon: '♞', permission: Perm.TrainersRead },
  { labelKey: 'nav.attendance', path: '/attendance', icon: '✔', permission: Perm.AttendanceRead },
  { labelKey: 'nav.schedule', path: '/schedule', icon: '▤', permission: Perm.ClassSessionsRead },
  { labelKey: 'nav.classes', path: '/classes', icon: '♫', permission: Perm.GroupClassesRead },
  { labelKey: 'nav.classAttendance', path: '/class-attendance', icon: '☑', permission: Perm.ClassAttendanceRead },
  { labelKey: 'nav.analytics', path: '/analytics', icon: '◔', permission: Perm.AnalyticsDashboardRead },
  { labelKey: 'nav.reportsFinancial', path: '/analytics/financial', icon: '▥', permission: Perm.ReportsFinancialRead },
  { labelKey: 'nav.reportsMembers', path: '/analytics/members', icon: '▦', permission: Perm.ReportsMembersRead },
  { labelKey: 'nav.reportsOperations', path: '/analytics/operations', icon: '▧', permission: Perm.ReportsOperationsRead },
  { labelKey: 'nav.reportsMyClasses', path: '/analytics/my-classes', icon: '▨', permission: Perm.ReportsInstructorRead, unlessPermission: Perm.ReportsOperationsRead },
  { labelKey: 'nav.reportsBranches', path: '/analytics/branches', icon: '▩', permission: Perm.ReportsCompare },
  { labelKey: 'nav.reportsExports', path: '/analytics/exports', icon: '⇩', permission: Perm.ReportsExport },
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
  { labelKey: 'nav.invitations', path: '/invitations', icon: '✉', permission: Perm.UsersCreate },
  { labelKey: 'nav.roles', path: '/roles', icon: '⚿', permission: Perm.RolesRead },
  { labelKey: 'nav.audit', path: '/audit', icon: '☰', permission: Perm.AuditRead },
  { labelKey: 'nav.branding', path: '/branding', icon: '◈', permission: Perm.BrandingManage },
  { labelKey: 'nav.invoicing', path: '/invoicing', icon: '§', permission: Perm.InvoicingRead },
  { labelKey: 'nav.invoicingSettings', path: '/invoicing/settings', icon: '⚙', permission: Perm.InvoicingRead },
  { labelKey: 'nav.mySubscription', path: '/my/subscription', icon: '★', permission: Perm.SubscriptionRead },
  { labelKey: 'nav.taxRates', path: '/platform/tax-rates', icon: '‰', permission: Perm.PlatformTaxesManage },
  { labelKey: 'nav.globalExercises', path: '/platform/exercises', icon: '♜', permission: Perm.PlatformExercisesManage },
  { labelKey: 'nav.platformMetrics', path: '/platform/metrics', icon: '◔', permission: Perm.PlatformMetricsRead },
  { labelKey: 'nav.platformOrganizations', path: '/platform/organizations', icon: '▣', permission: Perm.PlatformOrganizationsRead },
  { labelKey: 'nav.platformLeads', path: '/platform/leads', icon: '✎', permission: Perm.PlatformLeadsRead },
  { labelKey: 'nav.platformPlans', path: '/platform/plans', icon: '☷', permission: Perm.PlatformPlansRead },
  { labelKey: 'nav.platformBilling', path: '/platform/billing', icon: '$', permission: Perm.PlatformBillingRead },
  { labelKey: 'nav.platformRequests', path: '/platform/requests', icon: '✉', permission: Perm.PlatformSubscriptionsRead },
  { labelKey: 'nav.tenants', path: '/platform/tenants', icon: '▦', permission: Perm.PlatformTenantsManage },
];

/** Elementos del menú que el usuario puede ver según sus permisos. */
export function visibleNavItems(items: readonly NavItem[], has: (permission: string) => boolean): NavItem[] {
  return items.filter((item) => (!item.permission || has(item.permission)) && !(item.unlessPermission && has(item.unlessPermission)));
}
