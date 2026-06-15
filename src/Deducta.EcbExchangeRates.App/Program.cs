using System.Security.Authentication;
using Azure.Identity;
using Deducta.EcbExchangeRates.App.Configuration;
using Deducta.EcbExchangeRates.App.CurrencyApi;
using Deducta.EcbExchangeRates.App.Dtos;
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
builder.Services.AddScoped<MongoClient>(_ =>
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

builder.Services
    .AddApplicationInsightsTelemetryWorkerService()
    .ConfigureFunctionsApplicationInsights();

builder.Build().Run();