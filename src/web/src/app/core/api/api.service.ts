import { HttpClient, HttpHeaders, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import type { Observable } from 'rxjs';
import type {
  AccessDecision,
  AttendanceEntryRequest,
  CancelSeriesResult,
  ClassAttendance,
  ClassReservation,
  ClassRoster,
  ClassSeries,
  ClassSession,
  CreateReservationRequest,
  CreateSeriesRequest,
  CreateSessionRequest,
  GroupClass,
  GroupClassRequest,
  AcquisitionsReport,
  AnalyticsContext,
  AttendanceReport,
  BranchComparison,
  CashReport,
  ClassCancellationsReport,
  ClassOccupancyReport,
  CollectionsReport,
  Dashboard,
  DashboardSeries,
  ExpirationsReport,
  ExportRequest,
  InboxNotification,
  MembersSummaryReport,
  OutstandingReport,
  PaymentMethodsReport,
  RenewalsReport,
  ReportExport,
  RetentionReport,
  ReversalsReport,
  MyClassSession,
  NotificationPreference,
  NotificationSummary,
  SeriesRun,
  UpdateSessionRequest,
  Assessment,
  AssessmentComparison,
  AssessmentPoint,
  AssessmentRequest,
  Attendance,
  CloseGoalRequest,
  CreateWorkoutSessionRequest,
  DietaryProfile,
  DietaryProfileRequest,
  ExerciseProgress,
  Goal,
  GoalRequest,
  LogMealRequest,
  MealLog,
  MemberNutritionPlan,
  MemberTrainingPlan,
  NutritionAdherence,
  NutritionAssignment,
  NutritionLogs,
  NutritionPlanDetail,
  NutritionPlanRequest,
  NutritionPlanSummary,
  NutritionPlanVersion,
  PhotoConsent,
  ProgressPhoto,
  RevokePhotoConsentResult,
  TrainingAdherence,
  TrainingAssignment,
  TrainingPlanDetail,
  TrainingPlanRequest,
  TrainingPlanSummary,
  TrainingPlanVersion,
  WorkoutSession,
  AvailabilitySlotRequest,
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
  CreateTrainerRequest,
  CreateUserRequest,
  EnrollRequest,
  Exercise,
  ExerciseRequest,
  Member,
  MemberAccess,
  Membership,
  Notification,
  NotificationSettings,
  MembershipEvent,
  MyAttendance,
  MyMember,
  MyMembership,
  MyPayment,
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
  Trainer,
  TrainerAssignment,
  TrainerAvailability,
  TrainerNote,
  UpdateBranchRequest,
  UpdateCommercialSettingsRequest,
  UpdateMemberRequest,
  UpdateNotificationSettingsRequest,
  UpdateRoleRequest,
  UpdateTrainerRequest,
  User,
} from './models';

export const API_BASE = '/api/v1';

type Scalar = string | number | boolean | null | undefined;
type Query = Record<string, Scalar | readonly string[]>;

function toParams(query: Query): HttpParams {
  let params = new HttpParams();
  for (const [key, value] of Object.entries(query)) {
    if (Array.isArray(value)) {
      // Las listas (por ejemplo las sucursales) se envían repitiendo el parámetro: branchId=a&branchId=b.
      for (const item of value) {
        params = params.append(key, String(item));
      }
    } else if (value !== null && value !== undefined && value !== '') {
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
  /** Acceso del alumno a su portal. La clave temporal que devuelven se muestra una sola vez. */
  grantMemberAccess(id: string): Observable<MemberAccess> {
    return this.http.post<MemberAccess>(`${API_BASE}/members/${id}/access`, {});
  }
  resetMemberAccess(id: string): Observable<MemberAccess> {
    return this.http.post<MemberAccess>(`${API_BASE}/members/${id}/access/reset`, {});
  }
  setMemberAccessActive(id: string, active: boolean): Observable<Member> {
    return this.http.patch<Member>(`${API_BASE}/members/${id}/access`, { active });
  }
  changePassword(currentPassword: string, newPassword: string): Observable<void> {
    return this.http.post<void>(`${API_BASE}/auth/change-password`, { currentPassword, newPassword });
  }

  // ---- Biblioteca de ejercicios ----
  exercises(query: Query = {}): Observable<Paged<Exercise>> {
    return this.get('/exercises', query);
  }
  globalExercises(query: Query = {}): Observable<Paged<Exercise>> {
    return this.get('/platform/exercises', query);
  }
  saveExercise(body: ExerciseRequest, id: string | null, global: boolean): Observable<Exercise> {
    const base = global ? '/platform/exercises' : '/exercises';
    return id ? this.http.put<Exercise>(`${API_BASE}${base}/${id}`, body) : this.http.post<Exercise>(`${API_BASE}${base}`, body);
  }
  setExerciseStatus(id: string, isActive: boolean, global: boolean): Observable<Exercise> {
    return this.http.patch<Exercise>(`${API_BASE}${global ? '/platform/exercises' : '/exercises'}/${id}/status`, { isActive });
  }
  duplicateExercise(id: string): Observable<Exercise> {
    return this.http.post<Exercise>(`${API_BASE}/exercises/${id}/duplicate`, {});
  }

  // ---- Entrenadores (módulo de bienestar) ----
  trainers(query: Query = {}): Observable<Paged<Trainer>> {
    return this.get('/trainers', query);
  }
  trainer(id: string): Observable<Trainer> {
    return this.get(`/trainers/${id}`);
  }
  myTrainer(): Observable<Trainer> {
    return this.get('/trainers/me');
  }
  createTrainer(body: CreateTrainerRequest): Observable<Trainer> {
    return this.http.post<Trainer>(`${API_BASE}/trainers`, body);
  }
  updateTrainer(id: string, body: UpdateTrainerRequest): Observable<Trainer> {
    return this.http.put<Trainer>(`${API_BASE}/trainers/${id}`, body);
  }
  setTrainerStatus(id: string, isActive: boolean): Observable<Trainer> {
    return this.http.patch<Trainer>(`${API_BASE}/trainers/${id}/status`, { isActive });
  }
  trainerAvailability(id: string): Observable<TrainerAvailability[]> {
    return this.get(`/trainers/${id}/availability`);
  }
  setTrainerAvailability(id: string, slots: AvailabilitySlotRequest[]): Observable<TrainerAvailability[]> {
    return this.http.put<TrainerAvailability[]>(`${API_BASE}/trainers/${id}/availability`, { slots });
  }
  trainerMembers(id: string, query: Query = {}): Observable<Paged<TrainerAssignment>> {
    return this.get(`/trainers/${id}/members`, query);
  }
  assignMember(trainerId: string, memberId: string, reason: string | null): Observable<TrainerAssignment> {
    return this.http.post<TrainerAssignment>(`${API_BASE}/trainers/${trainerId}/members`, { memberId, reason });
  }
  unassignMember(trainerId: string, memberId: string, reason: string): Observable<TrainerAssignment> {
    return this.http.post<TrainerAssignment>(`${API_BASE}/trainers/${trainerId}/members/${memberId}/unassign`, { reason });
  }
  memberTrainerAssignments(memberId: string): Observable<TrainerAssignment[]> {
    return this.get(`/members/${memberId}/trainer-assignments`);
  }
  trainerNotes(memberId: string, query: Query = {}): Observable<Paged<TrainerNote>> {
    return this.get(`/members/${memberId}/trainer-notes`, query);
  }
  createTrainerNote(memberId: string, text: string): Observable<TrainerNote> {
    return this.http.post<TrainerNote>(`${API_BASE}/members/${memberId}/trainer-notes`, { text });
  }

  // ---- Portal del alumno (todo sale de la sesión; no se envía ningún identificador) ----
  myMember(): Observable<MyMember> {
    return this.get('/me/member');
  }
  myMemberships(query: Query = {}): Observable<Paged<MyMembership>> {
    return this.get('/me/memberships', query);
  }
  myPayments(query: Query = {}): Observable<Paged<MyPayment>> {
    return this.get('/me/payments', query);
  }
  myAttendance(query: Query = {}): Observable<Paged<MyAttendance>> {
    return this.get('/me/attendance', query);
  }

  /** Registra o revoca el consentimiento del alumno para recibir WhatsApp (LOPDP). */
  setWhatsAppConsent(id: string, granted: boolean): Observable<Member> {
    return this.http.put<Member>(`${API_BASE}/members/${id}/whatsapp-consent`, { granted });
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
  notifications(query: Query = {}): Observable<Paged<Notification>> {
    return this.get('/notifications', query);
  }
  notificationSettings(): Observable<NotificationSettings> {
    return this.get('/notification-settings');
  }
  updateNotificationSettings(body: UpdateNotificationSettingsRequest): Observable<NotificationSettings> {
    return this.http.put<NotificationSettings>(`${API_BASE}/notification-settings`, body);
  }

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

  // Rutinas
  trainingPlans(query: Query = {}): Observable<Paged<TrainingPlanSummary>> {
    return this.get('/training-plans', query);
  }
  trainingPlan(id: string, version?: number): Observable<TrainingPlanDetail> {
    return this.get(`/training-plans/${id}`, { version });
  }
  trainingPlanVersions(id: string): Observable<TrainingPlanVersion[]> {
    return this.get(`/training-plans/${id}/versions`);
  }
  createTrainingPlan(body: TrainingPlanRequest): Observable<TrainingPlanDetail> {
    return this.http.post<TrainingPlanDetail>(`${API_BASE}/training-plans`, body);
  }
  updateTrainingPlan(id: string, body: TrainingPlanRequest): Observable<TrainingPlanDetail> {
    return this.http.put<TrainingPlanDetail>(`${API_BASE}/training-plans/${id}`, body);
  }
  duplicateTrainingPlan(id: string): Observable<TrainingPlanDetail> {
    return this.http.post<TrainingPlanDetail>(`${API_BASE}/training-plans/${id}/duplicate`, {});
  }
  setTrainingPlanStatus(id: string, active: boolean): Observable<TrainingPlanSummary> {
    return this.http.patch<TrainingPlanSummary>(`${API_BASE}/training-plans/${id}/status`, { active });
  }
  assignTrainingPlan(planId: string, body: { memberId: string; startDate?: string | null; endDate?: string | null }): Observable<TrainingAssignment> {
    return this.http.post<TrainingAssignment>(`${API_BASE}/training-plans/${planId}/assignments`, body);
  }
  endTrainingAssignment(id: string, reason: string): Observable<TrainingAssignment> {
    return this.http.post<TrainingAssignment>(`${API_BASE}/training-assignments/${id}/end`, { reason });
  }
  memberTrainingPlan(memberId: string): Observable<MemberTrainingPlan> {
    return this.get(`/members/${memberId}/training-plan`);
  }
  memberTrainingAssignments(memberId: string): Observable<TrainingAssignment[]> {
    return this.get(`/members/${memberId}/training-assignments`);
  }
  createWorkoutSession(body: CreateWorkoutSessionRequest): Observable<WorkoutSession> {
    return this.http.post<WorkoutSession>(`${API_BASE}/workout-sessions`, body);
  }
  workoutSessions(memberId: string, query: Query = {}): Observable<Paged<WorkoutSession>> {
    return this.get(`/members/${memberId}/workout-sessions`, query);
  }
  addTrainerComment(sessionId: string, comment: string): Observable<WorkoutSession> {
    return this.http.post<WorkoutSession>(`${API_BASE}/workout-sessions/${sessionId}/trainer-comment`, { comment });
  }
  exerciseProgress(memberId: string, exerciseId: string): Observable<ExerciseProgress> {
    return this.get(`/members/${memberId}/exercise-progress`, { exerciseId });
  }
  trainingAdherence(memberId: string, query: Query = {}): Observable<TrainingAdherence> {
    return this.get(`/members/${memberId}/training-adherence`, query);
  }

  // Nutrición
  nutritionPlans(query: Query = {}): Observable<Paged<NutritionPlanSummary>> {
    return this.get('/nutrition-plans', query);
  }
  nutritionPlan(id: string, version?: number): Observable<NutritionPlanDetail> {
    return this.get(`/nutrition-plans/${id}`, { version });
  }
  nutritionPlanVersions(id: string): Observable<NutritionPlanVersion[]> {
    return this.get(`/nutrition-plans/${id}/versions`);
  }
  createNutritionPlan(body: NutritionPlanRequest): Observable<NutritionPlanDetail> {
    return this.http.post<NutritionPlanDetail>(`${API_BASE}/nutrition-plans`, body);
  }
  updateNutritionPlan(id: string, body: NutritionPlanRequest): Observable<NutritionPlanDetail> {
    return this.http.put<NutritionPlanDetail>(`${API_BASE}/nutrition-plans/${id}`, body);
  }
  duplicateNutritionPlan(id: string): Observable<NutritionPlanDetail> {
    return this.http.post<NutritionPlanDetail>(`${API_BASE}/nutrition-plans/${id}/duplicate`, {});
  }
  setNutritionPlanStatus(id: string, active: boolean): Observable<NutritionPlanSummary> {
    return this.http.patch<NutritionPlanSummary>(`${API_BASE}/nutrition-plans/${id}/status`, { active });
  }
  assignNutritionPlan(planId: string, body: { memberId: string; startDate?: string | null; endDate?: string | null }): Observable<NutritionAssignment> {
    return this.http.post<NutritionAssignment>(`${API_BASE}/nutrition-plans/${planId}/assignments`, body);
  }
  endNutritionAssignment(id: string, reason: string): Observable<NutritionAssignment> {
    return this.http.post<NutritionAssignment>(`${API_BASE}/nutrition-assignments/${id}/end`, { reason });
  }
  memberNutritionPlan(memberId: string): Observable<MemberNutritionPlan> {
    return this.get(`/members/${memberId}/nutrition-plan`);
  }
  dietaryProfile(memberId: string, history = false): Observable<DietaryProfile[]> {
    return this.get(`/members/${memberId}/dietary-profile`, { history });
  }
  saveDietaryProfile(memberId: string, body: DietaryProfileRequest): Observable<DietaryProfile> {
    return this.http.post<DietaryProfile>(`${API_BASE}/members/${memberId}/dietary-profile`, body);
  }
  logMeal(body: LogMealRequest): Observable<MealLog> {
    return this.http.post<MealLog>(`${API_BASE}/meal-logs`, body);
  }
  mealLogs(memberId: string, query: Query = {}): Observable<NutritionLogs> {
    return this.get(`/members/${memberId}/meal-logs`, query);
  }
  nutritionAdherence(memberId: string, query: Query = {}): Observable<NutritionAdherence> {
    return this.get(`/members/${memberId}/nutrition-adherence`, query);
  }

  // Evaluaciones físicas y objetivos
  assessments(memberId: string, query: Query = {}): Observable<Paged<Assessment>> {
    return this.get(`/members/${memberId}/assessments`, query);
  }
  createAssessment(memberId: string, body: AssessmentRequest): Observable<Assessment> {
    return this.http.post<Assessment>(`${API_BASE}/members/${memberId}/assessments`, body);
  }
  assessmentProgress(memberId: string): Observable<AssessmentPoint[]> {
    return this.get(`/members/${memberId}/assessments/progress`);
  }
  compareAssessments(memberId: string, a: string, b: string): Observable<AssessmentComparison> {
    return this.get(`/members/${memberId}/assessments/compare`, { a, b });
  }
  goals(memberId: string, status?: string): Observable<Goal[]> {
    return this.get(`/members/${memberId}/goals`, { status });
  }
  createGoal(memberId: string, body: GoalRequest): Observable<Goal> {
    return this.http.post<Goal>(`${API_BASE}/members/${memberId}/goals`, body);
  }
  closeGoal(id: string, body: CloseGoalRequest): Observable<Goal> {
    return this.http.post<Goal>(`${API_BASE}/goals/${id}/close`, body);
  }

  // Fotos de progreso (privadas: el archivo solo sale por la API con el token de la sesión)
  photoConsent(memberId: string): Observable<PhotoConsent> {
    return this.get(`/members/${memberId}/photo-consent`);
  }
  grantPhotoConsent(memberId: string, guardianName: string | null): Observable<PhotoConsent> {
    return this.http.post<PhotoConsent>(`${API_BASE}/members/${memberId}/photo-consent/grant`, { guardianName });
  }
  revokePhotoConsent(memberId: string): Observable<RevokePhotoConsentResult> {
    return this.http.post<RevokePhotoConsentResult>(`${API_BASE}/members/${memberId}/photo-consent/revoke`, {});
  }
  progressPhotos(memberId: string): Observable<ProgressPhoto[]> {
    return this.get(`/members/${memberId}/progress-photos`);
  }
  uploadProgressPhoto(memberId: string, file: File, takenOn: string, angle: string): Observable<ProgressPhoto> {
    const form = new FormData();
    form.append('file', file);
    form.append('takenOn', takenOn);
    form.append('angle', angle);
    return this.http.post<ProgressPhoto>(`${API_BASE}/members/${memberId}/progress-photos`, form);
  }
  progressPhotoContent(id: string): Observable<Blob> {
    return this.http.get(`${API_BASE}/progress-photos/${id}/content`, { responseType: 'blob' });
  }
  deleteProgressPhoto(id: string): Observable<void> {
    return this.http.delete<void>(`${API_BASE}/progress-photos/${id}`);
  }

  // Clases grupales: definición de la clase
  groupClasses(query: Query = {}): Observable<Paged<GroupClass>> {
    return this.get('/group-classes', query);
  }
  createGroupClass(body: GroupClassRequest): Observable<GroupClass> {
    return this.http.post<GroupClass>(`${API_BASE}/group-classes`, body);
  }
  updateGroupClass(id: string, body: GroupClassRequest): Observable<GroupClass> {
    return this.http.put<GroupClass>(`${API_BASE}/group-classes/${id}`, body);
  }
  setGroupClassStatus(id: string, isActive: boolean): Observable<GroupClass> {
    return this.http.patch<GroupClass>(`${API_BASE}/group-classes/${id}/status`, { isActive });
  }

  // Horario: sesiones y series
  classSessions(query: Query = {}): Observable<Paged<ClassSession>> {
    return this.get('/class-sessions', query);
  }
  classSession(id: string): Observable<ClassSession> {
    return this.get(`/class-sessions/${id}`);
  }
  createClassSession(body: CreateSessionRequest): Observable<ClassSession> {
    return this.http.post<ClassSession>(`${API_BASE}/class-sessions`, body);
  }
  updateClassSession(id: string, body: UpdateSessionRequest): Observable<ClassSession> {
    return this.http.patch<ClassSession>(`${API_BASE}/class-sessions/${id}`, body);
  }
  cancelClassSession(id: string, reason: string): Observable<ClassSession> {
    return this.http.post<ClassSession>(`${API_BASE}/class-sessions/${id}/cancel`, { reason });
  }
  completeClassSession(id: string, markPendingAsNoShow: boolean): Observable<ClassRoster> {
    return this.http.post<ClassRoster>(`${API_BASE}/class-sessions/${id}/complete`, { markPendingAsNoShow });
  }
  classSeriesList(query: Query = {}): Observable<Paged<ClassSeries>> {
    return this.get('/class-sessions/recurrences', query);
  }
  createClassSeries(body: CreateSeriesRequest): Observable<SeriesRun> {
    return this.http.post<SeriesRun>(`${API_BASE}/class-sessions/recurrences`, body);
  }
  generateClassSeries(id: string, endDate: string | null): Observable<SeriesRun> {
    return this.http.post<SeriesRun>(`${API_BASE}/class-sessions/recurrences/${id}/generate`, { endDate });
  }
  cancelClassSeries(id: string, reason: string): Observable<CancelSeriesResult> {
    return this.http.post<CancelSeriesResult>(`${API_BASE}/class-sessions/recurrences/${id}/cancel`, { reason });
  }

  // Reservas, lista de espera y asistencia
  classReservations(query: Query = {}): Observable<Paged<ClassReservation>> {
    return this.get('/class-reservations', query);
  }
  /** Reservar exige la clave de idempotencia: reintentar la misma solicitud no reserva dos veces. */
  reserveClass(body: CreateReservationRequest, key: string): Observable<ClassReservation> {
    return this.postKeyed('/class-reservations', body, key);
  }
  cancelClassReservation(id: string, reason: string | null): Observable<ClassReservation> {
    return this.http.post<ClassReservation>(`${API_BASE}/class-reservations/${id}/cancel`, { reason });
  }
  acceptClassOffer(id: string): Observable<ClassReservation> {
    return this.http.post<ClassReservation>(`${API_BASE}/class-reservations/${id}/accept`, {});
  }
  declineClassOffer(id: string): Observable<ClassReservation> {
    return this.http.post<ClassReservation>(`${API_BASE}/class-reservations/${id}/decline`, {});
  }
  classRoster(sessionId: string): Observable<ClassRoster> {
    return this.get(`/class-sessions/${sessionId}/roster`);
  }
  recordClassAttendance(sessionId: string, entries: AttendanceEntryRequest[]): Observable<ClassRoster> {
    return this.http.put<ClassRoster>(`${API_BASE}/class-sessions/${sessionId}/attendance`, { entries });
  }
  classAttendance(query: Query = {}): Observable<Paged<ClassAttendance>> {
    return this.get('/class-attendance', query);
  }

  // Portal del alumno: agenda, reservas propias e historial de clases
  mySessions(query: Query = {}): Observable<Paged<MyClassSession>> {
    return this.get('/me/class-sessions', query);
  }
  myClassReservations(query: Query = {}): Observable<Paged<ClassReservation>> {
    return this.get('/me/class-reservations', query);
  }
  reserveMyClass(body: { sessionId: string; joinWaitlist: boolean }, key: string): Observable<ClassReservation> {
    return this.postKeyed('/me/class-reservations', body, key);
  }
  cancelMyClassReservation(id: string, reason: string | null): Observable<ClassReservation> {
    return this.http.post<ClassReservation>(`${API_BASE}/me/class-reservations/${id}/cancel`, { reason });
  }
  acceptMyClassOffer(id: string): Observable<ClassReservation> {
    return this.http.post<ClassReservation>(`${API_BASE}/me/class-reservations/${id}/accept`, {});
  }
  declineMyClassOffer(id: string): Observable<ClassReservation> {
    return this.http.post<ClassReservation>(`${API_BASE}/me/class-reservations/${id}/decline`, {});
  }
  myClassAttendance(query: Query = {}): Observable<Paged<ClassAttendance>> {
    return this.get('/me/class-attendance', query);
  }

  // Analítica: contexto, dashboard, reportes y exportaciones. El servidor calcula todo; aquí solo se pide y se muestra.
  analyticsContext(query: Query = {}): Observable<AnalyticsContext> {
    return this.get('/analytics/context', query);
  }
  dashboard(query: Query = {}): Observable<Dashboard> {
    return this.get('/analytics/dashboard', query);
  }
  dashboardSeries(query: Query = {}): Observable<DashboardSeries> {
    return this.get('/analytics/dashboard/comparison', query);
  }
  collectionsReport(query: Query = {}): Observable<CollectionsReport> {
    return this.get('/reports/financial/collections', query);
  }
  paymentMethodsReport(query: Query = {}): Observable<PaymentMethodsReport> {
    return this.get('/reports/financial/payment-methods', query);
  }
  outstandingReport(query: Query = {}): Observable<OutstandingReport> {
    return this.get('/reports/financial/outstanding-balances', query);
  }
  cashReport(query: Query = {}): Observable<CashReport> {
    return this.get('/reports/financial/cash-registers', query);
  }
  reversalsReport(query: Query = {}): Observable<ReversalsReport> {
    return this.get('/reports/financial/reversals', query);
  }
  membersSummaryReport(query: Query = {}): Observable<MembersSummaryReport> {
    return this.get('/reports/members/summary', query);
  }
  acquisitionsReport(query: Query = {}): Observable<AcquisitionsReport> {
    return this.get('/reports/members/acquisitions', query);
  }
  retentionReport(query: Query = {}): Observable<RetentionReport> {
    return this.get('/reports/members/retention', query);
  }
  renewalsReport(query: Query = {}): Observable<RenewalsReport> {
    return this.get('/reports/members/renewals', query);
  }
  expirationsReport(query: Query = {}): Observable<ExpirationsReport> {
    return this.get('/reports/members/expirations', query);
  }
  attendanceReport(query: Query = {}): Observable<AttendanceReport> {
    return this.get('/reports/attendance', query);
  }
  classOccupancyReport(query: Query = {}): Observable<ClassOccupancyReport> {
    return this.get('/reports/classes/occupancy', query);
  }
  classCancellationsReport(query: Query = {}): Observable<ClassCancellationsReport> {
    return this.get('/reports/classes/cancellations', query);
  }
  branchComparison(query: Query = {}): Observable<BranchComparison> {
    return this.get('/reports/branches/comparison', query);
  }
  createExport(body: ExportRequest): Observable<ReportExport> {
    return this.http.post<ReportExport>(`${API_BASE}/reports/exports`, body);
  }
  exports(query: Query = {}): Observable<Paged<ReportExport>> {
    return this.get('/reports/exports', query);
  }
  /** El archivo se baja con la sesión del usuario (no hay enlaces públicos): el navegador lo guarda desde un blob. */
  downloadExport(id: string): Observable<Blob> {
    return this.http.get(`${API_BASE}/reports/exports/${id}/download`, { responseType: 'blob' });
  }

  // Bandeja de avisos del propio usuario
  inbox(query: Query = {}): Observable<Paged<InboxNotification>> {
    return this.get('/me/notifications', query);
  }
  inboxSummary(): Observable<NotificationSummary> {
    return this.get('/me/notifications/summary');
  }
  markInboxRead(id: string): Observable<InboxNotification> {
    return this.http.patch<InboxNotification>(`${API_BASE}/me/notifications/${id}/read`, {});
  }
  markAllInboxRead(): Observable<{ marked: number }> {
    return this.http.post<{ marked: number }>(`${API_BASE}/me/notifications/read-all`, {});
  }
  notificationPreferences(): Observable<NotificationPreference[]> {
    return this.get('/me/notification-preferences');
  }
  updateNotificationPreferences(preferences: { type: string; enabled: boolean }[]): Observable<NotificationPreference[]> {
    return this.http.put<NotificationPreference[]>(`${API_BASE}/me/notification-preferences`, { preferences });
  }
}
