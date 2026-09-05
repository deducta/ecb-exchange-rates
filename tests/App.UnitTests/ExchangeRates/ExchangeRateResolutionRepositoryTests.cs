using Deducta.EcbExchangeRates.App.Dtos;
using Deducta.EcbExchangeRates.App.ExchangeRates;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace App.UnitTests.ExchangeRates;

public sealed class ExchangeRateResolutionRepositoryTests
{
    private static readonly DateTimeOffset Today = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Hydrates_a_date_range_from_ECB_once_and_uses_the_previous_working_day()
    {
        var store = new MemoryStore();
        var ecb = new EcbSource(
        [
            Rate(new DateOnly(2026, 8, 21), ExchangeRateProviders.Ecb, ("USD", 1.17m), ("GBP", 0.86m)),
            Rate(new DateOnly(2026, 8, 24), ExchangeRateProviders.Ecb, ("USD", 1.18m), ("GBP", 0.87m)),
        ]);
        var currencyApi = new CurrencyApiSource(new CurrencyApiQuota(0, true));
        var repository = Repository(store, ecb, currencyApi);

        var result = await repository.ResolveAsync(Requirements(
            (new DateOnly(2026, 8, 22), ["USD", "EUR"]),
            (new DateOnly(2026, 8, 24), ["GBP", "EUR"])));

        Date(result[new DateOnly(2026, 8, 22)].Observation!).Should().Be(new DateOnly(2026, 8, 21));
        Date(result[new DateOnly(2026, 8, 24)].Observation!).Should().Be(new DateOnly(2026, 8, 24));
        ecb.Requests.Should().ContainSingle();
        store.Stored.Should().HaveCount(2);
        currencyApi.QuotaRequests.Should().Be(0);
    }

    [Fact]
    public async Task Reuses_the_shared_provider_cache_without_an_external_call()
    {
        var cached = Rate(new DateOnly(2026, 8, 20), ExchangeRateProviders.Ecb, ("USD", 1.17m));
        var store = new MemoryStore([cached]);
        var ecb = new EcbSource([]);
        var currencyApi = new CurrencyApiSource(new CurrencyApiQuota(0, true));
        var repository = Repository(store, ecb, currencyApi);

        var result = await repository.ResolveAsync(Requirements(
            (new DateOnly(2026, 8, 20), ["USD", "EUR"])));

        result.Values.Should().ContainSingle(value => value.Status == ExchangeRateLookupStatuses.Resolved);
        ecb.Requests.Should().BeEmpty();
        currencyApi.QuotaRequests.Should().Be(0);
    }

    [Fact]
    public async Task Does_not_spend_fallback_quota_when_the_batch_cannot_be_completed()
    {
        var store = new MemoryStore();
        var ecb = new EcbSource([]);
        var currencyApi = new CurrencyApiSource(new CurrencyApiQuota(1, true));
        var repository = Repository(store, ecb, currencyApi);

        var result = await repository.ResolveAsync(Requirements(
            (new DateOnly(2026, 8, 20), ["AED", "EUR"]),
            (new DateOnly(2026, 8, 21), ["AED", "EUR"])));

        result.Values.Should().OnlyContain(value => value.Status == ExchangeRateLookupStatuses.DependencyUnavailable);
        currencyApi.RateRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task Uses_CurrencyAPI_only_for_a_currency_ECB_does_not_cover()
    {
        var date = new DateOnly(2026, 8, 20);
        var store = new MemoryStore();
        var ecb = new EcbSource([]);
        var currencyApi = new CurrencyApiSource(
            new CurrencyApiQuota(1, true),
            new Dictionary<DateOnly, CurrencyApiRateResult>
            {
                [date] = new(Rate(date, ExchangeRateProviders.CurrencyApi, ("AED", 4.28m)), false),
            });
        var repository = Repository(store, ecb, currencyApi);

        var result = await repository.ResolveAsync(Requirements((date, ["AED", "EUR"])));

        result[date].Status.Should().Be(ExchangeRateLookupStatuses.Resolved);
        result[date].Observation!.Provider.Should().Be(ExchangeRateProviders.CurrencyApi);
        currencyApi.RateRequests.Should().Equal(date);
        store.Stored.Should().ContainSingle(rate => rate.Provider == ExchangeRateProviders.CurrencyApi);
    }

    private static ExchangeRateResolutionRepository Repository(
        IExchangeRateStore store,
        IEcbExchangeRateSource ecb,
        ICurrencyApiExchangeRateSource currencyApi) =>
        new(
            store,
            ecb,
            currencyApi,
            new FixedTimeProvider(Today),
            NullLogger<ExchangeRateResolutionRepository>.Instance);

    private static IReadOnlyDictionary<DateOnly, IReadOnlySet<string>> Requirements(
        params (DateOnly Date, string[] Currencies)[] requirements) =>
        requirements.ToDictionary(
            requirement => requirement.Date,
            requirement => (IReadOnlySet<string>)requirement.Currencies.ToHashSet(StringComparer.Ordinal));

    private static ExchangeRateObservation Rate(
        DateOnly date,
        string provider,
        params (string Currency, decimal Rate)[] rates)
    {
        var atMidnight = new DateTimeOffset(date.Year, date.Month, date.Day, 0, 0, 0, TimeSpan.Zero);
        return new ExchangeRateObservation
        {
            Id = ExchangeRateObservation.CreateId(provider, atMidnight.Ticks),
            Date = atMidnight.Ticks,
            EffectiveDate = atMidnight.Ticks,
            Provider = provider,
            Rates = rates.Select(rate => new RateDto
            {
                CurrencyCode = rate.Currency,
                Rate = rate.Rate,
            }).ToList(),
        };
    }

    private static DateOnly Date(ExchangeRateObservation rate) =>
        DateOnly.FromDateTime(new DateTime(rate.EffectiveDate, DateTimeKind.Utc));

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class MemoryStore(IEnumerable<ExchangeRateObservation>? initial = null) : IExchangeRateStore
    {
        private readonly List<ExchangeRateObservation> rates = initial?.ToList() ?? [];

        public List<ExchangeRateObservation> Stored { get; } = [];

        public Task<IReadOnlyList<ExchangeRateObservation>> GetAsync(
            DateOnly startDate,
            DateOnly endDate,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ExchangeRateObservation>>(rates.ToList());

        public Task StoreAsync(
            IReadOnlyCollection<ExchangeRateObservation> exchangeRates,
            CancellationToken cancellationToken = default)
        {
            Stored.AddRange(exchangeRates);
            rates.AddRange(exchangeRates);
            return Task.CompletedTask;
        }
    }

    private sealed class EcbSource(IReadOnlyList<ExchangeRateObservation> rates) : IEcbExchangeRateSource
    {
        public List<EcbRequest> Requests { get; } = [];

        public Task<IReadOnlyList<ExchangeRateObservation>> GetAsync(
            DateOnly startDate,
            DateOnly endDate,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(new EcbRequest(startDate, endDate));
            return Task.FromResult(rates);
        }
    }

    private sealed record EcbRequest(DateOnly StartDate, DateOnly EndDate);

    private sealed class CurrencyApiSource(
        CurrencyApiQuota quota,
        IReadOnlyDictionary<DateOnly, CurrencyApiRateResult>? rates = null) : ICurrencyApiExchangeRateSource
    {
        public int QuotaRequests { get; private set; }

        public List<DateOnly> RateRequests { get; } = [];

        public Task<CurrencyApiQuota> GetQuotaAsync(CancellationToken cancellationToken = default)
        {
            QuotaRequests++;
            return Task.FromResult(quota);
        }

        public Task<CurrencyApiRateResult> GetAsync(
            DateOnly date,
            CancellationToken cancellationToken = default)
        {
            RateRequests.Add(date);
            return Task.FromResult(
                rates?.GetValueOrDefault(date) ?? new CurrencyApiRateResult(null, false));
        }
    }
}
