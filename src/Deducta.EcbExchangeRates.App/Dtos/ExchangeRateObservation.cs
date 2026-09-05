namespace Deducta.EcbExchangeRates.App.Dtos;

public sealed class ExchangeRateObservation
{
    public required string Id { get; set; }

    public required long Date { get; set; }

    public required long EffectiveDate { get; set; }

    public required string Provider { get; set; }

    public required List<RateDto> Rates { get; set; }

    public static string CreateId(string provider, long effectiveDate) => $"{provider}:{effectiveDate}";
}
