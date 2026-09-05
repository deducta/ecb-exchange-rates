using Deducta.EcbExchangeRates.App.Dtos;

namespace Deducta.EcbExchangeRates.App.ExchangeRates;

public interface IEcbExchangeRateSource
{
    Task<IReadOnlyList<ExchangeRateObservation>> GetAsync(
        DateOnly startDate,
        DateOnly endDate,
        CancellationToken cancellationToken = default);
}
