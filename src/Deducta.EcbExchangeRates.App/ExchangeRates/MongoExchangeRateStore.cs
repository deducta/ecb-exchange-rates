using Deducta.EcbExchangeRates.App.Dtos;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Deducta.EcbExchangeRates.App.ExchangeRates;

public sealed class MongoExchangeRateStore(IMongoCollection<ExchangeRateObservation> collection) : IExchangeRateStore
{
    private readonly SemaphoreSlim indexLock = new(1, 1);

    private bool indexExists;

    public async Task<IReadOnlyList<ExchangeRateObservation>> GetAsync(
        DateOnly startDate,
        DateOnly endDate,
        CancellationToken cancellationToken = default)
    {
        await EnsureIndexAsync(cancellationToken);
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
        await EnsureIndexAsync(cancellationToken);
        var writes = exchangeRates
            .Where(exchangeRate => exchangeRate.Provider != null)
            .DistinctBy(exchangeRate => (exchangeRate.Provider, exchangeRate.Date))
            .Select(exchangeRate =>
            {
                var filter = Builders<ExchangeRateObservation>.Filter.And(
                    Builders<ExchangeRateObservation>.Filter.Eq(rate => rate.Provider, exchangeRate.Provider),
                    Builders<ExchangeRateObservation>.Filter.Eq(rate => rate.Date, exchangeRate.Date));
                return new ReplaceOneModel<ExchangeRateObservation>(filter, exchangeRate) { IsUpsert = true };
            })
            .ToList();
        if (writes.Count == 0)
        {
            return;
        }

        await collection.BulkWriteAsync(writes, cancellationToken: cancellationToken);
    }

    private async Task EnsureIndexAsync(CancellationToken cancellationToken)
    {
        if (indexExists)
        {
            return;
        }

        await indexLock.WaitAsync(cancellationToken);
        try
        {
            if (indexExists)
            {
                return;
            }

            var keys = Builders<ExchangeRateObservation>.IndexKeys
                .Ascending(rate => rate.Provider)
                .Ascending(rate => rate.Date);
            var options = new CreateIndexOptions<ExchangeRateObservation>
            {
                Name = "provider_effective_date",
                Unique = true,
                PartialFilterExpression = Builders<ExchangeRateObservation>.Filter.Type(
                    rate => rate.Provider,
                    BsonType.String),
            };
            await collection.Indexes.CreateOneAsync(
                new CreateIndexModel<ExchangeRateObservation>(keys, options),
                cancellationToken: cancellationToken);
            indexExists = true;
        }
        finally
        {
            indexLock.Release();
        }
    }

    private static DateTimeOffset AtMidnight(DateOnly date) =>
        new(date.Year, date.Month, date.Day, 0, 0, 0, TimeSpan.Zero);
}
