using Azure.Extensions.AspNetCore.Configuration.Secrets;
using Azure.Security.KeyVault.Secrets;

namespace Deducta.EcbExchangeRates.App.Configuration;

public sealed class PrefixKeyVaultSecretManager(string appPrefix) : KeyVaultSecretManager
{
    private readonly string[] prefixes =
        string.IsNullOrEmpty(appPrefix) ? ["shared--"] : [$"{appPrefix}--", "shared--"];

    public override bool Load(SecretProperties secret) =>
        this.prefixes.Any(p => secret.Name.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    public override string GetKey(KeyVaultSecret secret)
    {
        var prefix = this.prefixes.First(p => secret.Name.StartsWith(p, StringComparison.OrdinalIgnoreCase));
        return secret.Name[prefix.Length..].Replace("--", ":");
    }
}
