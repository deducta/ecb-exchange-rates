using System.Net.Http.Json;
using System.Runtime.Serialization;
using System.Text.Json;
using System.Web;
using Deducta.EcbExchangeRates.App.Dtos;
using Deducta.EcbExchangeRates.App.ExchangeRates;
using MongoDB.Driver;
using MongoDB.Driver.Linq;

namespace Deducta.EcbExchangeRates.App.CurrencyApi;

public class CurrencyApiExchangeRateRepository(
    HttpClient httpClient,
    string apiKey,
    IMongoCollection<ExchangeRate> collection)
    : IExchangeRateRepository
{
    public async Task<ExchangeRate> GetExchangeRatesFromRemote(CancellationToken cancellationToken = default)
    {
        var today = DateTimeOffset.UtcNow;
        var dateOnly = new DateTimeOffset(today.Year, today.Month, today.Day, 0, 0, 0, TimeSpan.Zero);
        return await GetExchangeRatesFromRemote(dateOnly, cancellationToken);
    }

    private async Task<ExchangeRate> GetExchangeRatesFromRemote(DateTimeOffset date,
        CancellationToken cancellationToken = default)
    {
        var url = $"{httpClient.BaseAddress}v3/latest";
        var dateOnly = new DateTimeOffset(date.Year, date.Month, date.Day, 0, 0, 0, TimeSpan.Zero);

        // Use UriBuilder to set up query parameters
        var uriBuilder = new UriBuilder(url);
        var query = HttpUtility.ParseQueryString(string.Empty);

        query["apikey"] = apiKey;
        query["currencies"] = "";
        query["base_currency"] = "EUR";

        // Assign the constructed query string back to the UriBuilder
        uriBuilder.Query = query.ToString();
        var jsonOptions = new JsonSerializerOptions()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            AllowTrailingCommas = true
        };
        var fullUrl = uriBuilder.ToString();
        var response =
            await httpClient.GetFromJsonAsync<CurrencyResponse>(fullUrl, jsonOptions, cancellationToken);
        if (response == null)
        {
            throw new SerializationException("Could not deserialize currency");
        }

        return new ExchangeRate
        {
            Date = dateOnly.Ticks,
            Rates = response.Data.Select(d => new RateDto
            {
                Rate = d.Value.Value,
                CurrencyCode = d.Key
            }).ToList()
        };
    }

    public async Task<ExchangeRate> GetStoredExchangeRatesWithFallback(DateTimeOffset date,
        CancellationToken cancellationToken = default)
    {
        var foundDate = await GetStoredExchangeRates(date, cancellationToken);
        if (foundDate != null) return foundDate;
        var rates = await GetExchangeRatesFromRemote(date, cancellationToken);
        await StoreExchangeRates([rates], cancellationToken);
        var recordedDate = await GetStoredExchangeRates(date, cancellationToken);
        return recordedDate ?? throw new Exception("Could not find stored rates");
    }

    public async Task<ExchangeRate?> GetStoredExchangeRates(DateTimeOffset date,
        CancellationToken cancellationToken = default)
    {
        var startOfDay = date.Date;
        var startOfNextDay = startOfDay.AddDays(1);

        var filter = Builders<ExchangeRate>.Filter.And(
            Builders<ExchangeRate>.Filter.Gte(x => x.Date, startOfDay.Ticks),
            Builders<ExchangeRate>.Filter.Lt(x => x.Date, startOfNextDay.Ticks)
        );
        var data = await collection.FindAsync(filter, cancellationToken: cancellationToken);
        return await data.FirstOrDefaultAsync(cancellationToken: cancellationToken);
    }

    public async Task<ExchangeRate> GetYearlyAverageExchangeRate(int year,
        CancellationToken cancellationToken = default)
    {
        var startOfYear = new DateTimeOffset(year, 01, 01, 00, 00, 00, TimeSpan.Zero);
        var startOfNextYear = startOfYear.AddYears(1);

        var filter = Builders<ExchangeRate>.Filter.And(
            Builders<ExchangeRate>.Filter.Gte(x => x.Date, startOfYear.Ticks),
            Builders<ExchangeRate>.Filter.Lt(x => x.Date, startOfNextYear.Ticks)
        );
        var dataCursor = await collection.FindAsync(filter, cancellationToken: cancellationToken);
        var data = await dataCursor.ToListAsync(cancellationToken: cancellationToken);
        data = data.DistinctBy(i => i.Date).ToList();
        var dayWithMostRates = data.OrderByDescending(i => i.Rates.Count).First();
        var rates = dayWithMostRates.Rates.Select(rate =>
        {
            var daysWithRate = data.Where(i => i.Rates.Any(r => r.CurrencyCode == rate.CurrencyCode)).ToList();
            var sumOfRate =
                daysWithRate.Sum(i => i.Rates.Where(r => r.CurrencyCode == rate.CurrencyCode).Sum(r => r.Rate));
            return new RateDto
            {
                Rate = sumOfRate / daysWithRate.Count,
                CurrencyCode = rate.CurrencyCode
            };
        }).ToList();
        return new ExchangeRate
        {
            Date = startOfYear.Ticks,
            Rates = rates,
        };
    }

    public async Task StoreExchangeRates(List<ExchangeRate> exchangeRates,
        CancellationToken cancellationToken = default)
    {
        await collection.InsertManyAsync(exchangeRates, cancellationToken: cancellationToken);
    }

    public class CurrencyResponse
    {
        public required Meta Meta { get; set; }

        public required Dictionary<string, Currency> Data { get; set; }
    }

    public class Meta
    {
        public required string LastUpdatedAt { get; set; }
    }

    public class Currency
    {
        public required string Code { get; set; }

        public required decimal Value { get; set; }
    }
}