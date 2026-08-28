using System.Collections.Concurrent;
using Deducta.EcbExchangeRates.App.Dtos;

namespace Deducta.EcbExchangeRates.App.ExchangeRates;

public sealed class ExchangeRateResolver(
    IExchangeRateRepository exchangeRateRepository,
    TimeProvider timeProvider)
{
    public const int MaximumBatchSize = 500;

    private const int MaximumParallelDates = 8;

    public async Task<IReadOnlyList<ResolvedExchangeRate>> Resolve(
        IReadOnlyList<ExchangeRateRequest> requests,
        CancellationToken cancellationToken = default)
    {
        Validate(requests);

        var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        var normalized = requests.Select(request => new NormalizedRequest(
            request.SourceCurrency.Trim().ToUpperInvariant(),
            request.TargetCurrency.Trim().ToUpperInvariant(),
            request.ConversionDate,
            request.ConversionDate >= today ? today.AddDays(-1) : request.ConversionDate)).ToList();

        var dates = normalized
            .Where(request => request.SourceCurrency != request.TargetCurrency)
            .Select(request => request.LookupDate)
            .Distinct()
            .ToList();
        var ratesByDate = new ConcurrentDictionary<DateOnly, ExchangeRate>();

        await Parallel.ForEachAsync(
            dates,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = MaximumParallelDates,
            },
            async (date, token) =>
            {
                var atMidnight = new DateTimeOffset(date.Year, date.Month, date.Day, 0, 0, 0, TimeSpan.Zero);
                ratesByDate[date] = await exchangeRateRepository.GetStoredExchangeRatesWithFallback(
                    atMidnight,
                    token);
            });

        return normalized.Select(request => Resolve(request, ratesByDate)).ToList();
    }

    private static ResolvedExchangeRate Resolve(
        NormalizedRequest request,
        IReadOnlyDictionary<DateOnly, ExchangeRate> ratesByDate)
    {
        if (request.SourceCurrency == request.TargetCurrency)
        {
            return Result(request, request.LookupDate, 1m, ExchangeRateResolutionStatuses.Resolved);
        }

        var daily = ratesByDate[request.LookupDate];
        var rates = daily.Rates.ToDictionary(
            rate => rate.CurrencyCode.ToUpperInvariant(),
            rate => rate.Rate,
            StringComparer.Ordinal);
        var sourceRate = request.SourceCurrency == "EUR" ? 1m : rates.GetValueOrDefault(request.SourceCurrency);
        var targetRate = request.TargetCurrency == "EUR" ? 1m : rates.GetValueOrDefault(request.TargetCurrency);
        decimal? rate = sourceRate == 0 || targetRate == 0 ? null : targetRate / sourceRate;
        var effectiveDate = daily.EffectiveDate is > 0
            ? DateOnly.FromDateTime(new DateTime(daily.EffectiveDate.Value, DateTimeKind.Utc))
            : request.LookupDate;

        return Result(
            request,
            effectiveDate,
            rate,
            rate == null
                ? ExchangeRateResolutionStatuses.RateUnavailable
                : ExchangeRateResolutionStatuses.Resolved);
    }

    private static ResolvedExchangeRate Result(
        NormalizedRequest request,
        DateOnly effectiveDate,
        decimal? rate,
        string status) =>
        new(
            request.SourceCurrency,
            request.TargetCurrency,
            request.ConversionDate,
            request.LookupDate,
            effectiveDate,
            rate,
            status);

    private static void Validate(IReadOnlyList<ExchangeRateRequest> requests)
    {
        if (requests.Count is < 1 or > MaximumBatchSize)
        {
            throw new ArgumentException($"items must contain between 1 and {MaximumBatchSize} requests");
        }

        foreach (var request in requests)
        {
            RequireCurrency(request.SourceCurrency, "sourceCurrency");
            RequireCurrency(request.TargetCurrency, "targetCurrency");
        }
    }

    private static void RequireCurrency(string? currency, string field)
    {
        if (currency == null || currency.Trim().Length != 3 || !currency.Trim().All(char.IsAsciiLetter))
        {
            throw new ArgumentException($"{field} must be a three-letter currency code");
        }
    }

    private sealed record NormalizedRequest(
        string SourceCurrency,
        string TargetCurrency,
        DateOnly ConversionDate,
        DateOnly LookupDate);
}
