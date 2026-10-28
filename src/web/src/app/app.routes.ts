import { Routes } from '@angular/router';
import { Perm } from './core/auth/permissions';
import { authGuard, guestGuard, permissionGuard } from './core/auth/guards';

export const routes: Routes = [
  {
    path: 'login',
    canActivate: [guestGuard],
    loadComponent: () => import('./features/login/login').then((m) => m.Login),
    title: 'FitCore 360',
  },
  {
    path: 'change-password',
    canActivate: [authGuard],
    loadComponent: () => import('./features/account/change-password').then((m) => m.ChangePassword),
    title: 'FitCore 360',
  },
  {
    path: '',
    canActivate: [authGuard],
    loadComponent: () => import('./layout/shell').then((m) => m.Shell),
    children: [
      { path: '', pathMatch: 'full', loadComponent: () => import('./features/home/home').then((m) => m.Home) },
      {
        path: 'members',
        canActivate: [permissionGuard],
        data: { permission: Perm.MembersRead },
        loadComponent: () => import('./features/members/members').then((m) => m.Members),
      },
      {
        path: 'members/:id',
        canActivate: [permissionGuard],
        data: { permission: Perm.MembersRead },
        loadComponent: () => import('./features/members/member-detail').then((m) => m.MemberDetail),
      },
      {
        path: 'plans',
        canActivate: [permissionGuard],
        data: { permission: Perm.PlansRead },
        loadComponent: () => import('./features/plans/plans').then((m) => m.Plans),
      },
      {
        path: 'promotions',
        canActivate: [permissionGuard],
        data: { permission: Perm.PromotionsRead },
        loadComponent: () => import('./features/plans/promotions').then((m) => m.Promotions),
      },
      {
        path: 'memberships',
        canActivate: [permissionGuard],
        data: { permission: Perm.MembershipsRead },
        loadComponent: () => import('./features/memberships/memberships').then((m) => m.Memberships),
      },
      {
        path: 'memberships/:id',
        canActivate: [permissionGuard],
        data: { permission: Perm.MembershipsRead },
        loadComponent: () => import('./features/memberships/membership-detail').then((m) => m.MembershipDetail),
      },
      {
        path: 'payments',
        canActivate: [permissionGuard],
        data: { permission: Perm.PaymentsRead },
        loadComponent: () => import('./features/billing/payments').then((m) => m.Payments),
      },
      {
        path: 'receivables',
        canActivate: [permissionGuard],
        data: { permission: Perm.ObligationsRead },
        loadComponent: () => import('./features/billing/receivables').then((m) => m.Receivables),
      },
      {
        path: 'cash',
        canActivate: [permissionGuard],
        data: { permission: Perm.CashRead },
        loadComponent: () => import('./features/cash/cash').then((m) => m.Cash),
      },
      {
        path: 'attendance',
        canActivate: [permissionGuard],
        data: { permission: Perm.AttendanceRead },
        loadComponent: () => import('./features/attendance/attendance').then((m) => m.AttendancePage),
      },
      {
        path: 'settings',
        canActivate: [permissionGuard],
        data: { permission: Perm.PlansRead },
        loadComponent: () => import('./features/settings/commercial-settings').then((m) => m.CommercialSettingsPage),
      },
      {
        path: 'platform/tax-rates',
        canActivate: [permissionGuard],
        data: { permission: Perm.PlatformTaxesManage },
        loadComponent: () => import('./features/settings/tax-rates').then((m) => m.TaxRates),
      },
      {
        path: 'notifications',
        canActivate: [permissionGuard],
        data: { permission: Perm.NotificationsRead },
        loadComponent: () => import('./features/notifications/notifications').then((m) => m.NotificationsPage),
      },
      {
        path: 'trainers',
        canActivate: [permissionGuard],
        data: { permission: Perm.TrainersRead },
        loadComponent: () => import('./features/trainers/trainers').then((m) => m.Trainers),
      },
      {
        path: 'trainers/:id',
        canActivate: [permissionGuard],
        data: { permission: Perm.TrainersRead },
        loadComponent: () => import('./features/trainers/trainer-detail').then((m) => m.TrainerDetail),
      },
      {
        path: 'exercises',
        canActivate: [permissionGuard],
        data: { permission: Perm.ExercisesRead },
        loadComponent: () => import('./features/exercises/exercises').then((m) => m.Exercises),
      },
      {
        path: 'platform/exercises',
        canActivate: [permissionGuard],
        data: { permission: Perm.PlatformExercisesManage, platform: true },
        loadComponent: () => import('./features/exercises/exercises').then((m) => m.Exercises),
      },
      {
        path: 'branches',
        canActivate: [permissionGuard],
        data: { permission: Perm.BranchesRead },
        loadComponent: () => import('./features/branches/branches').then((m) => m.Branches),
      },
      {
        path: 'users',
        canActivate: [permissionGuard],
        data: { permission: Perm.UsersRead },
        loadComponent: () => import('./features/users/users').then((m) => m.Users),
      },
      {
        path: 'roles',
        canActivate: [permissionGuard],
        data: { permission: Perm.RolesRead },
        loadComponent: () => import('./features/roles/roles').then((m) => m.Roles),
      },
      {
        path: 'audit',
        canActivate: [permissionGuard],
        data: { permission: Perm.AuditRead },
        loadComponent: () => import('./features/audit/audit').then((m) => m.Audit),
      },
      {
        path: 'platform/tenants',
        canActivate: [permissionGuard],
        data: { permission: Perm.PlatformTenantsManage },
        loadComponent: () => import('./features/tenants/tenants').then((m) => m.Tenants),
      },
      { path: 'forbidden', loadComponent: () => import('./features/status/status-pages').then((m) => m.Forbidden) },
      { path: '**', loadComponent: () => import('./features/status/status-pages').then((m) => m.NotFound) },
    ],
  },
];
