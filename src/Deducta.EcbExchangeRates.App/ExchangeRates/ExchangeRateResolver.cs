using Deducta.EcbExchangeRates.App.Dtos;

namespace Deducta.EcbExchangeRates.App.ExchangeRates;

public sealed class ExchangeRateResolver(
    IExchangeRateResolutionRepository exchangeRateRepository,
    TimeProvider timeProvider)
{
    public const int MaximumBatchSize = 500;

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

        var requirements = normalized
            .Where(request => request.SourceCurrency != request.TargetCurrency)
            .GroupBy(request => request.LookupDate)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlySet<string>)group
                    .SelectMany(request => new[] { request.SourceCurrency, request.TargetCurrency })
                    .ToHashSet(StringComparer.Ordinal));
        var ratesByDate = requirements.Count == 0
            ? new Dictionary<DateOnly, ExchangeRateLookupResult>()
            : await exchangeRateRepository.ResolveAsync(requirements, cancellationToken);

        return normalized.Select(request => Resolve(request, ratesByDate)).ToList();
    }

    private static ResolvedExchangeRate Resolve(
        NormalizedRequest request,
        IReadOnlyDictionary<DateOnly, ExchangeRateLookupResult> ratesByDate)
    {
        if (request.SourceCurrency == request.TargetCurrency)
        {
            return Result(request, request.LookupDate, 1m, ExchangeRateResolutionStatuses.Resolved);
        }

        var lookup = ratesByDate[request.LookupDate];
        var daily = lookup.Observation;
        if (daily == null)
        {
            return Result(request, request.LookupDate, null, lookup.Status);
        }

        var rates = daily.Rates.ToDictionary(
            rate => rate.CurrencyCode.ToUpperInvariant(),
            rate => rate.Rate,
            StringComparer.Ordinal);
        var sourceRate = request.SourceCurrency == "EUR" ? 1m : rates.GetValueOrDefault(request.SourceCurrency);
        var targetRate = request.TargetCurrency == "EUR" ? 1m : rates.GetValueOrDefault(request.TargetCurrency);
        decimal? rate = sourceRate == 0 || targetRate == 0 ? null : targetRate / sourceRate;
        var effectiveDate = DateOnly.FromDateTime(new DateTime(daily.EffectiveDate, DateTimeKind.Utc));

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
