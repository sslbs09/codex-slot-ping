using System.Security.Cryptography;
using System.Text;

namespace CodexSlotPing.Update;

public sealed record InstallIdentity
{
    public const string ProductIdValue = "CodexSlotPing";

    private InstallIdentity(string productId, string installRoot, string key)
    {
        ProductId = productId;
        InstallRoot = installRoot;
        Key = key;
    }

    public string ProductId { get; }
    public string InstallRoot { get; }
    public string Key { get; }

    public static InstallIdentity Create(string productId, string installRoot)
    {
        if (!string.Equals(productId, ProductIdValue, StringComparison.Ordinal))
        {
            throw new ArgumentException("The product ID is not supported.", nameof(productId));
        }
        if (string.IsNullOrWhiteSpace(installRoot) || !Path.IsPathFullyQualified(installRoot))
        {
            throw new ArgumentException("The install root must be an absolute path.", nameof(installRoot));
        }

        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installRoot));
        var identityInput = $"{ProductIdValue.ToUpperInvariant()}\n{normalizedRoot.ToUpperInvariant()}";
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identityInput)));
        return new InstallIdentity(ProductIdValue, normalizedRoot, key);
    }
}
