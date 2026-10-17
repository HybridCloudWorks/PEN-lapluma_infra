namespace LaPluma.CoreApi.Auth;

public interface ISamlServiceProvider
{
    /// <summary>This service provider's entity ID, which every accepted assertion's Audience must equal.</summary>
    string SpEntityId { get; }

    /// <summary>Base of the per-tenant assertion consumer URLs, which every accepted assertion's Recipient must equal.</summary>
    string SpAcsBaseUrl { get; }

    string GenerateSpMetadataXml(string spEntityId, string acsUrl);

    Task<SamlAuthnRequest> CreateAuthnRequestAsync(
        string domain,
        SamlIdpType? preferredIdp = null,
        CancellationToken ct = default);

    /// <summary>
    /// Builds the AuthnRequest for <paramref name="domain"/> with the ID and RelayState that
    /// <paramref name="bind"/> returns for the resolved tenant, so the request and the state it
    /// carries are tied to one sign-in.
    /// </summary>
    Task<SamlAuthnRequest> CreateAuthnRequestAsync(
        string domain,
        SamlIdpType? preferredIdp,
        Func<string, SamlRequestBinding> bind,
        CancellationToken ct = default);

    /// <summary>
    /// Validates a SAML response. When <paramref name="expectedInResponseTo"/> is given, the bearer
    /// confirmation must also name it as InResponseTo, so only an answer to that AuthnRequest is
    /// accepted.
    /// </summary>
    Task<SamlValidationResult> ValidateAssertionResponseAsync(
        string tenantId,
        string samlResponseXmlOrBase64,
        string expectedAudience,
        string? expectedInResponseTo = null,
        CancellationToken ct = default);

    Task<InstitutionIdpConfiguration?> GetIdpConfigurationByDomainAsync(
        string domain,
        CancellationToken ct = default);

    Task<InstitutionIdpConfiguration?> GetIdpConfigurationByTenantAsync(
        string tenantId,
        SamlIdpType idpType,
        CancellationToken ct = default);
}
