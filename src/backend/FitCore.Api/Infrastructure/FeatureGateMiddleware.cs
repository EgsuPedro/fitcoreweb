using System.Text.RegularExpressions;
using FitCore.Application.Core;
using FitCore.SharedKernel;

namespace FitCore.Api.Infrastructure;

/// <summary>
/// Aplica en UN solo lugar qué funcionalidad del plan exige cada grupo de rutas (ADR 0012). Ni los controladores ni los módulos comparan
/// nombres de plan: consultan el servicio de capacidades con un código estable. Corre después de autenticar y autorizar, así que un usuario sin
/// permiso recibe 403 de permisos antes que cualquier aviso comercial; y nunca bloquea a quien no es de un gimnasio (login, plataforma).
/// <para>
/// <c>FreezeWhenRestricted</c>: si la suscripción del gimnasio está restringida (suspendida, vencida o cancelada), las escrituras de esa
/// función se rechazan pero la consulta sigue disponible. Las funciones de operación diaria de los alumnos (cobros, caja, asistencia) y la
/// exportación de los propios datos no se congelan.
/// </para>
/// </summary>
internal sealed class FeatureGateMiddleware(RequestDelegate next)
{
    private const string ApiPrefix = "/api/v1";

    private sealed record Gate(Regex Path, string Feature, bool FreezeWhenRestricted, bool OnlyWrites = false);

    private static Gate G(string pattern, string feature, bool freeze, bool onlyWrites = false) =>
        new(new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)), feature, freeze, onlyWrites);

    private static readonly Gate[] Gates =
    [
        G(@"^/(group-classes|class-sessions|class-reservations|class-attendance)(/|$)", Capabilities.Features.Classes, true),
        G(@"^/me/class-(sessions|reservations|attendance)(/|$)", Capabilities.Features.Classes, true),
        G(@"^/(trainers|exercises|training-plans|training-assignments|workout-sessions)(/|$)", Capabilities.Features.Training, true),
        G(@"^/members/[^/]+/(training-plan|training-assignments|workout-sessions|exercise-progress|training-adherence|trainer-assignments|trainer-notes)(/|$)", Capabilities.Features.Training, true),
        G(@"^/(nutrition-plans|nutrition-assignments|meal-logs|goals)(/|$)", Capabilities.Features.Nutrition, true),
        G(@"^/members/[^/]+/(nutrition-plan|nutrition-assignments|dietary-profile|meal-logs|assessments|goals|nutrition-adherence)(/|$)", Capabilities.Features.Nutrition, true),
        G(@"^/members/[^/]+/(photo-consent|progress-photos)(/|$)", Capabilities.Features.ExtraStorage, true),
        G(@"^/progress-photos(/|$)", Capabilities.Features.ExtraStorage, true),
        G(@"^/reports/exports(/|$)", Capabilities.Features.Exports, false),
        G(@"^/(analytics|reports/(financial|members|attendance|classes|branches))(/|$)", Capabilities.Features.AdvancedReports, false),
        G(@"^/attendance(/|$)", Capabilities.Features.Attendance, false),
        G(@"^/(payments|cash-registers|payment-obligations|receivables)(/|$)", Capabilities.Features.CashPayments, false),
        G(@"^/roles(/|$)", Capabilities.Features.AdvancedPermissions, true, onlyWrites: true)
    ];

    public async Task InvokeAsync(HttpContext context, ICapabilityEvaluator capabilities)
    {
        if (context.User.Identity?.IsAuthenticated == true && context.Request.Path.StartsWithSegments(ApiPrefix, out var rest))
        {
            var isWrite = !HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method) && !HttpMethods.IsOptions(context.Request.Method);
            var path = rest.Value ?? string.Empty;
            var gate = Gates.FirstOrDefault(g => g.Path.IsMatch(path));
            if (gate is not null && (isWrite || !gate.OnlyWrites))
            {
                var allowed = await capabilities.RequireFeatureAsync(gate.Feature, isWrite && gate.FreezeWhenRestricted, context.RequestAborted);
                if (allowed.IsFailure)
                {
                    await allowed.Error.ToProblem().ExecuteAsync(context);
                    return;
                }
            }
        }

        await next(context);
    }
}
