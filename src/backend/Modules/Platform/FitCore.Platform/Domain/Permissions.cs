namespace FitCore.Platform.Domain;

public sealed record PermissionDefinition(string Code, string Module, string Description);

/// <summary>Catálogo fijo de permisos. Cada módulo agrega los suyos al implementarse.</summary>
public static class Permissions
{
    public const string PlatformTenantsManage = "platform.tenants.manage";

    public const string OrganizationRead = "organization.read";
    public const string OrganizationUpdate = "organization.update";

    public const string BranchesRead = "branches.read";
    public const string BranchesCreate = "branches.create";
    public const string BranchesUpdate = "branches.update";

    public const string UsersRead = "users.read";
    public const string UsersCreate = "users.create";
    public const string UsersUpdate = "users.update";

    public const string RolesRead = "roles.read";

    public const string AuditRead = "audit.read";

    public static readonly IReadOnlyList<PermissionDefinition> All =
    [
        new(PlatformTenantsManage, "platform", "Administrar gimnasios (solo equipo de plataforma)"),
        new(OrganizationRead, "organization", "Ver datos del gimnasio"),
        new(OrganizationUpdate, "organization", "Editar datos del gimnasio"),
        new(BranchesRead, "branches", "Ver sucursales"),
        new(BranchesCreate, "branches", "Crear sucursales"),
        new(BranchesUpdate, "branches", "Editar sucursales"),
        new(UsersRead, "users", "Ver usuarios"),
        new(UsersCreate, "users", "Crear usuarios"),
        new(UsersUpdate, "users", "Editar usuarios, roles y sucursales"),
        new(RolesRead, "roles", "Ver roles y permisos"),
        new(AuditRead, "audit", "Consultar la auditoría")
    ];

    public static bool IsPlatform(string code) => code.StartsWith("platform.", StringComparison.Ordinal);
}
