/** Códigos de permiso de la API (catálogo en FitCore.Platform.Domain.Permissions). */
export const Perm = {
  PlatformTenantsManage: 'platform.tenants.manage',
  OrganizationRead: 'organization.read',
  OrganizationUpdate: 'organization.update',
  BranchesRead: 'branches.read',
  BranchesCreate: 'branches.create',
  BranchesUpdate: 'branches.update',
  UsersRead: 'users.read',
  UsersCreate: 'users.create',
  UsersUpdate: 'users.update',
  RolesRead: 'roles.read',
  RolesManage: 'roles.manage',
  AuditRead: 'audit.read',
} as const;
