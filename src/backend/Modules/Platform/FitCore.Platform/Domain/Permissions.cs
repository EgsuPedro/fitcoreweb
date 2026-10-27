namespace FitCore.Platform.Domain;

public sealed record PermissionDefinition(string Code, string Module, string Description);

/// <summary>Catálogo fijo de permisos. Cada módulo agrega los suyos al implementarse.</summary>
public static class Permissions
{
    public const string PlatformTenantsManage = "platform.tenants.manage";
    public const string PlatformTaxesManage = "platform.taxes.manage";

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

    public static readonly IReadOnlyList<PermissionDefinition> All =
    [
        new(PlatformTenantsManage, "platform", "Administrar gimnasios (solo equipo de plataforma)"),
        new(PlatformTaxesManage, "platform", "Administrar las tarifas de IVA (solo equipo de plataforma)"),
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
        new(CommercialSettingsManage, "commercial", "Configurar políticas comerciales del gimnasio")
    ];

    public static bool IsPlatform(string code) => code.StartsWith("platform.", StringComparison.Ordinal);
}
