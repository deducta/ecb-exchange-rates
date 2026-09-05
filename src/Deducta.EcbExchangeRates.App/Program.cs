using System.Security.Authentication;
using Azure.Identity;
using Deducta.EcbExchangeRates.App.Configuration;
using Deducta.EcbExchangeRates.App.CurrencyApi;
using Deducta.EcbExchangeRates.App.Dtos;
using Deducta.EcbExchangeRates.App.Ecb;
using Deducta.EcbExchangeRates.App.ExchangeRates;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

var keyVaultUri = builder.Configuration["KEYVAULT_URI"];
if (!string.IsNullOrWhiteSpace(keyVaultUri))
{
    var appPrefix = builder.Configuration["APP_NAME"] ?? string.Empty;
    builder.Configuration.AddAzureKeyVault(
        new Uri(keyVaultUri),
        new DefaultAzureCredential(),
        new PrefixKeyVaultSecretManager(appPrefix));
}

BsonClassMap.RegisterClassMap<ExchangeRate>(
    classMap =>
    {
        classMap.AutoMap();
        classMap.SetIgnoreExtraElements(true);
    });
var mongoDbConnectionString = builder.Configuration.GetSection("MongoDbConnectionString").Value ??
                              throw new NullReferenceException("MongoDbConnectionString");
var openExchangeRateKey = builder.Configuration.GetSection("OpenExchangeRateApiKey").Value ??
                          throw new NullReferenceException("OpenExchangeRateApiKey");
builder.Services.AddHttpClient("CurrencyApi",
    client => { client.BaseAddress = new Uri("https://api.currencyapi.com"); });
builder.Services.AddHttpClient("Ecb",
    client => { client.BaseAddress = new Uri("https://data-api.ecb.europa.eu/service/"); });
builder.Services.AddSingleton<MongoClient>(_ =>
{
    var settings = MongoClientSettings.FromUrl(
        new MongoUrl(mongoDbConnectionString)
    );
    settings.SslSettings =
        new SslSettings() { EnabledSslProtocols = SslProtocols.Tls12 };
    return new MongoClient(settings);
});
builder.Services.AddTransient<IExchangeRateRepository>(sp =>
{
    var mongoClient = sp.GetRequiredService<MongoClient>();
    var collection = mongoClient.GetDatabase("Rates").GetCollection<ExchangeRate>("Rates");
    return new CurrencyApiExchangeRateRepository(
        sp.GetRequiredService<IHttpClientFactory>().CreateClient("CurrencyApi"),
        openExchangeRateKey, collection);
});
builder.Services.AddSingleton<IExchangeRateStore>(sp =>
{
    var mongoClient = sp.GetRequiredService<MongoClient>();
    var collection = mongoClient
        .GetDatabase("Rates")
        .GetCollection<ExchangeRateObservation>("HistoricalRatesV2");
    return new MongoExchangeRateStore(collection);
});
builder.Services.AddSingleton<IEcbExchangeRateSource>(sp =>
    new EcbExchangeRateSource(sp.GetRequiredService<IHttpClientFactory>().CreateClient("Ecb")));
builder.Services.AddSingleton<ICurrencyApiExchangeRateSource>(sp =>
    new CurrencyApiExchangeRateSource(
        sp.GetRequiredService<IHttpClientFactory>().CreateClient("CurrencyApi"),
        openExchangeRateKey,
        sp.GetRequiredService<TimeProvider>(),
        sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<CurrencyApiExchangeRateSource>>()));
builder.Services.AddScoped<IExchangeRateResolutionRepository, ExchangeRateResolutionRepository>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<ExchangeRateResolver>();

builder.Services
    .AddApplicationInsightsTelemetryWorkerService()
    .ConfigureFunctionsApplicationInsights();

builder.Build().Run();
