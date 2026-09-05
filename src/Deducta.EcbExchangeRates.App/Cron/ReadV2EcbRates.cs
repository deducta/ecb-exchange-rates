using Deducta.EcbExchangeRates.App.ExchangeRates;
using Microsoft.Azure.Functions.Worker;

namespace Deducta.EcbExchangeRates.App.Cron;

public sealed class ReadV2EcbRates(IExchangeRateResolutionRepository exchangeRates)
{
    [Function(nameof(RefreshV2EcbExchangeRates))]
    public Task RefreshV2EcbExchangeRates(
        [TimerTrigger("0 5 0 * * *")] TimerInfo timer,
        FunctionContext context) =>
        exchangeRates.RefreshRecentAsync(context.CancellationToken);
}
