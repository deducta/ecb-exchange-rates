using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Web;
using Deducta.EcbExchangeRates.App.Dtos;
using Deducta.EcbExchangeRates.App.ExchangeRates;
using Microsoft.Extensions.Logging;

namespace Deducta.EcbExchangeRates.App.CurrencyApi;

public sealed class CurrencyApiExchangeRateSource(
    HttpClient httpClient,
    string apiKey,
    TimeProvider timeProvider,
    ILogger<CurrencyApiExchangeRateSource> logger) : ICurrencyApiExchangeRateSource
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        AllowTrailingCommas = true,
    };

    private long blockedUntilUtcTicks;

    public async Task<CurrencyApiQuota> GetQuotaAsync(CancellationToken cancellationToken = default)
    {
        if (IsBlocked())
        {
            return new CurrencyApiQuota(0, false);
        }

        using var request = CreateRequest("v3/status");
        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "CurrencyAPI quota preflight returned {StatusCode}",
                    (int)response.StatusCode);
                return new CurrencyApiQuota(0, false);
            }

            var status = await response.Content.ReadFromJsonAsync<StatusResponse>(JsonOptions, cancellationToken);
            return status == null
                ? new CurrencyApiQuota(0, false)
                : new CurrencyApiQuota(
                    status.Quotas.Month.Remaining + status.Quotas.Grace.Remaining,
                    true);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or NotSupportedException)
        {
            logger.LogWarning(exception, "CurrencyAPI quota preflight failed");
            return new CurrencyApiQuota(0, false);
        }
    }

    public async Task<CurrencyApiRateResult> GetAsync(
        DateOnly date,
        CancellationToken cancellationToken = default)
    {
        if (IsBlocked())
        {
            return new CurrencyApiRateResult(null, true);
        }

        var uriBuilder = new UriBuilder(new Uri(httpClient.BaseAddress!, "v3/historical"));
        var query = HttpUtility.ParseQueryString(string.Empty);
        query["currencies"] = string.Empty;
        query["base_currency"] = "EUR";
        query["date"] = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        uriBuilder.Query = query.ToString();

        using var request = CreateRequest(uriBuilder.Uri);
        try
        {
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                Interlocked.Exchange(
                    ref blockedUntilUtcTicks,
                    timeProvider.GetUtcNow().AddMinutes(1).UtcTicks);
                logger.LogWarning("CurrencyAPI rate limit or quota was reached; fallback is paused for one minute");
                return new CurrencyApiRateResult(null, true);
            }

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "CurrencyAPI historical lookup for {Date} returned {StatusCode}",
                    date,
                    (int)response.StatusCode);
                return new CurrencyApiRateResult(
                    null,
                    (int)response.StatusCode >= 500 || response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);
            }

            var body = await response.Content.ReadFromJsonAsync<CurrencyResponse>(JsonOptions, cancellationToken);
            if (body == null)
            {
                logger.LogWarning("CurrencyAPI historical lookup for {Date} returned no body", date);
                return new CurrencyApiRateResult(null, true);
            }

            var effectiveDate = ParseEffectiveDate(body.Meta.LastUpdatedAt, date);
            var atMidnight = AtMidnight(effectiveDate);
            return new CurrencyApiRateResult(
                new ExchangeRateObservation
                {
                    Id = ExchangeRateObservation.CreateId(ExchangeRateProviders.CurrencyApi, atMidnight.Ticks),
                    Date = atMidnight.Ticks,
                    EffectiveDate = atMidnight.Ticks,
                    Provider = ExchangeRateProviders.CurrencyApi,
                    Rates = body.Data
                        .Select(value => new RateDto
                        {
                            CurrencyCode = value.Key.ToUpperInvariant(),
                            Rate = value.Value.Value,
                        })
                        .ToList(),
                },
                false);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or NotSupportedException)
        {
            logger.LogWarning(exception, "CurrencyAPI historical lookup for {Date} failed", date);
            return new CurrencyApiRateResult(null, true);
        }
    }

    private HttpRequestMessage CreateRequest(string endpoint) => CreateRequest(new Uri(httpClient.BaseAddress!, endpoint));

    private bool IsBlocked() =>
        timeProvider.GetUtcNow().UtcTicks < Interlocked.Read(ref blockedUntilUtcTicks);

    private HttpRequestMessage CreateRequest(Uri endpoint)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Add("apikey", apiKey);
        return request;
    }

    private static DateOnly ParseEffectiveDate(string value, DateOnly fallback) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? DateOnly.FromDateTime(parsed.UtcDateTime)
            : fallback;

    private static DateTimeOffset AtMidnight(DateOnly date) =>
        new(date.Year, date.Month, date.Day, 0, 0, 0, TimeSpan.Zero);

    public sealed class StatusResponse
    {
        public required Quotas Quotas { get; set; }
    }

    public sealed class Quotas
    {
        public required Quota Month { get; set; }

        public required Quota Grace { get; set; }
    }

    public sealed class Quota
    {
        public required int Remaining { get; set; }
    }

    public sealed class CurrencyResponse
    {
        public required Meta Meta { get; set; }

        public required Dictionary<string, Currency> Data { get; set; }
    }

    public sealed class Meta
    {
        public required string LastUpdatedAt { get; set; }
    }

    public sealed class Currency
    {
        public required decimal Value { get; set; }
    }
}
