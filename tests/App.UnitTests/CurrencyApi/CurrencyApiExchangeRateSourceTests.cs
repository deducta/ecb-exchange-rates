using System.Net;
using Deducta.EcbExchangeRates.App.CurrencyApi;
using Deducta.EcbExchangeRates.App.ExchangeRates;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace App.UnitTests.CurrencyApi;

public sealed class CurrencyApiExchangeRateSourceTests
{
    [Fact]
    public async Task Preflights_the_monthly_and_grace_quota_without_putting_the_key_in_the_url()
    {
        var handler = new RecordingHandler(_ => Json(
            """
            {"quotas":{"month":{"remaining":12},"grace":{"remaining":3}}}
            """));
        var source = Source(handler);

        var result = await source.GetQuotaAsync();

        result.Should().Be(new CurrencyApiQuota(15, true));
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Uri.AbsoluteUri.Should().Be("https://currency.test/v3/status");
        handler.Requests[0].ApiKey.Should().Be("secret-key");
    }

    [Fact]
    public async Task A_quota_response_opens_the_local_circuit_for_later_dates()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        var source = Source(handler);

        var first = await source.GetAsync(new DateOnly(2026, 8, 20));
        var second = await source.GetAsync(new DateOnly(2026, 8, 21));

        first.DependencyUnavailable.Should().BeTrue();
        second.DependencyUnavailable.Should().BeTrue();
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task Stores_the_historical_effective_date_and_provider()
    {
        var handler = new RecordingHandler(_ => Json(
            """
            {
              "meta":{"last_updated_at":"2026-08-20T23:59:59Z"},
              "data":{"USD":{"value":1.1681},"GBP":{"value":0.85725}}
            }
            """));
        var source = Source(handler);

        var result = await source.GetAsync(new DateOnly(2026, 8, 20));

        result.DependencyUnavailable.Should().BeFalse();
        result.Observation!.Provider.Should().Be(ExchangeRateProviders.CurrencyApi);
        result.Observation.Rates.Should().Contain(rate => rate.CurrencyCode == "USD" && rate.Rate == 1.1681m);
        handler.Requests[0].Uri.Query.Should().Contain("date=2026-08-20");
    }

    private static CurrencyApiExchangeRateSource Source(HttpMessageHandler handler) =>
        new(
            new HttpClient(handler) { BaseAddress = new Uri("https://currency.test/") },
            "secret-key",
            TimeProvider.System,
            NullLogger<CurrencyApiExchangeRateSource>.Instance);

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
    };

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new RecordedRequest(request.RequestUri!, request.Headers.GetValues("apikey").Single()));
            return Task.FromResult(respond(request));
        }
    }

    private sealed record RecordedRequest(Uri Uri, string ApiKey);
}
