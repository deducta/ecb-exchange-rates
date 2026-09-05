using Deducta.EcbExchangeRates.App.Dtos;
using Deducta.EcbExchangeRates.App.ExchangeRates;
using FluentAssertions;

namespace App.UnitTests.ExchangeRates;

public sealed class ExchangeRateResolverTests
{
    private static readonly DateTimeOffset Today = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Resolves_non_euro_pairs_through_the_euro_using_decimal_arithmetic()
    {
        var repository = new RecordingRepository(Date(2026, 8, 20), Rates(("USD", 1.10m), ("GBP", 0.85m)));
        var resolver = new ExchangeRateResolver(repository, new FixedTimeProvider(Today));

        var result = await resolver.Resolve([new ExchangeRateRequest("usd", "gbp", Date(2026, 8, 20))]);

        result.Should().ContainSingle().Which.Should().Be(
            new ResolvedExchangeRate(
                "USD",
                "GBP",
                Date(2026, 8, 20),
                Date(2026, 8, 20),
                Date(2026, 8, 19),
                0.85m / 1.10m,
                ExchangeRateResolutionStatuses.Resolved));
    }

    [Fact]
    public async Task Same_currency_needs_no_external_lookup()
    {
        var repository = new RecordingRepository(Date(2026, 8, 20), Rates());
        var resolver = new ExchangeRateResolver(repository, new FixedTimeProvider(Today));

        var result = await resolver.Resolve([new ExchangeRateRequest("EUR", "EUR", Date(2026, 8, 20))]);

        result.Should().ContainSingle().Which.Rate.Should().Be(1m);
        repository.RequestedDates.Should().BeEmpty();
    }

    [Fact]
    public async Task Future_dates_are_clamped_and_the_requested_lookup_and_effective_dates_are_distinct()
    {
        var repository = new RecordingRepository(Date(2026, 8, 27), Rates(("USD", 1.2m)));
        var resolver = new ExchangeRateResolver(repository, new FixedTimeProvider(Today));

        var result = await resolver.Resolve([new ExchangeRateRequest("USD", "EUR", Date(2026, 9, 5))]);

        result.Should().ContainSingle().Which.Should().Match<ResolvedExchangeRate>(rate =>
            rate.ConversionDate == Date(2026, 9, 5)
            && rate.LookupDate == Date(2026, 8, 27)
            && rate.EffectiveDate == Date(2026, 8, 26));
        repository.RequestedDates.Should().Equal(Date(2026, 8, 27));
    }

    [Fact]
    public async Task Requests_on_the_same_date_share_one_repository_lookup()
    {
        var repository = new RecordingRepository(Date(2026, 8, 20), Rates(("USD", 1.1m), ("GBP", 0.8m)));
        var resolver = new ExchangeRateResolver(repository, new FixedTimeProvider(Today));

        await resolver.Resolve(
        [
            new ExchangeRateRequest("USD", "EUR", Date(2026, 8, 20)),
            new ExchangeRateRequest("GBP", "EUR", Date(2026, 8, 20)),
        ]);

        repository.RequestedDates.Should().Equal(Date(2026, 8, 20));
    }

    [Fact]
    public async Task Unsupported_currencies_are_reported_without_inventing_a_rate()
    {
        var repository = new RecordingRepository(Date(2026, 8, 20), Rates(("USD", 1.1m)));
        var resolver = new ExchangeRateResolver(repository, new FixedTimeProvider(Today));

        var result = await resolver.Resolve([new ExchangeRateRequest("XXX", "EUR", Date(2026, 8, 20))]);

        result.Should().ContainSingle().Which.Should().Match<ResolvedExchangeRate>(rate =>
            rate.Rate == null && rate.Status == ExchangeRateResolutionStatuses.RateUnavailable);
    }

    [Theory]
    [InlineData("")]
    [InlineData("EU")]
    [InlineData("E1R")]
    public async Task Rejects_malformed_currency_codes(string currency)
    {
        var resolver = new ExchangeRateResolver(
            new RecordingRepository(Date(2026, 8, 20), Rates()),
            new FixedTimeProvider(Today));

        var act = () => resolver.Resolve([new ExchangeRateRequest(currency, "EUR", Date(2026, 8, 20))]);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    private static DateOnly Date(int year, int month, int day) => new(year, month, day);

    private static List<RateDto> Rates(params (string Currency, decimal Rate)[] values) =>
        values.Select(value => new RateDto { CurrencyCode = value.Currency, Rate = value.Rate }).ToList();

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RecordingRepository(DateOnly lookupDate, List<RateDto> rates) : IExchangeRateResolutionRepository
    {
        public List<DateOnly> RequestedDates { get; } = [];

        public Task<IReadOnlyDictionary<DateOnly, ExchangeRateLookupResult>> ResolveAsync(
            IReadOnlyDictionary<DateOnly, IReadOnlySet<string>> requirements,
            CancellationToken cancellationToken = default)
        {
            RequestedDates.AddRange(requirements.Keys);
            IReadOnlyDictionary<DateOnly, ExchangeRateLookupResult> resolved = requirements.ToDictionary(
                requirement => requirement.Key,
                requirement => new ExchangeRateLookupResult(
                    new ExchangeRateObservation
                    {
                        Id = ExchangeRateObservation.CreateId(
                            ExchangeRateProviders.Ecb,
                            AtMidnight(requirement.Key).Ticks),
                        Date = AtMidnight(requirement.Key).Ticks,
                        EffectiveDate = AtMidnight(lookupDate.AddDays(-1)).Ticks,
                        Provider = ExchangeRateProviders.Ecb,
                        Rates = rates,
                    },
                    ExchangeRateLookupStatuses.Resolved));
            return Task.FromResult(resolved);
        }

        public Task RefreshRecentAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        private static DateTimeOffset AtMidnight(DateOnly date) =>
            new(
                date.Year,
                date.Month,
                date.Day,
                0,
                0,
                0,
                TimeSpan.Zero);
    }
}
