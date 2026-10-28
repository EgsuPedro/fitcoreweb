namespace FitCore.Platform.Domain;

/// <param name="Sensitive">Datos de salud o personales delicados (nutrición, evaluaciones, fotos…): no se conceden por defecto a propietario ni administradores.</param>
public sealed record PermissionDefinition(string Code, string Module, string Description, bool Sensitive = false);

/// <summary>Catálogo fijo de permisos. Cada módulo agrega los suyos al implementarse.</summary>
public static class Permissions
{
    public const string PlatformTenantsManage = "platform.tenants.manage";
    public const string PlatformTaxesManage = "platform.taxes.manage";
    public const string PlatformExercisesManage = "platform.exercises.manage";

    public const string OrganizationRead = "organization.read";
    public const string OrganizationUpdate = "organization.update";

    public const string BranchesRead = "branches.read";
    public const string BranchesCreate = "branches.create";
    public const string BranchesUpdate = "branches.update";

    public const string UsersRead = "users.read";
    public const string UsersCreate = "users.create";
    public const string UsersUpdate = "users.update";

    public const string RolesRead = "roles.read";
    public const string RolesManage = "roles.manage";

    public const string AuditRead = "audit.read";

    public const string MembersRead = "members.read";
    public const string MembersCreate = "members.create";
    public const string MembersUpdate = "members.update";
    public const string MembersStatus = "members.status";

    public const string PlansRead = "plans.read";
    public const string PlansManage = "plans.manage";
    public const string PromotionsRead = "promotions.read";
    public const string PromotionsManage = "promotions.manage";
    public const string PromotionsApply = "promotions.apply";

    public const string MembershipsRead = "memberships.read";
    public const string MembershipsCreate = "memberships.create";
    public const string MembershipsRenew = "memberships.renew";
    public const string MembershipsFreeze = "memberships.freeze";
    public const string MembershipsCancel = "memberships.cancel";

    public const string ObligationsRead = "obligations.read";
    public const string ObligationsCreate = "obligations.create";
    public const string ObligationsVoid = "obligations.void";

    public const string PaymentsRead = "payments.read";
    public const string PaymentsCreate = "payments.create";
    public const string PaymentsConfirm = "payments.confirm";
    public const string PaymentsReverse = "payments.reverse";

    public const string CashRead = "cash.read";
    public const string CashManage = "cash.manage";
    public const string CashOperate = "cash.operate";

    public const string AttendanceRead = "attendance.read";
    public const string AttendanceRegister = "attendance.register";
    public const string AttendanceOverride = "attendance.override";

    public const string CommercialSettingsManage = "commercial.settings.manage";

    public const string NotificationsRead = "notifications.read";

    public const string ExercisesRead = "exercises.read";
    public const string ExercisesManage = "exercises.manage";

    public const string TrainingPlansRead = "training.plans.read";
    public const string TrainingPlansManage = "training.plans.manage";
    public const string TrainingMembersManage = "training.members.manage";
    public const string TrainingMemberRead = "training.member.read";
    public const string TrainingSessionsLog = "training.sessions.log";

    public const string NutritionPlansRead = "nutrition.plans.read";
    public const string NutritionPlansManage = "nutrition.plans.manage";
    public const string NutritionMembersManage = "nutrition.members.manage";
    public const string NutritionMemberRead = "nutrition.member.read";
    public const string NutritionProfileManage = "nutrition.profile.manage";
    public const string NutritionLogsRecord = "nutrition.logs.record";

    public const string AssessmentsRead = "wellness.assessments.read";
    public const string AssessmentsManage = "wellness.assessments.manage";
    public const string GoalsRead = "wellness.goals.read";
    public const string GoalsManage = "wellness.goals.manage";
    public const string PhotosRead = "wellness.photos.read";
    public const string PhotosManage = "wellness.photos.manage";

    public const string TrainersRead = "trainers.read";
    public const string TrainersManage = "trainers.manage";
    public const string TrainersNotes = "trainers.notes";
    public const string WellnessMembersAll = "wellness.members.all";

    public const string GroupClassesRead = "group-classes.read";
    public const string GroupClassesManage = "group-classes.manage";
    public const string ClassSessionsRead = "class-sessions.read";
    public const string ClassSessionsManage = "class-sessions.manage";

    public const string MembersAccess = "members.access";
    public const string MeRead = "me.read";

    public static readonly IReadOnlyList<PermissionDefinition> All =
    [
        new(PlatformTenantsManage, "platform", "Administrar gimnasios (solo equipo de plataforma)"),
        new(PlatformTaxesManage, "platform", "Administrar las tarifas de IVA (solo equipo de plataforma)"),
        new(PlatformExercisesManage, "platform", "Administrar el catálogo base de ejercicios (solo equipo de plataforma)"),
        new(OrganizationRead, "organization", "Ver datos del gimnasio"),
        new(OrganizationUpdate, "organization", "Editar datos del gimnasio"),
        new(BranchesRead, "branches", "Ver sucursales"),
        new(BranchesCreate, "branches", "Crear sucursales"),
        new(BranchesUpdate, "branches", "Editar sucursales"),
        new(UsersRead, "users", "Ver usuarios"),
        new(UsersCreate, "users", "Crear usuarios"),
        new(UsersUpdate, "users", "Editar usuarios, roles y sucursales"),
        new(RolesRead, "roles", "Ver roles y permisos"),
        new(RolesManage, "roles", "Crear, editar y eliminar roles"),
        new(AuditRead, "audit", "Consultar la auditoría"),
        new(MembersRead, "members", "Ver alumnos"),
        new(MembersCreate, "members", "Registrar alumnos"),
        new(MembersUpdate, "members", "Editar datos de alumnos"),
        new(MembersStatus, "members", "Desactivar y reactivar alumnos"),
        new(PlansRead, "plans", "Ver planes"),
        new(PlansManage, "plans", "Crear y editar planes"),
        new(PromotionsRead, "plans", "Ver promociones"),
        new(PromotionsManage, "plans", "Crear y editar promociones"),
        new(PromotionsApply, "plans", "Aplicar promociones al inscribir o renovar"),
        new(MembershipsRead, "memberships", "Ver membresías"),
        new(MembershipsCreate, "memberships", "Inscribir alumnos en un plan"),
        new(MembershipsRenew, "memberships", "Renovar membresías"),
        new(MembershipsFreeze, "memberships", "Congelar y reactivar membresías"),
        new(MembershipsCancel, "memberships", "Cancelar membresías"),
        new(ObligationsRead, "billing", "Ver obligaciones de pago y saldos"),
        new(ObligationsCreate, "billing", "Crear obligaciones de pago"),
        new(ObligationsVoid, "billing", "Anular obligaciones de pago"),
        new(PaymentsRead, "billing", "Ver pagos y recibos"),
        new(PaymentsCreate, "billing", "Registrar pagos y abonos"),
        new(PaymentsConfirm, "billing", "Confirmar transferencias pendientes"),
        new(PaymentsReverse, "billing", "Revertir pagos"),
        new(CashRead, "cash", "Consultar cajas, sesiones y movimientos"),
        new(CashManage, "cash", "Crear y configurar cajas"),
        new(CashOperate, "cash", "Abrir y cerrar caja y registrar movimientos"),
        new(AttendanceRead, "attendance", "Consultar la asistencia"),
        new(AttendanceRegister, "attendance", "Registrar entradas y salidas"),
        new(AttendanceOverride, "attendance", "Autorizar accesos excepcionales"),
        new(CommercialSettingsManage, "commercial", "Configurar políticas comerciales del gimnasio"),
        new(NotificationsRead, "notifications", "Consultar los avisos de WhatsApp enviados"),
        new(ExercisesRead, "exercises", "Ver la biblioteca de ejercicios"),
        new(ExercisesManage, "exercises", "Crear, editar, duplicar y desactivar los ejercicios del gimnasio"),
        new(TrainingPlansRead, "training", "Ver las rutinas de entrenamiento y sus versiones"),
        new(TrainingPlansManage, "training", "Crear, modificar, duplicar y archivar rutinas"),
        new(TrainingMembersManage, "training", "Asignar rutinas a alumnos, cerrar asignaciones y comentar sus sesiones"),
        new(TrainingMemberRead, "training", "Ver la rutina vigente y el historial de entrenamiento de un alumno (el alumno solo ve el suyo)"),
        new(TrainingSessionsLog, "training", "Registrar sesiones de entrenamiento (el alumno solo las suyas)"),
        new(NutritionPlansRead, "nutrition", "Ver los planes nutricionales y sus versiones", Sensitive: true),
        new(NutritionPlansManage, "nutrition", "Crear, modificar, duplicar y archivar planes nutricionales", Sensitive: true),
        new(NutritionMembersManage, "nutrition", "Asignar planes nutricionales a alumnos y cerrar asignaciones", Sensitive: true),
        new(NutritionMemberRead, "nutrition", "Ver el plan, las alergias y el cumplimiento nutricional de un alumno (el alumno solo ve lo suyo)", Sensitive: true),
        new(NutritionProfileManage, "nutrition", "Registrar las alergias, intolerancias y restricciones alimentarias de un alumno", Sensitive: true),
        new(NutritionLogsRecord, "nutrition", "Registrar el cumplimiento de las comidas del plan (el alumno solo las suyas)", Sensitive: true),
        new(AssessmentsRead, "wellness", "Ver las evaluaciones físicas de un alumno (el alumno solo ve las suyas)", Sensitive: true),
        new(AssessmentsManage, "wellness", "Registrar y corregir evaluaciones físicas de los alumnos", Sensitive: true),
        new(GoalsRead, "wellness", "Ver los objetivos de un alumno y su progreso (el alumno solo ve los suyos)", Sensitive: true),
        new(GoalsManage, "wellness", "Definir y cerrar los objetivos de los alumnos", Sensitive: true),
        new(PhotosRead, "wellness", "Ver las fotos de progreso de un alumno y su consentimiento (el alumno solo ve las suyas)", Sensitive: true),
        new(PhotosManage, "wellness", "Registrar o retirar el consentimiento y subir o borrar fotos de progreso (el alumno solo las suyas)", Sensitive: true),
        new(TrainersRead, "trainers", "Ver los perfiles de los entrenadores"),
        new(TrainersManage, "trainers", "Habilitar entrenadores, editar sus perfiles y asignarles alumnos"),
        new(TrainersNotes, "trainers", "Escribir y leer observaciones profesionales de los alumnos asignados", Sensitive: true),
        new(WellnessMembersAll, "trainers", "Ver el seguimiento de todos los alumnos de sus sucursales, no solo de los asignados"),
        new(MembersAccess, "members", "Dar, restablecer y quitar el acceso de un alumno a su portal"),
        new(GroupClassesRead, "classes", "Ver las clases grupales y su configuración"),
        new(GroupClassesManage, "classes", "Crear, editar, activar y desactivar clases grupales"),
        new(ClassSessionsRead, "classes", "Ver el horario de las clases grupales (sesiones y series)"),
        new(ClassSessionsManage, "classes", "Programar, reprogramar y cancelar sesiones y series de clases grupales"),
        new(MeRead, "me", "Consultar los propios datos de alumno (portal del alumno)")
    ];

    public static bool IsSensitive(string code) => All.Any(p => p.Sensitive && p.Code == code);

    public static bool IsPlatform(string code) => code.StartsWith("platform.", StringComparison.Ordinal);
}
