using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace Retail.Api;

internal enum RetailRole
{
    Admin,
    Manager,
    Cashier
}

internal sealed class ModelEditorAccess
{
    private readonly Dictionary<RetailRole, byte[]> tokenHashes = new();

    public ModelEditorAccess(IConfiguration configuration)
    {
        var configuredTokens = new HashSet<string>(StringComparer.Ordinal);
        foreach (var role in Enum.GetValues<RetailRole>())
        {
            var token = configuration[$"RetailAccess:{role}Token"];
            if (string.IsNullOrEmpty(token))
            {
                continue;
            }

            if (token.Length < 32 || token.Length > 512 ||
                token.Any(character => character <= ' ' || character > '~' || character == ',') ||
                !configuredTokens.Add(token))
            {
                throw new InvalidOperationException("Configure distinct RetailAccess bearer tokens of 32–512 ASCII characters without whitespace or commas.");
            }

            tokenHashes.Add(role, Hash(token));
        }
    }

    public RetailRole RequireRole(HttpContext context)
    {
        var values = context.Request.Headers.Authorization;
        if (values.Count == 1 && AuthenticationHeaderValue.TryParse(values[0], out var header) &&
            string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) &&
            header.Parameter is { Length: >= 32 and <= 512 } token)
        {
            var receivedHash = Hash(token);
            foreach (var (role, configuredHash) in tokenHashes)
            {
                if (CryptographicOperations.FixedTimeEquals(receivedHash, configuredHash))
                {
                    return role;
                }
            }
        }

        context.Response.Headers.WWWAuthenticate = "Bearer";
        throw new RetailException(401, "A valid bearer token is required.");
    }

    private static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));
}
