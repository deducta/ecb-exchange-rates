using System.Globalization;
using Deducta.EcbExchangeRates.App.Dtos;
using Deducta.EcbExchangeRates.App.ExchangeRates;

namespace Deducta.EcbExchangeRates.App.Ecb;

public sealed class EcbExchangeRateSource(HttpClient httpClient) : IEcbExchangeRateSource
{
    private const int MaximumRequestDays = 366;

    public async Task<IReadOnlyList<ExchangeRateObservation>> GetAsync(
        DateOnly startDate,
        DateOnly endDate,
        CancellationToken cancellationToken = default)
    {
        if (endDate < startDate)
        {
            throw new ArgumentException("endDate must not be before startDate");
        }

        var observations = new List<Observation>();

        for (var chunkStart = startDate; chunkStart <= endDate; chunkStart = chunkStart.AddDays(MaximumRequestDays))
        {
            var chunkEnd = DateOnly.FromDayNumber(
                Math.Min(endDate.DayNumber, chunkStart.DayNumber + MaximumRequestDays - 1));
            observations.AddRange(
                await GetChunkAsync(chunkStart, chunkEnd, cancellationToken));
        }

        return observations
            .GroupBy(observation => observation.Date)
            .Select(group =>
            {
                var atMidnight = AtMidnight(group.Key);
                return new ExchangeRateObservation
                {
                    Id = ExchangeRateObservation.CreateId(ExchangeRateProviders.Ecb, atMidnight.Ticks),
                    Date = atMidnight.Ticks,
                    EffectiveDate = atMidnight.Ticks,
                    Provider = ExchangeRateProviders.Ecb,
                    Rates = group
                        .GroupBy(observation => observation.Currency, StringComparer.Ordinal)
                        .Select(currency => currency.Last())
                        .OrderBy(observation => observation.Currency, StringComparer.Ordinal)
                        .Select(observation => new RateDto
                        {
                            CurrencyCode = observation.Currency,
                            Rate = observation.Rate,
                        })
                        .ToList(),
                };
            })
            .OrderBy(exchangeRate => exchangeRate.Date)
            .ToList();
    }

    private async Task<IReadOnlyList<Observation>> GetChunkAsync(
        DateOnly startDate,
        DateOnly endDate,
        CancellationToken cancellationToken)
    {
        var endpoint = string.Create(
            CultureInfo.InvariantCulture,
            $"data/EXR/D..EUR.SP00.A?startPeriod={startDate:yyyy-MM-dd}"
            + $"&endPeriod={endDate:yyyy-MM-dd}&format=csvdata&detail=dataonly");
        using var response = await httpClient.GetAsync(endpoint, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return [];
        }

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        return await ReadCsvAsync(reader, cancellationToken);
    }

    private static async Task<IReadOnlyList<Observation>> ReadCsvAsync(
        TextReader reader,
        CancellationToken cancellationToken)
    {
        var header = await reader.ReadLineAsync(cancellationToken);
        if (header == null)
        {
            return [];
        }

        var columns = header.TrimStart('\uFEFF').Split(',');
        var currencyIndex = Array.IndexOf(columns, "CURRENCY");
        var dateIndex = Array.IndexOf(columns, "TIME_PERIOD");
        var rateIndex = Array.IndexOf(columns, "OBS_VALUE");
        if (currencyIndex < 0 || dateIndex < 0 || rateIndex < 0)
        {
            throw new InvalidDataException("ECB exchange-rate response did not contain the expected columns.");
        }

        var observations = new List<Observation>();
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            var values = line.Split(',');
            if (values.Length <= Math.Max(currencyIndex, Math.Max(dateIndex, rateIndex))
                || !DateOnly.TryParseExact(
                    values[dateIndex],
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var date)
                || !decimal.TryParse(
                    values[rateIndex],
                    NumberStyles.Number,
                    CultureInfo.InvariantCulture,
                    out var rate))
            {
                throw new InvalidDataException("ECB exchange-rate response contained an invalid observation.");
            }

            observations.Add(new Observation(date, values[currencyIndex].ToUpperInvariant(), rate));
        }

        return observations;
    }

    private static DateTimeOffset AtMidnight(DateOnly date) =>
        new(date.Year, date.Month, date.Day, 0, 0, 0, TimeSpan.Zero);

    private sealed record Observation(DateOnly Date, string Currency, decimal Rate);
}
