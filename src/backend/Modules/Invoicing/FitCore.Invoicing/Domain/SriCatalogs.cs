namespace FitCore.Invoicing.Domain;

/// <summary>
/// Códigos del SRI que usa el comprobante. Cada valor indica de dónde sale. <b>Fuente principal:</b> Ficha técnica de comprobantes electrónicos, esquema offline, versión 2.20
/// (diciembre 2020), leída del PDF oficial. Lo que cambió después (IVA 15 %) no consta en esa versión: ver la nota en <see cref="SriCatalogs.IvaRateCode"/>.
/// Antes de la prueba real (bloque 7) se contrastan con la versión vigente (2.34) y con los XSD.
/// </summary>
public static class SriCatalogs
{
    /// <summary>Tabla 16: código del impuesto IVA.</summary>
    public const string IvaTaxCode = "2";

    /// <summary>Tabla 3: código del tipo de comprobante «Factura» (etiqueta <c>codDoc</c>).</summary>
    public const string InvoiceDocumentCode = "01";

    /// <summary>Tabla 2: tipo de emisión «Normal».</summary>
    public const string NormalEmissionCode = "1";

    /// <summary>Tabla 4: tipo de ambiente (1 pruebas, 2 producción).</summary>
    public static string EnvironmentCode(SriEnvironment environment) => environment == SriEnvironment.Production ? "2" : "1";

    /// <summary>
    /// Tabla 17 (tarifa del IVA): 0 % → 0, 12 % → 2, 14 % → 3, no objeto de impuesto → 6, exento → 7 (versión 2.20, leída). El <b>15 % → 4</b> no está en esa versión: lo
    /// asignan la versión 2.26 en adelante según el índice oficial del SRI y reseñas de la actualización; <b>no se pudo leer en el PDF vigente desde este entorno</b>, por lo que se valida
    /// con el SRI de pruebas (bloque 7). Devuelve <c>null</c> si la combinación no se conoce: nunca se adivina un código.
    /// </summary>
    public static string? IvaRateCode(string taxKind, decimal percent) => taxKind switch
    {
        "Zero" => percent == 0m ? "0" : null,
        "NotSubject" => percent == 0m ? "6" : null,
        "Exempt" => percent == 0m ? "7" : null,
        "General" => percent switch
        {
            12m => "2",
            14m => "3",
            15m => "4",
            _ => null
        },
        _ => null
    };

    /// <summary>
    /// Tabla 24 (formas de pago, versión 2.20): 01 sin utilización del sistema financiero, 15 compensación de deudas, 16 tarjeta de débito, 17 dinero electrónico, 18 tarjeta prepago,
    /// 19 tarjeta de crédito, 20 otros con utilización del sistema financiero, 21 endoso de títulos. El texto extraído del PDF desordena las columnas: la correspondencia código ↔
    /// nombre sigue el orden de la tabla y debe verificarse con la versión vigente.
    /// </summary>
    public static string PaymentMethodCode(string method, CardPaymentMethod card) => method switch
    {
        "Cash" => "01",
        "Card" => card == CardPaymentMethod.Debit ? "16" : "19",
        _ => "20" // BankTransfer y Other: se pagó por el sistema financiero.
    };
}

/// <summary>El módulo de pagos registra «tarjeta» sin distinguir crédito de débito; el SRI sí los distingue. Cada gimnasio elige cuál es su caso habitual.</summary>
public enum CardPaymentMethod
{
    Credit = 1,
    Debit = 2
}
