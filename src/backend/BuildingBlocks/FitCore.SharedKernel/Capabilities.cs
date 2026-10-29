namespace FitCore.SharedKernel;

/// <summary>
/// Catálogo de capacidades comerciales de FitCore 360 (ADR 0012). Son códigos estables: los planes los combinan y los módulos de negocio
/// los consultan por código, nunca por el nombre del plan. Agregar una capacidad nueva es agregar una fila aquí; las reglas de autorización
/// y los planes no se reescriben.
/// </summary>
public static class Capabilities
{
    /// <summary>Funcionalidades que un plan puede habilitar o no (sí/no).</summary>
    public static class Features
    {
        public const string Members = "members";
        public const string Branches = "branches";
        public const string CashPayments = "cash_payments";
        public const string Attendance = "attendance";
        public const string Training = "training";
        public const string Nutrition = "nutrition";
        public const string Classes = "classes";
        public const string AdvancedReports = "advanced_reports";
        public const string Exports = "exports";
        public const string AdvancedPermissions = "advanced_permissions";
        public const string ExtraStorage = "extra_storage";
    }

    /// <summary>Límites cuantitativos. Un valor nulo en un plan significa «sin límite».</summary>
    public static class Limits
    {
        public const string Branches = "branches";
        public const string InternalUsers = "internal_users";
        public const string Members = "members";
        public const string StorageMegabytes = "storage_mb";
    }

    public sealed record Descriptor(string Code, string Name, string Description);

    public sealed record LimitDescriptor(string Code, string Name, string Description, string Unit);

    public static IReadOnlyList<Descriptor> FeatureCatalog { get; } =
    [
        new(Features.Members, "Gestión de alumnos", "Registrar y administrar alumnos y sus membresías"),
        new(Features.Branches, "Gestión de sucursales", "Crear y administrar más de una sucursal"),
        new(Features.CashPayments, "Caja y pagos", "Cobros a alumnos, caja y cartera"),
        new(Features.Attendance, "Asistencia", "Control de acceso y asistencia al gimnasio"),
        new(Features.Training, "Entrenamiento", "Entrenadores, ejercicios y rutinas"),
        new(Features.Nutrition, "Nutrición", "Planes nutricionales, evaluaciones y seguimiento"),
        new(Features.Classes, "Clases grupales", "Clases, horarios, reservas y lista de espera"),
        new(Features.AdvancedReports, "Reportes avanzados", "Dashboards, reportes financieros, de alumnos y comparación de sucursales"),
        new(Features.Exports, "Exportaciones", "Exportar reportes a CSV"),
        new(Features.AdvancedPermissions, "Gestión avanzada de permisos", "Crear roles propios y asignar permisos finos"),
        new(Features.ExtraStorage, "Almacenamiento adicional", "Fotos de progreso y archivos por encima del almacenamiento base")
    ];

    public static IReadOnlyList<LimitDescriptor> LimitCatalog { get; } =
    [
        new(Limits.Branches, "Sucursales", "Máximo de sucursales activas", "sucursales"),
        new(Limits.InternalUsers, "Usuarios internos", "Máximo de usuarios del personal (no cuenta a los alumnos con acceso al portal)", "usuarios"),
        new(Limits.Members, "Alumnos registrados", "Máximo de alumnos registrados", "alumnos"),
        new(Limits.StorageMegabytes, "Almacenamiento", "Espacio para archivos y fotos", "MB")
    ];

    public static bool IsFeature(string code) => FeatureCatalog.Any(f => f.Code == code);

    public static bool IsLimit(string code) => LimitCatalog.Any(l => l.Code == code);
}
