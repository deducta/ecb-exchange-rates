namespace Deducta.EcbExchangeRates.App.ExchangeRates;

public interface IExchangeRateResolutionRepository
{
    Task<IReadOnlyDictionary<DateOnly, ExchangeRateLookupResult>> ResolveAsync(
        IReadOnlyDictionary<DateOnly, IReadOnlySet<string>> requirements,
        CancellationToken cancellationToken = default);

    Task RefreshRecentAsync(CancellationToken cancellationToken = default);
}
