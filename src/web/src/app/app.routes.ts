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
    path: '',
    canActivate: [authGuard],
    loadComponent: () => import('./layout/shell').then((m) => m.Shell),
    children: [
      { path: '', pathMatch: 'full', loadComponent: () => import('./features/home/home').then((m) => m.Home) },
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
