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

    Task<SamlValidationResult> ValidateAssertionResponseAsync(
        string tenantId,
        string samlResponseXmlOrBase64,
        string expectedAudience,
        CancellationToken ct = default);

    Task<InstitutionIdpConfiguration?> GetIdpConfigurationByDomainAsync(
        string domain,
        CancellationToken ct = default);

    Task<InstitutionIdpConfiguration?> GetIdpConfigurationByTenantAsync(
        string tenantId,
        SamlIdpType idpType,
        CancellationToken ct = default);
}
