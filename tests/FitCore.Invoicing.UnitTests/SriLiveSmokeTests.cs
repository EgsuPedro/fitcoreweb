using FitCore.Invoicing.Domain;
using FitCore.Invoicing.Sri;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit.Abstractions;

namespace FitCore.Invoicing.UnitTests;

/// <summary>Se ejecuta solo si FITCORE_SRI_LIVE=1.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class LiveSriFactAttribute : FactAttribute
{
    public LiveSriFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("FITCORE_SRI_LIVE") != "1")
        {
            Skip = "Prueba contra el ambiente de pruebas real del SRI: defina FITCORE_SRI_LIVE=1 para ejecutarla.";
        }
    }
}

/// <summary>
/// Humo contra el ambiente de PRUEBAS real del SRI (celcer.sri.gob.ec). Solo hacen llamadas inocuas, sin datos de nadie: consultar la autorización de una clave que no existe y enviar un
/// documento vacío (<c>&lt;a/&gt;</c>) que el SRI debe devolver. No emiten nada y no necesitan certificado. Comprueban que el cliente y el analizador entienden las respuestas reales.
/// </summary>
public class SriLiveSmokeTests(ITestOutputHelper output)
{
    private static SriSoapGateway Gateway() =>
        new(new HttpClient { Timeout = TimeSpan.FromSeconds(60) }, Options.Create(new SriOptions()), NullLogger<SriSoapGateway>.Instance);

    [LiveSriFact]
    public async Task Querying_an_unknown_access_key_is_understood_by_the_parser()
    {
        var key = AccessKey.Build(DateOnly.FromDateTime(DateTime.UtcNow), "01", "1790000028001", SriEnvironment.Test, "001", "001", 999_999_999, "00000000");

        var result = await Gateway().QueryAuthorizationAsync(SriEnvironment.Test, key, CancellationToken.None);

        output.WriteLine($"consultada={result.QueriedKey} autorizaciones={result.Authorizations.Count}");
        foreach (var a in result.Authorizations)
        {
            output.WriteLine($"estado={a.RawState} -> {a.State}; mensajes={string.Join(" | ", a.Messages.Select(m => $"{m.Id}:{m.Text}"))}");
        }

        Assert.Equal(key, result.QueriedKey);
    }

    [LiveSriFact]
    public async Task Sending_an_empty_document_comes_back_as_returned_with_messages()
    {
        var result = await Gateway().SendAsync(SriEnvironment.Test, "<a/>", CancellationToken.None);

        output.WriteLine($"estado={result.RawState} -> {result.State}");
        foreach (var m in result.Messages)
        {
            output.WriteLine($"{m.Id} [{m.Type}] {m.Text} :: {m.Detail}");
        }

        Assert.Equal(SriReceptionState.Returned, result.State);
        Assert.NotEmpty(result.Messages);
    }
}
