using System.Net;
using System.Text.Json;
using Deducta.EcbExchangeRates.App.ExchangeRates;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace Deducta.EcbExchangeRates.App.Http;

public class ExchangeRatesController(IExchangeRateRepository exchangeRateRepository)
{
    [Function(nameof(GetExchangeRates))]
    public async Task<HttpResponseData> GetExchangeRates(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "exchange-rates/{date}")]
        HttpRequestData req,
        string date, FunctionContext context)
    {
        var canParse = DateTimeOffset.TryParse(date, out var dateParsed);
        if (!canParse)
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }

        var rates = await exchangeRateRepository.GetStoredExchangeRatesWithFallback(dateParsed);
        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(rates, new JsonSerializerOptions()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        }));
        return response;
    }

    [Function(nameof(GetYearlyAvarageRates))]
    public async Task<HttpResponseData> GetYearlyAvarageRates(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "exchange-rates/yearly-average/{year}")]
        HttpRequestData req,
        int year, FunctionContext context)
    {
        var currentYear = DateTimeOffset.UtcNow.Year;
        var validYear = year > 1999 && year <= currentYear;
        if (!validYear)
        {
            return req.CreateResponse(HttpStatusCode.BadRequest);
        }

        var rates = await exchangeRateRepository.GetYearlyAverageExchangeRate(year);
        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(JsonSerializer.Serialize(rates, new JsonSerializerOptions()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        }));
        return response;
    }
}