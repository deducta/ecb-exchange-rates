using Deducta.EcbExchangeRates.App.Dtos;
using Microsoft.Extensions.Logging;

namespace Deducta.EcbExchangeRates.App.ExchangeRates;

public sealed class ExchangeRateResolutionRepository(
    IExchangeRateStore store,
    IEcbExchangeRateSource ecb,
    ICurrencyApiExchangeRateSource currencyApi,
    TimeProvider timeProvider,
    ILogger<ExchangeRateResolutionRepository> logger) : IExchangeRateResolutionRepository
{
    private const int MaximumLookbackDays = 7;

    private static readonly DateOnly FirstEcbReferenceDate = new(1999, 1, 4);

    public async Task<IReadOnlyDictionary<DateOnly, ExchangeRateLookupResult>> ResolveAsync(
        IReadOnlyDictionary<DateOnly, IReadOnlySet<string>> requirements,
        CancellationToken cancellationToken = default)
    {
        if (requirements.Count == 0)
        {
            return new Dictionary<DateOnly, ExchangeRateLookupResult>();
        }

        var firstDate = requirements.Keys.Min();
        var lastDate = requirements.Keys.Max();
        var startDate = SafeSubtract(firstDate, MaximumLookbackDays);
        var cached = (await store.GetAsync(startDate, lastDate, cancellationToken)).ToList();
        var resolved = ResolveFrom(cached, requirements);
        var missing = Missing(requirements, resolved);

        if (missing.Count > 0)
        {
            var ecbDates = missing.Where(date => date >= FirstEcbReferenceDate).ToList();
            try
            {
                if (ecbDates.Count > 0)
                {
                    var observations = await ecb.GetAsync(
                        DateOnly.FromDayNumber(Math.Max(
                            FirstEcbReferenceDate.DayNumber,
                            ecbDates.Min().DayNumber - MaximumLookbackDays)),
                        ecbDates.Max(),
                        cancellationToken);
                    var merged = Merge(cached, observations);
                    var incomingKeys = observations
                        .Select(rate => (rate.Provider, rate.Date))
                        .ToHashSet();
                    await store.StoreAsync(
                        merged.Where(rate => incomingKeys.Contains((rate.Provider, rate.Date))).ToList(),
                        cancellationToken);
                    cached = merged;
                    resolved = ResolveFrom(cached, requirements);
                    missing = Missing(requirements, resolved);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "ECB historical exchange-rate hydration failed");
            }
        }

        if (missing.Count == 0)
        {
            return resolved;
        }

        var quota = await currencyApi.GetQuotaAsync(cancellationToken);
        if (!quota.Available || quota.Remaining < missing.Count)
        {
            logger.LogWarning(
                "CurrencyAPI fallback requires {RequiredCalls} calls but {RemainingCalls} remain; no fallback calls were made",
                missing.Count,
                quota.Remaining);
            return WithUnavailable(resolved, missing, ExchangeRateLookupStatuses.DependencyUnavailable);
        }

        var fallbackRates = new List<ExchangeRateObservation>();
        var dependencyUnavailable = new HashSet<DateOnly>();
        foreach (var date in missing)
        {
            var result = await currencyApi.GetAsync(date, cancellationToken);
            if (result.Observation != null)
            {
                fallbackRates.Add(result.Observation);
                cached = Merge(cached, [result.Observation]);
                continue;
            }

            if (result.DependencyUnavailable)
            {
                dependencyUnavailable.Add(date);
            }
        }

        await store.StoreAsync(fallbackRates, cancellationToken);
        resolved = ResolveFrom(cached, requirements);
        missing = Missing(requirements, resolved);
        foreach (var date in missing)
        {
            resolved[date] = new ExchangeRateLookupResult(
                null,
                dependencyUnavailable.Contains(date)
                    ? ExchangeRateLookupStatuses.DependencyUnavailable
                    : ExchangeRateLookupStatuses.RateUnavailable);
        }

        return resolved;
    }

    public async Task RefreshRecentAsync(CancellationToken cancellationToken = default)
    {
        var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        var observations = await ecb.GetAsync(today.AddDays(-MaximumLookbackDays), today, cancellationToken);
        var cached = await store.GetAsync(today.AddDays(-MaximumLookbackDays), today, cancellationToken);
        await store.StoreAsync(Merge(cached, observations), cancellationToken);
    }

    private static Dictionary<DateOnly, ExchangeRateLookupResult> ResolveFrom(
        IReadOnlyCollection<ExchangeRateObservation> observations,
        IReadOnlyDictionary<DateOnly, IReadOnlySet<string>> requirements) =>
        requirements.ToDictionary(
            requirement => requirement.Key,
            requirement =>
            {
                var rate = Select(observations, requirement.Key, requirement.Value);
                return new ExchangeRateLookupResult(
                    rate,
                    rate == null ? ExchangeRateLookupStatuses.RateUnavailable : ExchangeRateLookupStatuses.Resolved);
            });

    private static ExchangeRateObservation? Select(
        IEnumerable<ExchangeRateObservation> observations,
        DateOnly requestedDate,
        IReadOnlySet<string> currencies)
    {
        var earliestDate = SafeSubtract(requestedDate, MaximumLookbackDays);
        return observations
            .Where(rate =>
            {
                var effectiveDate = ToDate(rate);
                return effectiveDate >= earliestDate
                    && effectiveDate <= requestedDate
                    && Contains(rate, currencies);
            })
            .OrderBy(rate => rate.Provider == ExchangeRateProviders.Ecb ? 0 : 1)
            .ThenByDescending(ToDate)
            .FirstOrDefault();
    }

    private static bool Contains(ExchangeRateObservation exchangeRate, IReadOnlySet<string> currencies)
    {
        var available = exchangeRate.Rates
            .Select(rate => rate.CurrencyCode)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return currencies.All(currency => currency == "EUR" || available.Contains(currency));
    }

    private static List<DateOnly> Missing(
        IReadOnlyDictionary<DateOnly, IReadOnlySet<string>> requirements,
        IReadOnlyDictionary<DateOnly, ExchangeRateLookupResult> resolved) =>
        requirements.Keys
            .Where(date => resolved.GetValueOrDefault(date)?.Observation == null)
            .Order()
            .ToList();

    private static Dictionary<DateOnly, ExchangeRateLookupResult> WithUnavailable(
        Dictionary<DateOnly, ExchangeRateLookupResult> resolved,
        IEnumerable<DateOnly> dates,
        string status)
    {
        foreach (var date in dates)
        {
            resolved[date] = new ExchangeRateLookupResult(null, status);
        }

        return resolved;
    }

    private static List<ExchangeRateObservation> Merge(
        IEnumerable<ExchangeRateObservation> existing,
        IEnumerable<ExchangeRateObservation> incoming) =>
        existing
            .Concat(incoming)
            .Where(rate => rate.Provider != null)
            .GroupBy(rate => (rate.Provider, rate.Date))
            .Select(group => new ExchangeRateObservation
            {
                Date = group.Key.Date,
                EffectiveDate = group.Select(rate => rate.EffectiveDate).FirstOrDefault(date => date is > 0),
                Provider = group.Key.Provider,
                Rates = group
                    .SelectMany(rate => rate.Rates)
                    .GroupBy(rate => rate.CurrencyCode, StringComparer.OrdinalIgnoreCase)
                    .Select(currency => currency.Last())
                    .ToList(),
            })
            .ToList();

    private static DateOnly ToDate(ExchangeRateObservation exchangeRate) =>
        DateOnly.FromDateTime(new DateTime(exchangeRate.EffectiveDate, DateTimeKind.Utc));

    private static DateTimeOffset AtMidnight(DateOnly date) =>
        new(date.Year, date.Month, date.Day, 0, 0, 0, TimeSpan.Zero);

    private static DateOnly SafeSubtract(DateOnly date, int days) =>
        DateOnly.FromDayNumber(Math.Max(DateOnly.MinValue.DayNumber, date.DayNumber - days));
}
