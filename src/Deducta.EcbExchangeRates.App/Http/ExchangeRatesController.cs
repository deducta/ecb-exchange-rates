using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Deducta.EcbExchangeRates.App.Dtos;
using Deducta.EcbExchangeRates.App.ExchangeRates;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace Deducta.EcbExchangeRates.App.Http;

public class ExchangeRatesController(
    IExchangeRateRepository exchangeRateRepository,
    ExchangeRateResolver exchangeRateResolver)
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

    [Function(nameof(ResolveExchangeRates))]
    public async Task<HttpResponseData> ResolveExchangeRates(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "exchange-rates/resolve")]
        HttpRequestData req,
        FunctionContext context)
    {
        try
        {
            var request = await req.ReadFromJsonAsync<ResolveExchangeRatesRequest>(context.CancellationToken);
            if (request?.Items == null)
            {
                return req.CreateResponse(HttpStatusCode.BadRequest);
            }

            var rates = await exchangeRateResolver.Resolve(request.Items, context.CancellationToken);
            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(rates, context.CancellationToken);
            return response;
        }
        catch (Exception exception) when (exception is ArgumentException or JsonException)
        {
            var response = req.CreateResponse(HttpStatusCode.BadRequest);
            await response.WriteAsJsonAsync(new { error = exception.Message }, context.CancellationToken);
            return response;
        }
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
