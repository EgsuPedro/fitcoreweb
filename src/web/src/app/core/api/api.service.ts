import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import type { Observable } from 'rxjs';
import type {
  AuditLog,
  Branch,
  CreateBranchRequest,
  CreateRoleRequest,
  CreateTenantRequest,
  CreateUserRequest,
  Paged,
  Permission,
  Role,
  SetUserAccessRequest,
  Tenant,
  UpdateBranchRequest,
  UpdateRoleRequest,
  User,
} from './models';

export const API_BASE = '/api/v1';

type Query = Record<string, string | number | boolean | null | undefined>;

function toParams(query: Query): HttpParams {
  let params = new HttpParams();
  for (const [key, value] of Object.entries(query)) {
    if (value !== null && value !== undefined && value !== '') {
      params = params.set(key, String(value));
    }
  }
  return params;
}

/** Cliente tipado de la API de negocio. La sesión y los errores los resuelve el interceptor. */
@Injectable({ providedIn: 'root' })
export class ApiService {
  private readonly http = inject(HttpClient);

  // Sucursales
  branches(query: Query = {}): Observable<Paged<Branch>> {
    return this.http.get<Paged<Branch>>(`${API_BASE}/branches`, { params: toParams(query) });
  }
  createBranch(body: CreateBranchRequest): Observable<Branch> {
    return this.http.post<Branch>(`${API_BASE}/branches`, body);
  }
  updateBranch(id: string, body: UpdateBranchRequest): Observable<Branch> {
    return this.http.put<Branch>(`${API_BASE}/branches/${id}`, body);
  }

  // Usuarios
  users(query: Query = {}): Observable<Paged<User>> {
    return this.http.get<Paged<User>>(`${API_BASE}/users`, { params: toParams(query) });
  }
  createUser(body: CreateUserRequest): Observable<User> {
    return this.http.post<User>(`${API_BASE}/users`, body);
  }
  setUserAccess(id: string, body: SetUserAccessRequest): Observable<User> {
    return this.http.put<User>(`${API_BASE}/users/${id}/access`, body);
  }
  setUserStatus(id: string, isActive: boolean): Observable<User> {
    return this.http.put<User>(`${API_BASE}/users/${id}/status`, { isActive });
  }

  // Roles y permisos
  roles(): Observable<Role[]> {
    return this.http.get<Role[]>(`${API_BASE}/roles`);
  }
  permissions(): Observable<Permission[]> {
    return this.http.get<Permission[]>(`${API_BASE}/permissions`);
  }
  createRole(body: CreateRoleRequest): Observable<Role> {
    return this.http.post<Role>(`${API_BASE}/roles`, body);
  }
  updateRole(id: string, body: UpdateRoleRequest): Observable<Role> {
    return this.http.put<Role>(`${API_BASE}/roles/${id}`, body);
  }
  deleteRole(id: string): Observable<void> {
    return this.http.delete<void>(`${API_BASE}/roles/${id}`);
  }

  // Auditoría
  auditLogs(query: Query = {}): Observable<Paged<AuditLog>> {
    return this.http.get<Paged<AuditLog>>(`${API_BASE}/audit-logs`, { params: toParams(query) });
  }

  // Plataforma
  tenants(query: Query = {}): Observable<Paged<Tenant>> {
    return this.http.get<Paged<Tenant>>(`${API_BASE}/platform/tenants`, { params: toParams(query) });
  }
  createTenant(body: CreateTenantRequest): Observable<Tenant> {
    return this.http.post<Tenant>(`${API_BASE}/platform/tenants`, body);
  }
  setTenantStatus(id: string, active: boolean): Observable<Tenant> {
    return this.http.post<Tenant>(`${API_BASE}/platform/tenants/${id}/${active ? 'activate' : 'suspend'}`, {});
  }
}
