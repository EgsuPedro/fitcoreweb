namespace FitCore.Invoicing.Domain;

/// <summary>
/// Códigos del SRI que usa el comprobante. <b>Fuente: Ficha técnica de comprobantes electrónicos, esquema offline, versión 2.34 (julio 2026)</b>, verificada en el PDF oficial
/// (docs/referencias-sri): Tabla 2, 3, 4, 6, 16, 17 y 24. Lo definitivo es la autorización en el ambiente de pruebas del SRI (bloque 7).
/// </summary>
public static class SriCatalogs
{
    /// <summary>Tabla 16: código del impuesto IVA.</summary>
    public const string IvaTaxCode = "2";

    /// <summary>Tabla 3: código del tipo de comprobante «Factura» (etiqueta <c>codDoc</c>).</summary>
    public const string InvoiceDocumentCode = "01";

    /// <summary>Tabla 3: código del tipo de comprobante «Nota de crédito».</summary>
    public const string CreditNoteDocumentCode = "04";

    public static string DocumentCode(InvoiceDocumentType type) => type == InvoiceDocumentType.CreditNote ? CreditNoteDocumentCode : InvoiceDocumentCode;

    /// <summary>Tabla 2: tipo de emisión «Normal».</summary>
    public const string NormalEmissionCode = "1";

    /// <summary>Tabla 4: tipo de ambiente (1 pruebas, 2 producción).</summary>
    public static string EnvironmentCode(SriEnvironment environment) => environment == SriEnvironment.Production ? "2" : "1";

    /// <summary>
    /// Tabla 17 (tarifa del IVA, ficha 2.34): 0 % → 0, 12 % → 2, 14 % → 3, <b>15 % → 4</b>, 5 % → 5, no objeto de impuesto → 6, exento → 7, IVA diferenciado → 8, 13 % → 10.
    /// Devuelve <c>null</c> si la combinación no se conoce: nunca se adivina un código.
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
            5m => "5",
            13m => "10",
            _ => null
        },
        _ => null
    };

    /// <summary>
    /// Tabla 24 (formas de pago, ficha 2.34): 01 sin utilización del sistema financiero, 15 compensación de deudas, 16 tarjeta de débito, 17 dinero electrónico, 18 tarjeta prepago,
    /// 19 tarjeta de crédito, 20 otros con utilización del sistema financiero, 21 endoso de títulos.
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
