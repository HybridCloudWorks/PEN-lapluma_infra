namespace LaPluma.CoreApi.Auth;

public enum SamlIdpType
{
    GoogleWorkspace,
    EntraIdSaml
}

public sealed record InstitutionIdpConfiguration(
    string TenantId,
    SamlIdpType IdpType,
    string EntityId,
    string SsoServiceUrl,
    string IdpX509Certificate,
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

public sealed record SamlTokenExchangeResponse(
    string AccessToken,
    string TokenType,
    int ExpiresIn,
    string TenantId,
    string UserId,
    IReadOnlyList<string> Roles
);
