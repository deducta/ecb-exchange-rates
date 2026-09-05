using Deducta.EcbExchangeRates.App.Dtos;

namespace Deducta.EcbExchangeRates.App.ExchangeRates;

public interface ICurrencyApiExchangeRateSource
{
    Task<CurrencyApiQuota> GetQuotaAsync(CancellationToken cancellationToken = default);

    Task<CurrencyApiRateResult> GetAsync(
        DateOnly date,
        CancellationToken cancellationToken = default);
}

public sealed record CurrencyApiQuota(int Remaining, bool Available);

public sealed record CurrencyApiRateResult(ExchangeRateObservation? Observation, bool DependencyUnavailable);
