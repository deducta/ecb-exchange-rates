namespace Deducta.EcbExchangeRates.App.Dtos;

public sealed record ResolveExchangeRatesRequest(IReadOnlyList<ExchangeRateRequest> Items);

public sealed record ExchangeRateRequest(
    string SourceCurrency,
    string TargetCurrency,
    DateOnly ConversionDate);
