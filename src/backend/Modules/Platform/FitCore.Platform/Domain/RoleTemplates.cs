namespace FitCore.Platform.Domain;

public sealed record RoleTemplate(string Name, string Description, bool GrantsAllBranches, IReadOnlyList<string> Permissions);

/// <summary>Plantillas de rol. Cada tenant recibe una copia editable al crearse.</summary>
public static class RoleTemplates
{
    public const string Owner = "Propietario";
    public const string BranchAdmin = "AdministradorSucursal";
    public const string PlatformAdmin = "AdministradorPlataforma";

    private static readonly string[] BasicRead = [Permissions.OrganizationRead, Permissions.BranchesRead];

    public static readonly IReadOnlyList<RoleTemplate> ForTenant =
    [
        new(
            Owner,
            "Control total del gimnasio y de todas sus sucursales",
            true,
            [.. Permissions.All.Where(p => !Permissions.IsPlatform(p.Code)).Select(p => p.Code)]),
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
                Permissions.RolesRead
            ]),
        new("Recepcion", "Atención al público en sucursal", false, BasicRead),
        new("Cajero", "Cobros y caja en sucursal", false, BasicRead),
        new("Entrenador", "Entrenamiento de alumnos asignados", false, BasicRead),
        new("Nutricionista", "Planes nutricionales de alumnos asignados", false, BasicRead),
        new("Contador", "Consulta financiera de todo el gimnasio", true, [.. BasicRead, Permissions.AuditRead]),
        new("Alumno", "Acceso del alumno a su propia información", false, [])
    ];

    public static readonly RoleTemplate ForPlatform = new(
        PlatformAdmin,
        "Equipo de FitCore que administra los gimnasios",
        true,
        [Permissions.PlatformTenantsManage, Permissions.OrganizationRead]);
}
