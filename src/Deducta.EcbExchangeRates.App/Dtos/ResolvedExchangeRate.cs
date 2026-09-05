namespace Deducta.EcbExchangeRates.App.Dtos;

public sealed record ResolvedExchangeRate(
    string SourceCurrency,
    string TargetCurrency,
    DateOnly ConversionDate,
    DateOnly LookupDate,
    DateOnly EffectiveDate,
    decimal? Rate,
    string Status);

public static class ExchangeRateResolutionStatuses
{
    public const string Resolved = "resolved";

    public const string RateUnavailable = "rate_unavailable";

    public const string DependencyUnavailable = "dependency_unavailable";
}
