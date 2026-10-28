using CommercialAccessPolicy = FitCore.Commercial.Domain.AccessPolicy;
using CommercialRules = FitCore.Commercial.Domain.AccessRules;
using CommercialStanding = FitCore.Commercial.Domain.ObligationStanding;
using FitCore.Classes.Domain;

namespace FitCore.Architecture.Tests;

/// <summary>
/// Los módulos no se referencian, así que las clases reexpresan la política de acceso con deuda del gimnasio. Esta prueba (que sí ve a ambos)
/// asegura que decidan siempre igual: si alguien cambia la política en un lado y no en el otro, el build falla.
/// </summary>
public class PolicyParityTests
{
    private static readonly DateOnly Today = new(2026, 10, 28);

    [Fact]
    public void Las_politicas_tienen_los_mismos_nombres_y_valores()
    {
        var commercial = Enum.GetValues<CommercialAccessPolicy>().ToDictionary(p => p.ToString(), p => (int)p);
        var classes = Enum.GetValues<DebtPolicy>().ToDictionary(p => p.ToString(), p => (int)p);

        Assert.Equal(commercial, classes);
    }

    [Fact]
    public void La_decision_por_deuda_es_la_misma_para_la_entrada_al_gimnasio_y_para_reservar_una_clase()
    {
        decimal[] payables = [0m, 30m, 100m];
        decimal[] paids = [0m, 10m, 30m, 50m, 100m];
        int[] dueOffsets = [-10, -1, 0, 1, 10];
        int[] percents = [1, 30, 33, 34, 50, 100];

        var checkedCases = 0;
        foreach (var policy in Enum.GetValues<CommercialAccessPolicy>())
        {
            foreach (var percent in percents)
            {
                foreach (var payable in payables)
                {
                    foreach (var paid in paids)
                    {
                        foreach (var due in dueOffsets)
                        {
                            // Una y dos obligaciones, para cubrir también la suma de varias.
                            foreach (var count in new[] { 1, 2 })
                            {
                                var commercial = Enumerable.Repeat(new CommercialStanding(payable, paid, Today.AddDays(due)), count).ToList();
                                var classes = Enumerable.Repeat(new ObligationStanding(payable, paid, Today.AddDays(due)), count).ToList();

                                var expected = CommercialRules.DebtDenial(policy, percent, Today, commercial);
                                var actual = EligibilityRules.DebtDenial((DebtPolicy)(int)policy, percent, Today, classes);

                                Assert.Equal(expected?.Split('.')[^1], actual?.Code.Split('.')[^1]);
                                checkedCases++;
                            }
                        }
                    }
                }
            }
        }

        Assert.True(checkedCases > 1000);
    }
}
