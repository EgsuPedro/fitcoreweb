using System.Collections.Concurrent;
using FitCore.Invoicing.Domain;

namespace FitCore.Api.IntegrationTests.Infrastructure;

/// <summary>
/// SRI simulado para las pruebas de integración: cada respuesta se programa antes de ejecutar el Worker y cada llamada queda registrada. Jamás sale a la red.
/// Si no hay respuesta programada, falla con un «SRI no disponible» (así una prueba nunca depende de un valor por omisión).
/// </summary>
public sealed class FakeSriGateway : ISriGateway
{
    private readonly ConcurrentQueue<Func<SriReception>> _receptions = new();
    private readonly ConcurrentQueue<Func<SriAuthorizationResult>> _authorizations = new();

    public ConcurrentQueue<(SriEnvironment Environment, string Xml)> Sends { get; } = new();

    public ConcurrentQueue<(SriEnvironment Environment, string Key)> Queries { get; } = new();

    /// <summary>Pausa artificial dentro de cada llamada: ensancha la ventana de carrera de las pruebas de concurrencia.</summary>
    public TimeSpan Delay { get; set; } = TimeSpan.Zero;

    public FakeSriGateway ThenReceive(SriReception reception)
    {
        _receptions.Enqueue(() => reception);
        return this;
    }

    public FakeSriGateway ThenFailReception(string code)
    {
        _receptions.Enqueue(() => throw new SriUnavailableException(code, "simulado"));
        return this;
    }

    public FakeSriGateway ThenAuthorize(SriAuthorizationResult result)
    {
        _authorizations.Enqueue(() => result);
        return this;
    }

    public FakeSriGateway ThenFailAuthorization(string code)
    {
        _authorizations.Enqueue(() => throw new SriUnavailableException(code, "simulado"));
        return this;
    }

    public async Task<SriReception> SendAsync(SriEnvironment environment, string signedXml, CancellationToken cancellationToken)
    {
        Sends.Enqueue((environment, signedXml));
        if (Delay > TimeSpan.Zero)
        {
            await Task.Delay(Delay, cancellationToken);
        }

        return _receptions.TryDequeue(out var next) ? next() : throw new SriUnavailableException("sri_unreachable", "sin respuesta programada");
    }

    public async Task<SriAuthorizationResult> QueryAuthorizationAsync(SriEnvironment environment, string accessKey, CancellationToken cancellationToken)
    {
        Queries.Enqueue((environment, accessKey));
        if (Delay > TimeSpan.Zero)
        {
            await Task.Delay(Delay, cancellationToken);
        }

        return _authorizations.TryDequeue(out var next) ? next() : throw new SriUnavailableException("sri_unreachable", "sin respuesta programada");
    }

    // ---- respuestas habituales --------------------------------------------------------------------------------------------------

    public static SriReception Received() => new(SriReceptionState.Received, "RECIBIDA", []);

    public static SriReception Returned(string id = "35", string text = "ARCHIVO NO CUMPLE ESTRUCTURA XML", string detail = "Se encontró un error") =>
        new(SriReceptionState.Returned, "DEVUELTA", [new SriMessage(id, text, detail, "ERROR")]);

    public static SriAuthorizationResult Nothing(string key) => new(key, []);

    public static SriAuthorizationResult Pending(string key, string state = "EN PROCESAMIENTO") =>
        new(key, [new SriAuthorization(SriAuthorizationStates.Parse(state), state, null, null, "PRUEBAS", null, [])]);

    public static SriAuthorizationResult Authorized(string key, string signedXml, DateTimeOffset? at = null) =>
        new(key, [new SriAuthorization(
            SriAuthorizationState.Authorized, "AUTORIZADO", key, at ?? DateTimeOffset.UtcNow, "PRUEBAS",
            $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><autorizacion><estado>AUTORIZADO</estado><numeroAutorizacion>{key}</numeroAutorizacion><comprobante><![CDATA[{signedXml}]]></comprobante></autorizacion>",
            [new SriMessage("60", "ESTE PROCESO FUE REALIZADO EN EL AMBIENTE DE PRUEBAS", null, "ADVERTENCIA")])]);

    public static SriAuthorizationResult NotAuthorized(string key) =>
        new(key, [new SriAuthorization(SriAuthorizationState.NotAuthorized, "NO AUTORIZADO", null, DateTimeOffset.UtcNow, "PRUEBAS", "<autorizacion/>", [new SriMessage("39", "FIRMA INVALIDA", "x", "ERROR")])]);
}
