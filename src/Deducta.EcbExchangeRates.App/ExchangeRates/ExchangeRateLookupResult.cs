using Deducta.EcbExchangeRates.App.Dtos;

namespace Deducta.EcbExchangeRates.App.ExchangeRates;

public sealed record ExchangeRateLookupResult(ExchangeRateObservation? Observation, string Status);

public static class ExchangeRateLookupStatuses
{
    public const string Resolved = "resolved";

    public const string RateUnavailable = "rate_unavailable";

    public const string DependencyUnavailable = "dependency_unavailable";
}
