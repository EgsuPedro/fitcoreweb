using System.Data.Common;
using FitCore.Application.Core;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace FitCore.Platform.Persistence;

/// <summary>
/// Al abrir cada conexión fija <c>app.tenant_id</c>, que usan las políticas de Row-Level Security.
/// Sin tenant activo se fija vacío y PostgreSQL no devuelve ninguna fila.
/// </summary>
public sealed class TenantConnectionInterceptor(ITenantContext tenantContext) : DbConnectionInterceptor
{
    private const string SetTenantSql = "SELECT set_config('app.tenant_id', @tenant, false)";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var command = CreateCommand(connection, tenantContext.TenantId);
        command.ExecuteScalar();
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default) =>
        await ApplyTenantAsync(connection, tenantContext.TenantId, cancellationToken);

    internal static async Task ApplyTenantAsync(DbConnection connection, Guid? tenantId, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, tenantId);
        await command.ExecuteScalarAsync(cancellationToken);
    }

    private static DbCommand CreateCommand(DbConnection connection, Guid? tenantId)
    {
        var command = connection.CreateCommand();
        command.CommandText = SetTenantSql;

        var parameter = command.CreateParameter();
        parameter.ParameterName = "tenant";
        parameter.Value = tenantId?.ToString() ?? string.Empty;
        command.Parameters.Add(parameter);

        return command;
    }
}
