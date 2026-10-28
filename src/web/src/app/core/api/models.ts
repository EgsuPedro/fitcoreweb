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

// Módulo comercial (Fase 3)
export type Member = Schemas['MemberDto'];
export type CreateMemberRequest = Schemas['CreateMemberRequest'];
export type UpdateMemberRequest = Schemas['UpdateMemberRequest'];
export type Plan = Schemas['PlanDto'];
export type PlanRequest = Schemas['PlanRequest'];
export type Promotion = Schemas['PromotionDto'];
export type PromotionRequest = Schemas['PromotionRequest'];
export type CommercialSettings = Schemas['CommercialSettingsDto'];
export type UpdateCommercialSettingsRequest = Schemas['UpdateCommercialSettingsRequest'];
export type TaxRate = Schemas['TaxRateDto'];
export type CreateTaxRateRequest = Schemas['CreateTaxRateRequest'];
export type Membership = Schemas['MembershipDto'];
export type MembershipEvent = Schemas['MembershipEventDto'];
export type EnrollRequest = Schemas['EnrollRequest'];
export type RenewRequest = Schemas['RenewRequest'];
export type Obligation = Schemas['ObligationDto2'];
export type CreateObligationRequest = Schemas['CreateObligationRequest'];
export type Payment = Schemas['PaymentDto'];
export type PaymentLine = Schemas['PaymentLineDto'];
export type CreatePaymentRequest = Schemas['CreatePaymentRequest'];
export type Receipt = Schemas['ReceiptDto'];
export type ReceivablesSummary = Schemas['ReceivablesSummaryDto'];
export type CashRegister = Schemas['CashRegisterDto'];
export type CashSession = Schemas['CashSessionDto'];
export type CashMovement = Schemas['CashMovementDto'];
export type Attendance = Schemas['AttendanceDto'];
export type MemberAccess = Schemas['MemberAccessDto'];
export type Exercise = Schemas['ExerciseDto'];
export type ExerciseRequest = Schemas['ExerciseRequest'];
export type Trainer = Schemas['TrainerDto'];
export type TrainerAvailability = Schemas['AvailabilityDto'];
export type TrainerAssignment = Schemas['AssignmentDto'];
export type TrainerNote = Schemas['TrainerNoteDto'];
export type AvailabilitySlotRequest = Schemas['AvailabilitySlotRequest'];
export type CreateTrainerRequest = Schemas['CreateTrainerRequest'];
export type UpdateTrainerRequest = Schemas['UpdateTrainerRequest'];
// Bienestar (Fase 4): rutinas, nutrición, evaluaciones, objetivos, adherencia y fotos
export type TrainingPlanSummary = Schemas['PlanSummaryDto'];
export type TrainingPlanDetail = Schemas['PlanDetailDto'];
export type TrainingPlanVersion = Schemas['PlanVersionDto'];
export type TrainingPlanRequest = Schemas['TrainingPlanRequest'];
export type TrainingAssignment = Schemas['TrainingAssignmentDto'];
export type MemberTrainingPlan = Schemas['MemberTrainingPlanDto'];
export type WorkoutSession = Schemas['WorkoutSessionDto'];
export type CreateWorkoutSessionRequest = Schemas['CreateWorkoutSessionRequest'];
export type ExerciseProgress = Schemas['ExerciseProgressDto'];
export type NutritionPlanSummary = Schemas['NutritionPlanSummaryDto'];
export type NutritionPlanDetail = Schemas['NutritionPlanDetailDto'];
export type NutritionPlanVersion = Schemas['NutritionVersionDto'];
export type NutritionPlanRequest = Schemas['NutritionPlanRequest'];
export type NutritionAssignment = Schemas['NutritionAssignmentDto'];
export type MemberNutritionPlan = Schemas['MemberNutritionPlanDto'];
export type DietaryProfile = Schemas['DietaryProfileDto'];
export type DietaryProfileRequest = Schemas['DietaryProfileRequest'];
export type LogMealRequest = Schemas['LogMealRequest'];
export type MealLog = Schemas['MealLogDto'];
export type NutritionLogs = Schemas['NutritionLogsDto'];
export type Assessment = Schemas['AssessmentDto'];
export type AssessmentRequest = Schemas['AssessmentRequest'];
export type AssessmentPoint = Schemas['AssessmentPointDto'];
export type AssessmentComparison = Schemas['AssessmentComparisonDto'];
export type Goal = Schemas['GoalDto'];
export type GoalRequest = Schemas['GoalRequest'];
export type CloseGoalRequest = Schemas['CloseGoalRequest'];
export type TrainingAdherence = Schemas['TrainingAdherenceDto'];
export type NutritionAdherence = Schemas['NutritionAdherenceDto'];
export type PhotoConsent = Schemas['PhotoConsentDto'];
export type RevokePhotoConsentResult = Schemas['RevokePhotoConsentResult'];
export type ProgressPhoto = Schemas['ProgressPhotoDto'];
// Clases grupales (Fase 5): clases, sesiones, series, reservas, lista de espera, asistencia y bandeja de avisos
export type GroupClass = Schemas['GroupClassDto'];
export type GroupClassRequest = Schemas['GroupClassRequest'];
export type ActivityType = Schemas['ActivityType'];
export type ClassLevel = Schemas['ClassLevel'];
export type ClassSession = Schemas['ClassSessionDto'];
export type CreateSessionRequest = Schemas['CreateSessionRequest'];
export type UpdateSessionRequest = Schemas['UpdateSessionRequest'];
export type ClassSeries = Schemas['ClassSeriesDto'];
export type CreateSeriesRequest = Schemas['CreateSeriesRequest'];
export type SeriesRun = Schemas['SeriesRunDto'];
export type CancelSeriesResult = Schemas['CancelSeriesResultDto'];
export type ClassReservation = Schemas['ReservationDto'];
export type CreateReservationRequest = Schemas['CreateReservationRequest'];
export type RosterEntry = Schemas['RosterEntryDto'];
// El generador tipa como unknown[] las listas repetidas de la misma clase; se precisan aquí.
export type ClassRoster = Omit<Schemas['RosterDto'], 'offers' | 'waitlist'> & { offers: RosterEntry[]; waitlist: RosterEntry[] };
export type AttendanceEntryRequest = Schemas['AttendanceEntryRequest'];
export type AttendanceOutcome = Schemas['AttendanceOutcome'];
export type ClassAttendance = Schemas['ClassAttendanceDto'];
export type MyClassSession = Schemas['MyClassSessionDto'];
export type InboxNotification = Schemas['InboxNotificationDto'];
export type NotificationPreference = Schemas['NotificationPreferenceDto'];
export type NotificationSummary = Schemas['NotificationSummaryDto'];
export type MyMember = Schemas['MyMemberDto'];
export type MyMembership = Schemas['MyMembershipDto'];
export type MyPayment = Schemas['MyPaymentDto'];
export type MyAttendance = Schemas['MyAttendanceDto'];
export type Notification = Schemas['NotificationDto'];
export type NotificationSettings = Schemas['NotificationSettingsDto'];
export type UpdateNotificationSettingsRequest = Schemas['UpdateNotificationSettingsRequest'];
export type AccessDecision = Schemas['AccessDecisionDto'];
export type QrToken = Schemas['QrTokenDto'];
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
