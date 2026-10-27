import type { components } from './schema';

type Schemas = components['schemas'];

// Los tipos vienen del OpenAPI de la API (npm run generate:api). Aquí solo se les da un nombre corto.
export type TokenResponse = Schemas['TokenResponse'];
export type Me = Schemas['MeResponse'];
export type Tenant = Schemas['TenantDto'];
export type Branch = Schemas['BranchDto'];
export type User = Schemas['UserDto'];
export type Role = Schemas['RoleDto'];
export type Permission = Schemas['PermissionDto'];
export type AuditLog = Schemas['AuditLogDto'];

export type CreateTenantRequest = Schemas['CreateTenantRequest'];
export type CreateBranchRequest = Schemas['CreateBranchRequest'];
export type UpdateBranchRequest = Schemas['UpdateBranchRequest'];
export type CreateUserRequest = Schemas['CreateUserRequest'];
export type SetUserAccessRequest = Schemas['SetUserAccessRequest'];
export type CreateRoleRequest = Schemas['CreateRoleRequest'];
export type UpdateRoleRequest = Schemas['UpdateRoleRequest'];

export interface Paged<T> {
  items: T[];
  page: number;
  pageSize: number;
  totalCount: number;
}

/** Cuerpo de error de la API (RFC 9457) con el código estable de negocio. */
export interface ProblemDetails {
  status?: number;
  title?: string;
  code?: string;
  errors?: Record<string, string[]>;
}
