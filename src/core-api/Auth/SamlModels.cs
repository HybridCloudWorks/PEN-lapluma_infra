namespace LaPluma.CoreApi.Auth;

public enum SamlIdpType
{
    GoogleWorkspace,
    EntraIdSaml
}

/// <summary>
/// One institution's identity provider. <paramref name="SigningCertificates"/> holds the IdP's
/// X.509 signing certificates (PEM, or the bare Base64 DER found in IdP metadata); more than one is
/// allowed so a certificate can be rolled over without an outage.
/// </summary>
public sealed record InstitutionIdpConfiguration(
    string TenantId,
    SamlIdpType IdpType,
    string EntityId,
    string SsoServiceUrl,
    IReadOnlyList<string> SigningCertificates,
    IReadOnlyList<string> AllowedDomains,
    IReadOnlyDictionary<string, string> AttributeMappings,
    bool AdminConsentVerified,
    bool IsActive
);

public sealed record SamlAuthnRequest(
    string Id,
    DateTimeOffset IssueInstant,
    string Destination,
    string Issuer,
    string AssertionConsumerServiceUrl,
    string RedirectUrl
);

public sealed record SamlAssertion(
    string Issuer,
    string SubjectNameId,
    string TenantId,
    string Email,
    string? GivenName,
    string? Surname,
    IReadOnlyList<string> Roles,
    DateTimeOffset IssueInstant,
    DateTimeOffset NotBefore,
    DateTimeOffset NotOnOrAfter,
    string Audience
);

public sealed record SamlValidationResult(
    bool IsValid,
    SamlAssertion? Assertion,
    string? ErrorCode,
    string? ErrorMessage
)
{
    public static SamlValidationResult Success(SamlAssertion assertion) =>
        new(true, assertion, null, null);

    public static SamlValidationResult Failure(string errorCode, string errorMessage) =>
        new(false, null, errorCode, errorMessage);
}

/// <summary>The body of a successful <c>POST /auth/token</c>. Served with <c>Cache-Control: no-store</c>.</summary>
public sealed record SamlTokenExchangeResponse(
    string AccessToken,
    string TokenType,
    int ExpiresIn,
    string TenantId,
    string UserId,
    IReadOnlyList<string> Roles,
    string Email
);
