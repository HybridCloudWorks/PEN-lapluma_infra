namespace LaPluma.CoreApi.Auth;

public interface ISamlServiceProvider
{
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

    Task<SamlTokenExchangeResponse> ExchangeAssertionForTokenAsync(
        SamlAssertion assertion,
        CancellationToken ct = default);

    Task<InstitutionIdpConfiguration?> GetIdpConfigurationByDomainAsync(
        string domain,
        CancellationToken ct = default);

    Task<InstitutionIdpConfiguration?> GetIdpConfigurationByTenantAsync(
        string tenantId,
        SamlIdpType idpType,
        CancellationToken ct = default);
}
