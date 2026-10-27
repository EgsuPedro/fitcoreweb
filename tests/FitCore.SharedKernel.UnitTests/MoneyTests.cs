using FitCore.SharedKernel;

namespace FitCore.SharedKernel.UnitTests;

public class MoneyTests
{
    [Theory]
    [InlineData(10.005, 10.01)]
    [InlineData(10.004, 10.00)]
    [InlineData(-10.005, -10.01)]
    public void Constructor_redondea_a_dos_decimales_alejandose_de_cero(double input, double expected)
    {
        var money = new Money((decimal)input);

        Assert.Equal((decimal)expected, money.Amount);
    }

    [Fact]
    public void Moneda_por_defecto_es_USD()
    {
        Assert.Equal("USD", Money.Usd(5m).Currency);
    }

    [Fact]
    public void Suma_y_resta_en_la_misma_moneda()
    {
        var total = Money.Usd(10.10m) + Money.Usd(0.20m) - Money.Usd(0.30m);

        Assert.Equal(10.00m, total.Amount);
    }

    [Fact]
    public void Operar_monedas_distintas_lanza_excepcion()
    {
        Assert.Throws<InvalidOperationException>(() => Money.Usd(1m) + new Money(1m, "EUR"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("US")]
    [InlineData("DOLLAR")]
    public void Moneda_invalida_lanza_excepcion(string currency)
    {
        Assert.Throws<ArgumentException>(() => new Money(1m, currency));
    }

    [Fact]
    public void Multiplicar_por_tarifa_redondea_el_resultado()
    {
        // 33.33 * 15% = 4.9995 -> 5.00
        Assert.Equal(5.00m, (Money.Usd(33.33m) * 0.15m).Amount);
    }
}
