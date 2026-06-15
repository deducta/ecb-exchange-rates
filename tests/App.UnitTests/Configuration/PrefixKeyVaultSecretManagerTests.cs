using Azure.Security.KeyVault.Secrets;
using Deducta.EcbExchangeRates.App.Configuration;
using FluentAssertions;

namespace App.UnitTests.Configuration;

public class PrefixKeyVaultSecretManagerTests
{
    private const string AppPrefix = "ecb-exchange-rates";

    [Theory]
    [InlineData("ecb-exchange-rates--MongoDbConnectionString")]
    [InlineData("shared--ApplicationInsightsConnectionString")]
    public void Load_returns_true_for_app_prefixed_and_shared_secrets(string secretName)
    {
        var manager = new PrefixKeyVaultSecretManager(AppPrefix);

        manager.Load(new SecretProperties(secretName)).Should().BeTrue();
    }

    [Theory]
    [InlineData("other-app--MongoDbConnectionString")]
    [InlineData("MongoDbConnectionString")]
    [InlineData("sharedSomethingElse")]
    public void Load_returns_false_for_other_prefixes_and_unprefixed_names(string secretName)
    {
        var manager = new PrefixKeyVaultSecretManager(AppPrefix);

        manager.Load(new SecretProperties(secretName)).Should().BeFalse();
    }

    [Theory]
    [InlineData("ECB-EXCHANGE-RATES--MongoDbConnectionString")]
    [InlineData("SHARED--MongoDbConnectionString")]
    public void Load_matches_the_prefix_case_insensitively(string secretName)
    {
        var manager = new PrefixKeyVaultSecretManager(AppPrefix);

        manager.Load(new SecretProperties(secretName)).Should().BeTrue();
    }

    [Theory]
    [InlineData("ecb-exchange-rates--MongoDbConnectionString", "MongoDbConnectionString")]
    [InlineData("shared--OpenExchangeRateApiKey", "OpenExchangeRateApiKey")]
    [InlineData("ecb-exchange-rates--ConnectionStrings--MongoDb", "ConnectionStrings:MongoDb")]
    public void GetKey_strips_the_matched_prefix_and_maps_double_dash_to_colon(
        string secretName, string expectedKey)
    {
        var manager = new PrefixKeyVaultSecretManager(AppPrefix);

        manager.GetKey(new KeyVaultSecret(secretName, "value")).Should().Be(expectedKey);
    }

    [Fact]
    public void Empty_app_prefix_loads_only_shared_secrets()
    {
        var manager = new PrefixKeyVaultSecretManager(string.Empty);

        manager.Load(new SecretProperties("shared--MongoDbConnectionString")).Should().BeTrue();
        manager.Load(new SecretProperties("ecb-exchange-rates--MongoDbConnectionString")).Should().BeFalse();
    }
}
