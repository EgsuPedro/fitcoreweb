namespace FitCore.Platform.Domain;

public sealed record RoleTemplate(string Name, string Description, bool GrantsAllBranches, IReadOnlyList<string> Permissions);

/// <summary>Plantillas de rol. Cada tenant recibe una copia editable al crearse.</summary>
public static class RoleTemplates
{
    public const string Owner = "Propietario";
    public const string BranchAdmin = "AdministradorSucursal";
    public const string PlatformAdmin = "AdministradorPlataforma";

    private static readonly string[] BasicRead = [Permissions.OrganizationRead, Permissions.BranchesRead];

    /// <summary>
    /// Permisos que el rol Propietario nunca recibe por defecto: los sensibles (nutrición, evaluaciones, fotos, observaciones…).
    /// El propietario puede conceder los sensibles a un rol propio; esa decisión queda auditada.
    /// </summary>
    public static IReadOnlyList<string> OwnerExcluded { get; } = [.. Permissions.All.Where(p => p.Sensitive).Select(p => p.Code)];

    public static readonly IReadOnlyList<RoleTemplate> ForTenant =
    [
        new(
            Owner,
            "Control total del gimnasio y de todas sus sucursales",
            true,
            [.. Permissions.All.Where(p => !Permissions.IsPlatform(p.Code) && !OwnerExcluded.Contains(p.Code)).Select(p => p.Code)]),
        new(
            BranchAdmin,
            "Administra una o más sucursales asignadas",
            false,
            [
                Permissions.OrganizationRead,
                Permissions.BranchesRead,
                Permissions.UsersRead,
                Permissions.UsersCreate,
                Permissions.UsersUpdate,
                Permissions.RolesRead,
                Permissions.MembersRead,
                Permissions.MembersCreate,
                Permissions.MembersUpdate,
                Permissions.MembersStatus,
                Permissions.PlansRead,
                Permissions.PromotionsRead,
                Permissions.PromotionsApply,
                Permissions.MembershipsRead,
                Permissions.MembershipsCreate,
                Permissions.MembershipsRenew,
                Permissions.MembershipsFreeze,
                Permissions.MembershipsCancel,
                Permissions.ObligationsRead,
                Permissions.ObligationsCreate,
                Permissions.ObligationsVoid,
                Permissions.PaymentsRead,
                Permissions.PaymentsCreate,
                Permissions.PaymentsConfirm,
                Permissions.PaymentsReverse,
                Permissions.CashRead,
                Permissions.CashOperate,
                Permissions.AttendanceRead,
                Permissions.AttendanceRegister,
                Permissions.AttendanceOverride,
                Permissions.NotificationsRead,
                Permissions.MembersAccess,
                Permissions.TrainersRead,
                Permissions.TrainersManage,
                Permissions.WellnessMembersAll,
                Permissions.ExercisesRead,
                Permissions.ExercisesManage,
                Permissions.TrainingPlansRead,
                Permissions.TrainingPlansManage,
                Permissions.TrainingMembersManage,
                Permissions.TrainingMemberRead
            ]),
        new(
            "Recepcion",
            "Atención al público en sucursal",
            false,
            [
                .. BasicRead,
                Permissions.MembersRead,
                Permissions.MembersCreate,
                Permissions.MembersUpdate,
                Permissions.PlansRead,
                Permissions.PromotionsRead,
                Permissions.MembershipsRead,
                Permissions.MembershipsCreate,
                Permissions.MembershipsRenew,
                Permissions.ObligationsRead,
                Permissions.PaymentsRead,
                Permissions.PaymentsCreate,
                Permissions.AttendanceRead,
                Permissions.AttendanceRegister
            ]),
        new(
            "Cajero",
            "Cobros y caja en sucursal",
            false,
            [
                .. BasicRead,
                Permissions.MembersRead,
                Permissions.MembershipsRead,
                Permissions.ObligationsRead,
                Permissions.PaymentsRead,
                Permissions.PaymentsCreate,
                Permissions.PaymentsConfirm,
                Permissions.CashRead,
                Permissions.CashOperate
            ]),
        // Mínimo privilegio: el entrenador trabaja solo con sus alumnos asignados (lo resuelve el módulo de bienestar), no con todo el listado
        // de alumnos ni la asistencia del gimnasio.
        new("Entrenador", "Entrenamiento de alumnos asignados", false, [.. BasicRead, Permissions.TrainersRead, Permissions.TrainersNotes, Permissions.ExercisesRead, Permissions.ExercisesManage,
            Permissions.TrainingPlansRead, Permissions.TrainingPlansManage, Permissions.TrainingMembersManage, Permissions.TrainingMemberRead, Permissions.TrainingSessionsLog]),
        new("Nutricionista", "Planes nutricionales de alumnos asignados", false, [.. BasicRead, Permissions.MembersRead]),
        new(
            "Contador",
            "Consulta financiera de todo el gimnasio",
            true,
            [
                .. BasicRead,
                Permissions.AuditRead,
                Permissions.MembersRead,
                Permissions.MembershipsRead,
                Permissions.ObligationsRead,
                Permissions.PaymentsRead,
                Permissions.CashRead
            ]),
        new("Alumno", "Acceso del alumno a su propia información", false, [Permissions.MeRead, Permissions.TrainingMemberRead, Permissions.TrainingSessionsLog])
    ];

    public static readonly RoleTemplate ForPlatform = new(
        PlatformAdmin,
        "Equipo de FitCore que administra los gimnasios",
        true,
        [Permissions.PlatformTenantsManage, Permissions.PlatformTaxesManage, Permissions.PlatformExercisesManage, Permissions.OrganizationRead]);
}
