using Deducta.EcbExchangeRates.App.Dtos;
using MongoDB.Driver;

namespace Deducta.EcbExchangeRates.App.ExchangeRates;

public sealed class MongoExchangeRateStore(IMongoCollection<ExchangeRateObservation> collection) : IExchangeRateStore
{
    public async Task<IReadOnlyList<ExchangeRateObservation>> GetAsync(
        DateOnly startDate,
        DateOnly endDate,
        CancellationToken cancellationToken = default)
    {
        var start = AtMidnight(startDate);
        var endExclusive = AtMidnight(endDate.AddDays(1));
        var filter = Builders<ExchangeRateObservation>.Filter.And(
            Builders<ExchangeRateObservation>.Filter.Ne(rate => rate.Provider, null),
            Builders<ExchangeRateObservation>.Filter.Gte(rate => rate.Date, start.Ticks),
            Builders<ExchangeRateObservation>.Filter.Lt(rate => rate.Date, endExclusive.Ticks));
        using var cursor = await collection.FindAsync(filter, cancellationToken: cancellationToken);
        return await cursor.ToListAsync(cancellationToken);
    }

    public async Task StoreAsync(
        IReadOnlyCollection<ExchangeRateObservation> exchangeRates,
        CancellationToken cancellationToken = default)
    {
        var writes = exchangeRates
            .DistinctBy(exchangeRate => exchangeRate.Id)
            .Select(exchangeRate =>
            {
                var filter = Builders<ExchangeRateObservation>.Filter.Eq(rate => rate.Id, exchangeRate.Id);
                return new ReplaceOneModel<ExchangeRateObservation>(filter, exchangeRate) { IsUpsert = true };
            })
            .ToList();
        if (writes.Count == 0)
        {
            return;
        }

        await collection.BulkWriteAsync(writes, cancellationToken: cancellationToken);
    }

    private static DateTimeOffset AtMidnight(DateOnly date) =>
        new(date.Year, date.Month, date.Day, 0, 0, 0, TimeSpan.Zero);
}
