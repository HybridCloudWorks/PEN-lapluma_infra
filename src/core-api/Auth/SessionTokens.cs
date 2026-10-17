using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace LaPluma.CoreApi.Auth;

/// <summary>
/// The RSA key the core API signs LaPluma session tokens with.
///
/// The private key arrives through configuration — Secret Manager in a deployment, injected as
/// <c>SessionTokens__SigningKeyPem</c> — and never leaves this process. Only the public half is
/// published, at <c>/.well-known/jwks.json</c>, for API Gateway and the workflow API to validate
/// against.
///
/// An absent key is a supported state: nothing can be issued and nothing validates, so every
/// protected route fails closed. A key that is present but unusable stops the host at startup
/// instead, so a bad secret is a failed deployment rather than a quiet outage.
/// </summary>
public sealed class SessionTokenKeys
{
    public const string SigningKeySetting = "SessionTokens:SigningKeyPem";
    public const int MinimumKeySizeBits = 2048;

    private static readonly byte[] DerivationSalt = Encoding.UTF8.GetBytes("lapluma session signing key derivation v1");

    // HKDF pseudorandom key extracted from the private key, so purpose-specific symmetric keys
    // can be derived without keeping a second copy of the key material around.
    private readonly byte[]? _derivationKey;

    private SessionTokenKeys(RSA? privateKey)
    {
        if (privateKey is null)
        {
            ValidationKeys = [];
            PublicKeySet = new JsonWebKeySetDocument([]);
            return;
        }

        var pkcs8 = privateKey.ExportPkcs8PrivateKey();
        try
        {
            _derivationKey = HKDF.Extract(HashAlgorithmName.SHA256, pkcs8, DerivationSalt);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs8);
        }

        var publicParameters = privateKey.ExportParameters(includePrivateParameters: false);
        var keyId = ComputeKeyId(publicParameters);

        SigningCredentials = new SigningCredentials(
            new RsaSecurityKey(privateKey) { KeyId = keyId }, SecurityAlgorithms.RsaSha256);
        ValidationKeys = [new RsaSecurityKey(RSA.Create(publicParameters)) { KeyId = keyId }];
        PublicKeySet = new JsonWebKeySetDocument(
        [
            new PublicJsonWebKey(
                Kty: "RSA",
                Use: "sig",
                Alg: SecurityAlgorithms.RsaSha256,
                Kid: keyId,
                N: Base64UrlEncoder.Encode(publicParameters.Modulus),
                E: Base64UrlEncoder.Encode(publicParameters.Exponent)),
        ]);
    }

    /// <summary>Null when no signing key is configured.</summary>
    public SigningCredentials? SigningCredentials { get; }

    /// <summary>Public keys only. Empty when no signing key is configured, so nothing validates.</summary>
    public IReadOnlyList<SecurityKey> ValidationKeys { get; }

    /// <summary>The document served at <c>/.well-known/jwks.json</c>. Never carries private parameters.</summary>
    public JsonWebKeySetDocument PublicKeySet { get; }

    /// <summary>
    /// A 256-bit key for one <paramref name="purpose"/>, derived from the signing key with HKDF-SHA256
    /// (RFC 5869). Every instance holding the same signing key derives the same value, which is what
    /// lets the sign-in flow stay stateless across instances; distinct purposes give independent
    /// keys. Null when no signing key is configured.
    /// </summary>
    public byte[]? DeriveKey(string purpose) =>
        _derivationKey is null
            ? null
            : HKDF.Expand(HashAlgorithmName.SHA256, _derivationKey, 32, Encoding.UTF8.GetBytes(purpose));

    public static SessionTokenKeys FromConfiguration(IConfiguration configuration) =>
        FromPem(configuration[SigningKeySetting]);

    public static SessionTokenKeys FromPem(string? pem)
    {
        if (string.IsNullOrWhiteSpace(pem))
        {
            return new SessionTokenKeys(null);
        }

        var rsa = RSA.Create();
        try
        {
            rsa.ImportFromPem(pem);
            // ImportFromPem accepts a public key just as readily; signing would then fail on the
            // first sign-in instead of here.
            _ = rsa.ExportParameters(includePrivateParameters: true);
        }
        catch (Exception error) when (error is ArgumentException or CryptographicException)
        {
            rsa.Dispose();
            throw new InvalidOperationException(
                $"{SigningKeySetting} must be an unencrypted PEM-encoded RSA private key (PKCS#8 or PKCS#1).",
                error);
        }

        if (rsa.KeySize < MinimumKeySizeBits)
        {
            var size = rsa.KeySize;
            rsa.Dispose();
            throw new InvalidOperationException(
                $"{SigningKeySetting} is a {size}-bit RSA key; at least {MinimumKeySizeBits} bits are required.");
        }

        return new SessionTokenKeys(rsa);
    }

    /// <summary>RFC 7638 JWK thumbprint, so the key ID is derived from the key and cannot drift from it.</summary>
    private static string ComputeKeyId(RSAParameters publicParameters)
    {
        var canonical =
            $"{{\"e\":\"{Base64UrlEncoder.Encode(publicParameters.Exponent)}\",\"kty\":\"RSA\",\"n\":\"{Base64UrlEncoder.Encode(publicParameters.Modulus)}\"}}";
        return Base64UrlEncoder.Encode(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}

/// <summary>An RFC 7517 JWK Set holding public keys only.</summary>
public sealed record JsonWebKeySetDocument(
    [property: JsonPropertyName("keys")] IReadOnlyList<PublicJsonWebKey> Keys);

/// <summary>
/// A public RSA JWK. Declared field by field rather than serialising the IdentityModel type, so a
/// private parameter cannot reach the published document by accident.
/// </summary>
public sealed record PublicJsonWebKey(
    [property: JsonPropertyName("kty")] string Kty,
    [property: JsonPropertyName("use")] string Use,
    [property: JsonPropertyName("alg")] string Alg,
    [property: JsonPropertyName("kid")] string Kid,
    [property: JsonPropertyName("n")] string N,
    [property: JsonPropertyName("e")] string E);

/// <summary>Who a session token is for: what a verified assertion says, carried through the sign-in code.</summary>
public sealed record SessionIdentity(string Subject, string Email, string TenantId, IReadOnlyList<string> Roles)
{
    public static SessionIdentity From(SamlAssertion assertion) =>
        new(assertion.SubjectNameId, assertion.Email, assertion.TenantId, assertion.Roles);
}

/// <summary>
/// Mints the LaPluma session token that <c>POST /auth/token</c> returns for a redeemed sign-in
/// code: an RS256 JWT with the configured issuer and audience, a one-hour lifetime, and the
/// caller's tenant, email, and roles. Both APIs validate it with the standard JWT bearer handler.
/// </summary>
public sealed class SessionTokenIssuer(SessionTokenKeys keys, IConfiguration configuration, TimeProvider clock)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    public const string TenantClaim = "tenant_id";
    public const string RoleClaim = "role";

    private readonly string? issuer = configuration[CatalogAuthentication.IssuerSetting];
    private readonly string? audience = configuration[CatalogAuthentication.AudienceSetting];
    private readonly JsonWebTokenHandler handler = new() { SetDefaultTimesOnTokenCreation = false };

    /// <summary>False until a signing key, an issuer, and an audience are all configured.</summary>
    public bool CanIssue =>
        keys.SigningCredentials is not null &&
        !string.IsNullOrWhiteSpace(issuer) &&
        !string.IsNullOrWhiteSpace(audience);

    public SamlTokenExchangeResponse Issue(SamlAssertion assertion) => Issue(SessionIdentity.From(assertion));

    public SamlTokenExchangeResponse Issue(SessionIdentity identity)
    {
        if (!CanIssue)
        {
            throw new InvalidOperationException("Session token signing is not configured.");
        }

        var now = clock.GetUtcNow();
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = now.Add(Lifetime).UtcDateTime,
            SigningCredentials = keys.SigningCredentials,
            Claims = new Dictionary<string, object>
            {
                [JwtRegisteredClaimNames.Sub] = identity.Subject,
                [JwtRegisteredClaimNames.Email] = identity.Email,
                [JwtRegisteredClaimNames.Jti] = Guid.NewGuid().ToString("N"),
                [TenantClaim] = identity.TenantId,
                [RoleClaim] = identity.Roles.ToArray(),
            },
        };

        return new SamlTokenExchangeResponse(
            AccessToken: handler.CreateToken(descriptor),
            TokenType: "Bearer",
            ExpiresIn: (int)Lifetime.TotalSeconds,
            TenantId: identity.TenantId,
            UserId: identity.Subject,
            Roles: identity.Roles,
            Email: identity.Email);
    }
}

/// <summary>Validation shared by every route that accepts a LaPluma session token.</summary>
public static class SessionTokenValidation
{
    /// <summary>
    /// API Gateway authenticates to Cloud Run with its own Google-signed ID token in
    /// <c>Authorization</c> and moves the caller's header here. That ID token only proves the
    /// gateway may invoke the service — Cloud Run IAM has already checked it — so it is never
    /// treated as a user identity.
    /// </summary>
    public const string ForwardedAuthorizationHeader = "X-Forwarded-Authorization";

    public static TokenValidationParameters CreateParameters(
        string? issuer, string? audience, IEnumerable<SecurityKey> signingKeys) => new()
    {
        ValidateIssuer = true,
        ValidIssuer = issuer,
        ValidateAudience = true,
        ValidAudience = audience,
        ValidateLifetime = true,
        RequireExpirationTime = true,
        RequireSignedTokens = true,
        ValidateIssuerSigningKey = true,
        IssuerSigningKeys = signingKeys.ToArray(),
        ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
        ClockSkew = TimeSpan.FromSeconds(30),
    };

    /// <summary>
    /// Chooses which header carries the caller's token. It only selects; the JWT bearer handler
    /// still validates whatever is selected in full, so a forwarded header confers nothing a
    /// direct one would not.
    /// </summary>
    public static Task SelectBearerToken(MessageReceivedContext context)
    {
        var forwarded = context.Request.Headers[ForwardedAuthorizationHeader].ToString();
        if (forwarded.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            context.Token = forwarded["Bearer ".Length..].Trim();
        }

        return Task.CompletedTask;
    }
}
