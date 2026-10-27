namespace FitCore.SharedKernel;

/// <summary>
/// Valor monetario. Siempre 2 decimales (redondeo "away from zero") y moneda ISO 4217.
/// Nunca se mezclan monedas en una operación.
/// </summary>
public readonly record struct Money
{
    public const string DefaultCurrency = "USD";

    public Money(decimal amount, string currency = DefaultCurrency)
    {
        if (string.IsNullOrWhiteSpace(currency) || currency.Length != 3)
        {
            throw new ArgumentException("La moneda debe ser un código ISO 4217 de 3 letras.", nameof(currency));
        }

        Amount = Math.Round(amount, 2, MidpointRounding.AwayFromZero);
        Currency = currency.ToUpperInvariant();
    }

    public decimal Amount { get; }
    public string Currency { get; }

    public static Money Zero(string currency = DefaultCurrency) => new(0m, currency);
    public static Money Usd(decimal amount) => new(amount, DefaultCurrency);

    public static Money operator +(Money a, Money b)
    {
        EnsureSameCurrency(a, b);
        return new Money(a.Amount + b.Amount, a.Currency);
    }

    public static Money operator -(Money a, Money b)
    {
        EnsureSameCurrency(a, b);
        return new Money(a.Amount - b.Amount, a.Currency);
    }

    public static Money operator *(Money a, decimal factor) => new(a.Amount * factor, a.Currency);

    public bool IsNegative => Amount < 0;
    public bool IsZero => Amount == 0;

    public override string ToString() => $"{Amount:0.00} {Currency}";

    private static void EnsureSameCurrency(Money a, Money b)
    {
        if (a.Currency != b.Currency)
        {
            throw new InvalidOperationException($"No se pueden operar monedas distintas ({a.Currency} y {b.Currency}).");
        }
    }
}
