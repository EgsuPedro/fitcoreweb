import { HttpClient, HttpHeaders, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import type { Observable } from 'rxjs';
import type {
  AccessDecision,
  Attendance,
  AuditLog,
  Branch,
  CashMovement,
  CashRegister,
  CashSession,
  CommercialSettings,
  CreateBranchRequest,
  CreateMemberRequest,
  CreateObligationRequest,
  CreatePaymentRequest,
  CreateRoleRequest,
  CreateTaxRateRequest,
  CreateTenantRequest,
  CreateUserRequest,
  EnrollRequest,
  Member,
  Membership,
  MembershipEvent,
  Obligation,
  Paged,
  Payment,
  Permission,
  Plan,
  PlanRequest,
  Promotion,
  PromotionRequest,
  QrToken,
  Receipt,
  ReceivablesSummary,
  RenewRequest,
  Role,
  SetUserAccessRequest,
  TaxRate,
  Tenant,
  UpdateBranchRequest,
  UpdateCommercialSettingsRequest,
  UpdateMemberRequest,
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

  private get<T>(path: string, query: Query = {}): Observable<T> {
    return this.http.get<T>(`${API_BASE}${path}`, { params: toParams(query) });
  }

  /** Operaciones que mueven dinero: la clave de idempotencia evita duplicarlas ante un reintento. */
  private postKeyed<T>(path: string, body: unknown, key: string): Observable<T> {
    return this.http.post<T>(`${API_BASE}${path}`, body, { headers: new HttpHeaders({ 'Idempotency-Key': key }) });
  }

  // Sucursales
  branches(query: Query = {}): Observable<Paged<Branch>> {
    return this.get('/branches', query);
  }
  createBranch(body: CreateBranchRequest): Observable<Branch> {
    return this.http.post<Branch>(`${API_BASE}/branches`, body);
  }
  updateBranch(id: string, body: UpdateBranchRequest): Observable<Branch> {
    return this.http.put<Branch>(`${API_BASE}/branches/${id}`, body);
  }

  // Usuarios
  users(query: Query = {}): Observable<Paged<User>> {
    return this.get('/users', query);
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
    return this.get('/roles');
  }
  permissions(): Observable<Permission[]> {
    return this.get('/permissions');
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
    return this.get('/audit-logs', query);
  }

  // Plataforma
  tenants(query: Query = {}): Observable<Paged<Tenant>> {
    return this.get('/platform/tenants', query);
  }
  createTenant(body: CreateTenantRequest): Observable<Tenant> {
    return this.http.post<Tenant>(`${API_BASE}/platform/tenants`, body);
  }
  setTenantStatus(id: string, active: boolean): Observable<Tenant> {
    return this.http.post<Tenant>(`${API_BASE}/platform/tenants/${id}/${active ? 'activate' : 'suspend'}`, {});
  }
  platformTaxRates(): Observable<TaxRate[]> {
    return this.get('/platform/tax-rates');
  }
  createTaxRate(body: CreateTaxRateRequest): Observable<TaxRate> {
    return this.http.post<TaxRate>(`${API_BASE}/platform/tax-rates`, body);
  }
  currentTaxRates(): Observable<TaxRate[]> {
    return this.get('/tax-rates/current');
  }

  // Alumnos
  members(query: Query = {}): Observable<Paged<Member>> {
    return this.get('/members', query);
  }
  member(id: string): Observable<Member> {
    return this.get(`/members/${id}`);
  }
  createMember(body: CreateMemberRequest): Observable<Member> {
    return this.http.post<Member>(`${API_BASE}/members`, body);
  }
  updateMember(id: string, body: UpdateMemberRequest): Observable<Member> {
    return this.http.put<Member>(`${API_BASE}/members/${id}`, body);
  }
  setMemberStatus(id: string, isActive: boolean): Observable<Member> {
    return this.http.patch<Member>(`${API_BASE}/members/${id}/status`, { isActive });
  }
  issueMemberQr(id: string): Observable<QrToken> {
    return this.http.post<QrToken>(`${API_BASE}/members/${id}/qr-token`, {});
  }

  // Planes, promociones y políticas
  plans(query: Query = {}): Observable<Paged<Plan>> {
    return this.get('/membership-plans', query);
  }
  createPlan(body: PlanRequest): Observable<Plan> {
    return this.http.post<Plan>(`${API_BASE}/membership-plans`, body);
  }
  updatePlan(id: string, body: PlanRequest): Observable<Plan> {
    return this.http.put<Plan>(`${API_BASE}/membership-plans/${id}`, body);
  }
  promotions(query: Query = {}): Observable<Paged<Promotion>> {
    return this.get('/promotions', query);
  }
  createPromotion(body: PromotionRequest): Observable<Promotion> {
    return this.http.post<Promotion>(`${API_BASE}/promotions`, body);
  }
  updatePromotion(id: string, body: PromotionRequest): Observable<Promotion> {
    return this.http.put<Promotion>(`${API_BASE}/promotions/${id}`, body);
  }
  commercialSettings(): Observable<CommercialSettings> {
    return this.get('/commercial-settings');
  }
  updateCommercialSettings(body: UpdateCommercialSettingsRequest): Observable<CommercialSettings> {
    return this.http.put<CommercialSettings>(`${API_BASE}/commercial-settings`, body);
  }

  // Membresías
  memberships(query: Query = {}): Observable<Paged<Membership>> {
    return this.get('/memberships', query);
  }
  expiringMemberships(query: Query = {}): Observable<Paged<Membership>> {
    return this.get('/memberships/expiring', query);
  }
  membership(id: string): Observable<Membership> {
    return this.get(`/memberships/${id}`);
  }
  membershipEvents(id: string): Observable<MembershipEvent[]> {
    return this.get(`/memberships/${id}/events`);
  }
  enroll(body: EnrollRequest): Observable<Membership> {
    return this.http.post<Membership>(`${API_BASE}/memberships`, body);
  }
  renew(id: string, body: RenewRequest): Observable<Membership> {
    return this.http.post<Membership>(`${API_BASE}/memberships/${id}/renew`, body);
  }
  freeze(id: string, reason: string | null): Observable<Membership> {
    return this.http.post<Membership>(`${API_BASE}/memberships/${id}/freeze`, { reason });
  }
  reactivate(id: string): Observable<Membership> {
    return this.http.post<Membership>(`${API_BASE}/memberships/${id}/reactivate`, {});
  }
  cancelMembership(id: string, reason: string): Observable<Membership> {
    return this.http.post<Membership>(`${API_BASE}/memberships/${id}/cancel`, { reason });
  }

  // Obligaciones, pagos y cartera
  obligations(query: Query = {}): Observable<Paged<Obligation>> {
    return this.get('/payment-obligations', query);
  }
  createObligation(body: CreateObligationRequest): Observable<Obligation> {
    return this.http.post<Obligation>(`${API_BASE}/payment-obligations`, body);
  }
  voidObligation(id: string, reason: string): Observable<Obligation> {
    return this.http.post<Obligation>(`${API_BASE}/payment-obligations/${id}/void`, { reason });
  }
  payments(query: Query = {}): Observable<Paged<Payment>> {
    return this.get('/payments', query);
  }
  receipt(id: string): Observable<Receipt> {
    return this.get(`/payments/${id}/receipt`);
  }
  createPayment(body: CreatePaymentRequest, key: string): Observable<Payment> {
    return this.postKeyed('/payments', body, key);
  }
  confirmPayment(id: string, key: string): Observable<Payment> {
    return this.postKeyed(`/payments/${id}/confirm`, {}, key);
  }
  reversePayment(id: string, reason: string, key: string): Observable<Payment> {
    return this.postKeyed(`/payments/${id}/reverse`, { reason }, key);
  }
  receivables(query: Query = {}): Observable<ReceivablesSummary> {
    return this.get('/receivables/summary', query);
  }

  // Caja
  cashRegisters(query: Query = {}): Observable<Paged<CashRegister>> {
    return this.get('/cash-registers', query);
  }
  createCashRegister(body: { branchId: string; name: string; allowMultipleOpenSessions: boolean }): Observable<CashRegister> {
    return this.http.post<CashRegister>(`${API_BASE}/cash-registers`, body);
  }
  updateCashRegister(id: string, body: { name: string; isActive: boolean; allowMultipleOpenSessions: boolean }): Observable<CashRegister> {
    return this.http.put<CashRegister>(`${API_BASE}/cash-registers/${id}`, body);
  }
  openCash(id: string, body: { openingAmount: number; notes: string | null }, key: string): Observable<CashSession> {
    return this.postKeyed(`/cash-registers/${id}/open`, body, key);
  }
  closeCash(id: string, body: { sessionId: string | null; countedAmount: number; notes: string | null }, key: string): Observable<CashSession> {
    return this.postKeyed(`/cash-registers/${id}/close`, body, key);
  }
  addCashMovement(id: string, body: { type: 'ManualIn' | 'ManualOut'; amount: number; reason: string }, key: string): Observable<CashMovement> {
    return this.postKeyed(`/cash-registers/${id}/movements`, body, key);
  }
  cashMovements(id: string, query: Query = {}): Observable<Paged<CashMovement>> {
    return this.get(`/cash-registers/${id}/movements`, query);
  }
  cashSessions(id: string, query: Query = {}): Observable<Paged<CashSession>> {
    return this.get(`/cash-registers/${id}/sessions`, query);
  }

  // Asistencia
  attendance(query: Query = {}): Observable<Paged<Attendance>> {
    return this.get('/attendance', query);
  }
  validateAccess(body: { branchId: string; memberId?: string | null; memberCode?: string | null; qrToken?: string | null }): Observable<AccessDecision> {
    return this.http.post<AccessDecision>(`${API_BASE}/attendance/validate-access`, body);
  }
  checkIn(body: { branchId: string; memberId?: string | null; memberCode?: string | null; qrToken?: string | null; overrideReason?: string | null }): Observable<Attendance> {
    return this.http.post<Attendance>(`${API_BASE}/attendance/check-in`, body);
  }
  checkOut(body: { memberId?: string | null; memberCode?: string | null; qrToken?: string | null }): Observable<Attendance> {
    return this.http.post<Attendance>(`${API_BASE}/attendance/check-out`, body);
  }
}
