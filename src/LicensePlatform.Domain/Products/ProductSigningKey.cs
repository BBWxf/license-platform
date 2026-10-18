using LicensePlatform.Domain.Common;

namespace LicensePlatform.Domain.Products;

public enum SigningKeyStatus
{
    Active = 1,
    Retired = 2,
    Revoked = 3
}

public sealed class ProductSigningKey : Entity
{
    private ProductSigningKey()
    {
    }

    public ProductSigningKey(
        Guid id,
        Guid productId,
        string keyId,
        string publicKeyPem,
        byte[] protectedPrivateKey,
        DateTimeOffset createdAtUtc)
        : base(id)
    {
        if (productId == Guid.Empty)
        {
            throw new DomainException("Product id cannot be empty.");
        }

        ProductId = productId;
        KeyId = Guard.Required(keyId, nameof(keyId), 128);
        PublicKeyPem = Guard.Required(publicKeyPem, nameof(publicKeyPem), 2_048);
        ProtectedPrivateKey = Guard.Bytes(protectedPrivateKey, nameof(protectedPrivateKey));
        Algorithm = "ES256";
        Status = SigningKeyStatus.Active;
        CreatedAtUtc = Guard.Utc(createdAtUtc, nameof(createdAtUtc));
    }

    public Guid ProductId { get; private set; }

    public Product? Product { get; private set; }

    public string KeyId { get; private set; } = string.Empty;

    public string Algorithm { get; private set; } = "ES256";

    public string PublicKeyPem { get; private set; } = string.Empty;

    public byte[] ProtectedPrivateKey { get; private set; } = [];

    public SigningKeyStatus Status { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset? RetiredAtUtc { get; private set; }

    public DateTimeOffset? RevokedAtUtc { get; private set; }

    public void Retire(DateTimeOffset nowUtc)
    {
        if (Status == SigningKeyStatus.Revoked)
        {
            throw new DomainException("A revoked signing key cannot be retired.");
        }

        if (Status == SigningKeyStatus.Retired)
        {
            return;
        }

        Status = SigningKeyStatus.Retired;
        RetiredAtUtc = Guard.Utc(nowUtc, nameof(nowUtc));
        Touch();
    }

    public void Revoke(DateTimeOffset nowUtc)
    {
        if (Status == SigningKeyStatus.Revoked)
        {
            return;
        }

        Status = SigningKeyStatus.Revoked;
        RevokedAtUtc = Guard.Utc(nowUtc, nameof(nowUtc));
        Touch();
    }
}
