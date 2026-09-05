using Deducta.EcbExchangeRates.App.Dtos;

namespace Deducta.EcbExchangeRates.App.ExchangeRates;

public interface IExchangeRateStore
{
    Task<IReadOnlyList<ExchangeRateObservation>> GetAsync(
        DateOnly startDate,
        DateOnly endDate,
        CancellationToken cancellationToken = default);

    Task StoreAsync(
        IReadOnlyCollection<ExchangeRateObservation> exchangeRates,
        CancellationToken cancellationToken = default);
}
