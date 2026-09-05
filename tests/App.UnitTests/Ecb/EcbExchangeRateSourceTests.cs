using System.Net;
using Deducta.EcbExchangeRates.App.Ecb;
using Deducta.EcbExchangeRates.App.ExchangeRates;
using FluentAssertions;

namespace App.UnitTests.Ecb;

public sealed class EcbExchangeRateSourceTests
{
    [Fact]
    public async Task Reads_daily_observations_from_one_bulk_request()
    {
        const string csv =
            "KEY,FREQ,CURRENCY,CURRENCY_DENOM,EXR_TYPE,EXR_SUFFIX,TIME_PERIOD,OBS_VALUE\n"
            + "EXR.D.GBP.EUR.SP00.A,D,GBP,EUR,SP00,A,2026-08-20,0.85725\n"
            + "EXR.D.USD.EUR.SP00.A,D,USD,EUR,SP00,A,2026-08-20,1.1681\n"
            + "EXR.D.USD.EUR.SP00.A,D,USD,EUR,SP00,A,2026-08-21,1.1699\n";
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(csv),
        });
        var source = new EcbExchangeRateSource(new HttpClient(handler)
        {
            BaseAddress = new Uri("https://ecb.test/service/"),
        });

        var result = await source.GetAsync(
            new DateOnly(2026, 8, 20),
            new DateOnly(2026, 8, 21));

        result.Should().HaveCount(2);
        result[0].Provider.Should().Be(ExchangeRateProviders.Ecb);
        result[0].Rates.Should().BeEquivalentTo(
        [
            new { CurrencyCode = "GBP", Rate = 0.85725m },
            new { CurrencyCode = "USD", Rate = 1.1681m },
        ]);
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].PathAndQuery.Should().Be(
            "/service/data/EXR/D..EUR.SP00.A?startPeriod=2026-08-20&endPeriod=2026-08-21&format=csvdata&detail=dataonly");
    }

    [Fact]
    public async Task Treats_an_unsupported_currency_query_as_no_observations()
    {
        var source = new EcbExchangeRateSource(new HttpClient(
            new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)))
        {
            BaseAddress = new Uri("https://ecb.test/service/"),
        });

        var result = await source.GetAsync(
            new DateOnly(2026, 8, 20),
            new DateOnly(2026, 8, 21));

        result.Should().BeEmpty();
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(respond(request));
        }
    }
}
