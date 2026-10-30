using FitCore.Application.Core;
using FitCore.Invoicing.Domain;

namespace FitCore.Invoicing.UnitTests;

public class BuyerRulesTests
{
    private const decimal Limit = 50m;
    private const string ValidCedula = "1712345675"; // misma del RUC de prueba 1712345675001
    private const string ValidRuc = "1790000028001";

    private static InvoiceSourceBuyer Source(
        SourceIdKind kind = SourceIdKind.Cedula, string number = ValidCedula, string first = "Ana", string last = "Pérez",
        string? email = "ana@correo.test", string? address = "Av. Principal 123", string? phone = "0991234567") =>
        new(Guid.NewGuid(), first, last, kind, number, email, phone, address);

    private static bool Has(BuyerResolution r, string code, bool? blocking = null) =>
        r.Issues.Any(i => i.Code == code && (blocking is null || i.Blocking == blocking));

    [Theory]
    [InlineData(SriBuyerIdType.Ruc, "04")]
    [InlineData(SriBuyerIdType.Cedula, "05")]
    [InlineData(SriBuyerIdType.Passport, "06")]
    [InlineData(SriBuyerIdType.FinalConsumer, "07")]
    [InlineData(SriBuyerIdType.Foreign, "08")]
    public void Buyer_id_types_use_the_codes_of_table_6_of_the_SRI_technical_sheet(SriBuyerIdType type, string code) =>
        Assert.Equal(code, type.Code());

    [Theory]
    [InlineData(SourceIdKind.Ruc, SriBuyerIdType.Ruc)]
    [InlineData(SourceIdKind.Cedula, SriBuyerIdType.Cedula)]
    [InlineData(SourceIdKind.Passport, SriBuyerIdType.Passport)]
    public void The_registered_document_maps_to_its_SRI_type(SourceIdKind kind, SriBuyerIdType expected) =>
        Assert.Equal(expected, SriBuyerIdTypes.DefaultFor(kind));

    [Fact]
    public void A_document_registered_as_other_has_no_default_and_must_be_chosen()
    {
        Assert.Null(SriBuyerIdTypes.DefaultFor(SourceIdKind.Other));

        var unresolved = BuyerRules.Resolve(Source(SourceIdKind.Other, "AB12345"), null, 30m, Limit);
        Assert.False(unresolved.IsValid);
        Assert.Null(unresolved.Buyer);
        Assert.True(Has(unresolved, "id_type_required", true));

        var asForeign = BuyerRules.Resolve(Source(SourceIdKind.Other, "AB12345"), SriBuyerIdType.Foreign, 30m, Limit);
        Assert.True(asForeign.IsValid);
        Assert.Equal("08", asForeign.Buyer!.IdTypeCode);
    }

    [Fact]
    public void A_valid_cedula_and_complete_data_resolve_without_issues_and_normalize_the_number_and_name()
    {
        var result = BuyerRules.Resolve(Source(number: " 171234567-5 ", first: "  Ana   María ", last: "Pérez  "), null, 30m, Limit);

        Assert.True(result.IsValid);
        Assert.Empty(result.Issues);
        Assert.Equal(SriBuyerIdType.Cedula, result.Buyer!.IdType);
        Assert.Equal(ValidCedula, result.Buyer.IdNumber);
        Assert.Equal("Ana María Pérez", result.Buyer.LegalName);
        Assert.False(result.Buyer.IsFinalConsumer);
    }

    [Theory]
    [InlineData(SourceIdKind.Cedula, "1712345674", "cedula_invalid")]
    [InlineData(SourceIdKind.Ruc, "1790000029001", "ruc_invalid")]
    [InlineData(SourceIdKind.Passport, "AB1", "passport_invalid")]
    public void An_invalid_number_for_its_type_blocks_issuing(SourceIdKind kind, string number, string code)
    {
        var result = BuyerRules.Resolve(Source(kind, number), null, 30m, Limit);

        Assert.False(result.IsValid);
        Assert.True(Has(result, code, true));
    }

    [Fact]
    public void A_valid_ruc_is_accepted_as_type_04()
    {
        var result = BuyerRules.Resolve(Source(SourceIdKind.Ruc, ValidRuc, "Gimnasio", "Cliente S.A."), null, 120m, Limit);

        Assert.True(result.IsValid);
        Assert.Equal("04", result.Buyer!.IdTypeCode);
    }

    [Fact]
    public void Final_consumer_uses_thirteen_nines_and_the_fixed_name_and_drops_personal_data()
    {
        var result = BuyerRules.Resolve(Source(), SriBuyerIdType.FinalConsumer, 49.99m, Limit);

        Assert.True(result.IsValid);
        Assert.Equal("07", result.Buyer!.IdTypeCode);
        Assert.Equal("9999999999999", result.Buyer.IdNumber);
        Assert.Equal("CONSUMIDOR FINAL", result.Buyer.LegalName);
        Assert.Null(result.Buyer.Email);
        Assert.Null(result.Buyer.Address);
        Assert.Null(result.Buyer.PersonId);
        Assert.True(result.Buyer.IsFinalConsumer);
    }

    [Theory]
    [InlineData(50.00, true)]
    [InlineData(50.01, false)]
    [InlineData(200.00, false)]
    public void Final_consumer_is_only_allowed_up_to_the_configured_limit(double total, bool allowed)
    {
        var result = BuyerRules.Resolve(Source(), SriBuyerIdType.FinalConsumer, (decimal)total, Limit);

        Assert.Equal(allowed, result.IsValid);
        Assert.Equal(!allowed, Has(result, "final_consumer_over_limit", true));
    }

    [Fact]
    public void The_final_consumer_limit_is_a_parameter()
    {
        Assert.False(BuyerRules.Resolve(Source(), SriBuyerIdType.FinalConsumer, 80m, 50m).IsValid);
        Assert.True(BuyerRules.Resolve(Source(), SriBuyerIdType.FinalConsumer, 80m, 100m).IsValid);
    }

    [Fact]
    public void Missing_address_or_email_only_warns()
    {
        var result = BuyerRules.Resolve(Source(email: null, address: "  "), null, 30m, Limit);

        Assert.True(result.IsValid);
        Assert.True(Has(result, "email_missing", false));
        Assert.True(Has(result, "address_missing", false));
        Assert.Null(result.Buyer!.Email);
        Assert.Null(result.Buyer.Address);
    }

    [Theory]
    [InlineData("sin-arroba")]
    [InlineData("a@b")]
    [InlineData("Nombre <a@b.test>")]
    public void An_invalid_email_warns_and_is_not_kept(string email)
    {
        var result = BuyerRules.Resolve(Source(email: email), null, 30m, Limit);

        Assert.True(result.IsValid);
        Assert.True(Has(result, "email_invalid", false));
        Assert.Null(result.Buyer!.Email);
    }

    [Fact]
    public void The_email_is_lowercased()
    {
        var result = BuyerRules.Resolve(Source(email: "ANA@Correo.TEST"), null, 30m, Limit);

        Assert.Equal("ana@correo.test", result.Buyer!.Email);
    }

    [Fact]
    public void An_empty_or_too_long_name_or_address_blocks_issuing()
    {
        Assert.True(Has(BuyerRules.Resolve(Source(first: " ", last: ""), null, 30m, Limit), "name_required", true));
        Assert.True(Has(BuyerRules.Resolve(Source(first: new string('A', 200), last: new string('B', 200)), null, 30m, Limit), "name_too_long", true));
        Assert.True(Has(BuyerRules.Resolve(Source(address: new string('C', 301)), null, 30m, Limit), "address_too_long", true));
    }

    [Fact]
    public void Chosen_type_overrides_the_default_and_is_validated_against_the_number()
    {
        // Un RUC registrado como cédula (o al revés) no pasa: el número debe cumplir el tipo elegido.
        var asRuc = BuyerRules.Resolve(Source(SourceIdKind.Cedula, ValidCedula), SriBuyerIdType.Ruc, 30m, Limit);
        Assert.True(Has(asRuc, "ruc_invalid", true));

        var asPassport = BuyerRules.Resolve(Source(SourceIdKind.Cedula, ValidCedula), SriBuyerIdType.Passport, 30m, Limit);
        Assert.True(asPassport.IsValid);
        Assert.Equal("06", asPassport.Buyer!.IdTypeCode);
    }
}
