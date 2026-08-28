namespace Deducta.EcbExchangeRates.App.Dtos;

public class ExchangeRate
{
    public required long Date { get; set; }

    public long? EffectiveDate { get; set; }

    public required List<RateDto> Rates { get; set; }
}
