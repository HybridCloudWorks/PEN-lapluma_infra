using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace LaPluma.CoreApi.Auth;

public sealed class SamlServiceProvider : ISamlServiceProvider
{
    private static readonly HashSet<string> DisallowedConsumerDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "gmail.com", "googlemail.com", "outlook.com", "hotmail.com",
        "live.com", "msn.com", "yahoo.com", "icloud.com", "aol.com"
    };

    private readonly string _spEntityId;
    private readonly string _spAcsBaseUrl;
    private readonly List<InstitutionIdpConfiguration> _idpConfigs = new();

    public SamlServiceProvider(string spEntityId, string spAcsBaseUrl, IEnumerable<InstitutionIdpConfiguration>? initialConfigs = null)
    {
        _spEntityId = string.IsNullOrWhiteSpace(spEntityId) ? "https://api.lapluma.app/auth/saml/metadata" : spEntityId;
        _spAcsBaseUrl = string.IsNullOrWhiteSpace(spAcsBaseUrl) ? "https://api.lapluma.app/auth/saml/acs" : spAcsBaseUrl;

        if (initialConfigs != null)
        {
            _idpConfigs.AddRange(initialConfigs);
        }
        else
        {
            // Default configured enterprise tenant for HybridCloudWorks
            _idpConfigs.Add(new InstitutionIdpConfiguration(
                TenantId: "tenant_hybridcloudworks",
                IdpType: SamlIdpType.GoogleWorkspace,
                EntityId: "https://accounts.google.com/o/saml2?idpid=C03hcwks",
                SsoServiceUrl: "https://accounts.google.com/o/saml2/idp?idpid=C03hcwks",
                IdpX509Certificate: "MOCK_GOOGLE_WORKSPACE_CERT",
                AllowedDomains: new[] { "hybridcloudworks.com" },
                AttributeMappings: new Dictionary<string, string>
                {
                    ["email"] = "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress",
                    ["role"] = "http://schemas.microsoft.com/ws/2008/06/identity/claims/role"
                },
                AdminConsentVerified: true,
                IsActive: true
            ));

            _idpConfigs.Add(new InstitutionIdpConfiguration(
                TenantId: "tenant_hybridcloudworks",
                IdpType: SamlIdpType.EntraIdSaml,
                EntityId: "https://sts.windows.net/4a2b9a76-2e4b-47e1-872f-5b1234567890/",
                SsoServiceUrl: "https://login.microsoftonline.com/4a2b9a76-2e4b-47e1-872f-5b1234567890/saml2",
                IdpX509Certificate: "MOCK_MICROSOFT_ENTRA_ID_CERT",
                AllowedDomains: new[] { "hybridcloudworks.com" },
                AttributeMappings: new Dictionary<string, string>
                {
                    ["email"] = "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress",
                    ["role"] = "http://schemas.microsoft.com/ws/2008/06/identity/claims/role"
                },
                AdminConsentVerified: true,
                IsActive: true
            ));
        }
    }

    public string GenerateSpMetadataXml(string spEntityId, string acsUrl)
    {
        var entity = string.IsNullOrWhiteSpace(spEntityId) ? _spEntityId : spEntityId;
        var acs = string.IsNullOrWhiteSpace(acsUrl) ? _spAcsBaseUrl : acsUrl;

        return $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <md:EntityDescriptor xmlns:md="urn:oasis:names:tc:SAML:2.0:metadata" entityID="{entity}">
            <md:SPSSODescriptor AuthnRequestsSigned="false" WantAssertionsSigned="true" protocolSupportEnumeration="urn:oasis:names:tc:SAML:2.0:protocol">
                <md:NameIDFormat>urn:oasis:names:tc:SAML:1.1:nameid-format:emailAddress</md:NameIDFormat>
                <md:AssertionConsumerService Binding="urn:oasis:names:tc:SAML:2.0:bindings:HTTP-POST" Location="{acs}" index="0" isDefault="true"/>
            </md:SPSSODescriptor>
        </md:EntityDescriptor>
        """;
    }

    public Task<SamlAuthnRequest> CreateAuthnRequestAsync(
        string domain,
        SamlIdpType? preferredIdp = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(domain))
        {
            throw new ArgumentException("Domain is required", nameof(domain));
        }

        var normalizedDomain = domain.Trim().ToLowerInvariant();
        if (DisallowedConsumerDomains.Contains(normalizedDomain))
        {
            throw new InvalidOperationException($"Personal domain '@{normalizedDomain}' is not permitted. Only verified institutional Google Workspace or Microsoft Entra ID accounts are allowed.");
        }

        var config = _idpConfigs.FirstOrDefault(c =>
            c.IsActive &&
            c.AllowedDomains.Any(d => d.Equals(normalizedDomain, StringComparison.OrdinalIgnoreCase)) &&
            (!preferredIdp.HasValue || c.IdpType == preferredIdp.Value));

        if (config == null)
        {
            throw new InvalidOperationException($"No active enterprise IdP configured for domain '{normalizedDomain}'.");
        }

        var id = "_" + Guid.NewGuid().ToString("N");
        var issueInstant = DateTimeOffset.UtcNow;
        var acsUrl = $"{_spAcsBaseUrl}/{config.TenantId}";

        var requestXml = $"""
        <samlp:AuthnRequest xmlns:samlp="urn:oasis:names:tc:SAML:2.0:protocol"
            xmlns:saml="urn:oasis:names:tc:SAML:2.0:assertion"
            ID="{id}"
            Version="2.0"
            IssueInstant="{issueInstant:O}"
            Destination="{config.SsoServiceUrl}"
            AssertionConsumerServiceURL="{acsUrl}"
            ProtocolBinding="urn:oasis:names:tc:SAML:2.0:bindings:HTTP-POST">
            <saml:Issuer>{_spEntityId}</saml:Issuer>
            <samlp:NameIDPolicy Format="urn:oasis:names:tc:SAML:1.1:nameid-format:emailAddress" AllowCreate="true"/>
        </samlp:AuthnRequest>
        """;

        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(requestXml));
        var redirectUrl = $"{config.SsoServiceUrl}?SAMLRequest={Uri.EscapeDataString(base64)}";

        return Task.FromResult(new SamlAuthnRequest(
            Id: id,
            IssueInstant: issueInstant,
            Destination: config.SsoServiceUrl,
            Issuer: _spEntityId,
            AssertionConsumerServiceUrl: acsUrl,
            RedirectUrl: redirectUrl
        ));
    }

    public Task<SamlValidationResult> ValidateAssertionResponseAsync(
        string tenantId,
        string samlResponseXmlOrBase64,
        string expectedAudience,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(samlResponseXmlOrBase64))
        {
            return Task.FromResult(SamlValidationResult.Failure("empty-response", "SAML response is missing or empty."));
        }

        string xml;
        try
        {
            if (samlResponseXmlOrBase64.TrimStart().StartsWith('<'))
            {
                xml = samlResponseXmlOrBase64;
            }
            else
            {
                var bytes = Convert.FromBase64String(samlResponseXmlOrBase64.Trim());
                xml = Encoding.UTF8.GetString(bytes);
            }
        }
        catch (Exception ex)
        {
            return Task.FromResult(SamlValidationResult.Failure("invalid-encoding", $"Unable to decode SAML response: {ex.Message}"));
        }

        XDocument doc;
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null
            };
            using var reader = XmlReader.Create(new StringReader(xml), settings);
            doc = XDocument.Load(reader);
        }
        catch (Exception ex)
        {
            return Task.FromResult(SamlValidationResult.Failure("invalid-xml", $"Malformed XML SAML response: {ex.Message}"));
        }

        XNamespace samlp = "urn:oasis:names:tc:SAML:2.0:protocol";
        XNamespace saml = "urn:oasis:names:tc:SAML:2.0:assertion";
        XNamespace ds = "http://www.w3.org/2000/09/xmldsig#";

        // Check Status
        var statusCodeElem = doc.Descendants(samlp + "StatusCode").FirstOrDefault();
        var statusCode = statusCodeElem?.Attribute("Value")?.Value;
        if (statusCode != "urn:oasis:names:tc:SAML:2.0:status:Success")
        {
            var statusMessage = doc.Descendants(samlp + "StatusMessage").FirstOrDefault()?.Value ?? "Authentication failed at Identity Provider";
            return Task.FromResult(SamlValidationResult.Failure("idp-error", $"IdP returned error status: {statusCode}. Message: {statusMessage}"));
        }

        // Find Assertion
        var assertionElem = doc.Descendants(saml + "Assertion").FirstOrDefault();
        if (assertionElem == null)
        {
            return Task.FromResult(SamlValidationResult.Failure("missing-assertion", "SAML Response contains no Assertion element."));
        }

        // Check Issuer
        var issuer = assertionElem.Element(saml + "Issuer")?.Value ?? doc.Root?.Element(saml + "Issuer")?.Value;
        if (string.IsNullOrWhiteSpace(issuer))
        {
            return Task.FromResult(SamlValidationResult.Failure("missing-issuer", "SAML Assertion contains no Issuer."));
        }

        // Match IdP configuration for tenant
        var idpConfig = _idpConfigs.FirstOrDefault(c =>
            c.TenantId.Equals(tenantId, StringComparison.OrdinalIgnoreCase) &&
            c.EntityId.Equals(issuer, StringComparison.OrdinalIgnoreCase) &&
            c.IsActive);

        if (idpConfig == null)
        {
            return Task.FromResult(SamlValidationResult.Failure("idp-not-approved", $"Issuer '{issuer}' is not an approved Identity Provider for tenant '{tenantId}'."));
        }

        // Check Signature presence
        var signatureElem = assertionElem.Element(ds + "Signature") ?? doc.Root?.Element(ds + "Signature");
        if (signatureElem == null)
        {
            return Task.FromResult(SamlValidationResult.Failure("unsigned-assertion", "SAML Assertion or Response is not cryptographically signed."));
        }

        // Check Subject NameID (User identifier / Email)
        var subjectElem = assertionElem.Element(saml + "Subject");
        var nameId = subjectElem?.Element(saml + "NameID")?.Value;
        if (string.IsNullOrWhiteSpace(nameId))
        {
            return Task.FromResult(SamlValidationResult.Failure("missing-nameid", "Assertion Subject has no NameID."));
        }

        // Check Conditions
        var conditionsElem = assertionElem.Element(saml + "Conditions");
        var now = DateTimeOffset.UtcNow;
        var skew = TimeSpan.FromMinutes(5);

        if (conditionsElem != null)
        {
            var notBeforeStr = conditionsElem.Attribute("NotBefore")?.Value;
            if (DateTimeOffset.TryParse(notBeforeStr, out var notBefore) && now + skew < notBefore)
            {
                return Task.FromResult(SamlValidationResult.Failure("assertion-not-yet-valid", $"Assertion is not valid before {notBefore:O}."));
            }

            var notOnOrAfterStr = conditionsElem.Attribute("NotOnOrAfter")?.Value;
            if (DateTimeOffset.TryParse(notOnOrAfterStr, out var notOnOrAfter) && now - skew >= notOnOrAfter)
            {
                return Task.FromResult(SamlValidationResult.Failure("assertion-expired", $"Assertion expired at {notOnOrAfter:O}."));
            }

            var audienceRestriction = conditionsElem.Element(saml + "AudienceRestriction");
            if (audienceRestriction != null)
            {
                var audience = audienceRestriction.Element(saml + "Audience")?.Value;
                var targetAudience = string.IsNullOrWhiteSpace(expectedAudience) ? _spEntityId : expectedAudience;
                if (!string.IsNullOrWhiteSpace(audience) && !audience.Equals(targetAudience, StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(SamlValidationResult.Failure("audience-mismatch", $"Assertion audience '{audience}' does not match expected '{targetAudience}'."));
                }
            }
        }

        // Extract Attributes
        string email = nameId;
        string? givenName = null;
        string? surname = null;
        var roles = new List<string>();

        var attributeStatement = assertionElem.Element(saml + "AttributeStatement");
        if (attributeStatement != null)
        {
            foreach (var attr in attributeStatement.Elements(saml + "Attribute"))
            {
                var attrName = attr.Attribute("Name")?.Value;
                var attrValues = attr.Elements(saml + "AttributeValue").Select(v => v.Value).ToList();

                if (attrName == idpConfig.AttributeMappings.GetValueOrDefault("email", "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress") ||
                    attrName?.EndsWith("emailaddress", StringComparison.OrdinalIgnoreCase) == true)
                {
                    if (attrValues.Count > 0) email = attrValues[0];
                }
                else if (attrName == idpConfig.AttributeMappings.GetValueOrDefault("given_name", "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/givenname") ||
                         attrName?.EndsWith("givenname", StringComparison.OrdinalIgnoreCase) == true)
                {
                    if (attrValues.Count > 0) givenName = attrValues[0];
                }
                else if (attrName == idpConfig.AttributeMappings.GetValueOrDefault("surname", "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/surname") ||
                         attrName?.EndsWith("surname", StringComparison.OrdinalIgnoreCase) == true)
                {
                    if (attrValues.Count > 0) surname = attrValues[0];
                }
                else if (attrName == idpConfig.AttributeMappings.GetValueOrDefault("role", "http://schemas.microsoft.com/ws/2008/06/identity/claims/role") ||
                         attrName?.EndsWith("role", StringComparison.OrdinalIgnoreCase) == true ||
                         attrName?.EndsWith("groups", StringComparison.OrdinalIgnoreCase) == true)
                {
                    roles.AddRange(attrValues);
                }
            }
        }

        // Validate domain
        var emailParts = email.Split('@');
        if (emailParts.Length != 2)
        {
            return Task.FromResult(SamlValidationResult.Failure("invalid-email", $"Subject '{email}' is not a valid email address."));
        }

        var domain = emailParts[1].ToLowerInvariant();
        if (DisallowedConsumerDomains.Contains(domain))
        {
            return Task.FromResult(SamlValidationResult.Failure("consumer-domain-prohibited", $"Personal email domains like '@{domain}' are forbidden. Authentication must use institutional Google Workspace or Microsoft Entra ID accounts."));
        }

        if (!idpConfig.AllowedDomains.Any(d => d.Equals(domain, StringComparison.OrdinalIgnoreCase)))
        {
            return Task.FromResult(SamlValidationResult.Failure("domain-not-authorized", $"Domain '{domain}' is not authorized for tenant '{tenantId}'."));
        }

        if (roles.Count == 0)
        {
            roles.Add("preparer");
        }

        var assertion = new SamlAssertion(
            Issuer: issuer,
            SubjectNameId: nameId,
            TenantId: tenantId,
            Email: email,
            GivenName: givenName,
            Surname: surname,
            Roles: roles,
            IssueInstant: DateTimeOffset.UtcNow,
            NotBefore: now - skew,
            NotOnOrAfter: now + TimeSpan.FromHours(1),
            Audience: expectedAudience
        );

        return Task.FromResult(SamlValidationResult.Success(assertion));
    }

    public Task<SamlTokenExchangeResponse> ExchangeAssertionForTokenAsync(
        SamlAssertion assertion,
        CancellationToken ct = default)
    {
        var token = "lp_saml_" + Convert.ToBase64String(Encoding.UTF8.GetBytes(
            $"{assertion.TenantId}:{assertion.Email}:{string.Join(",", assertion.Roles)}:{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}"));

        return Task.FromResult(new SamlTokenExchangeResponse(
            AccessToken: token,
            TokenType: "Bearer",
            ExpiresIn: 3600,
            TenantId: assertion.TenantId,
            UserId: assertion.SubjectNameId,
            Roles: assertion.Roles
        ));
    }

    public Task<InstitutionIdpConfiguration?> GetIdpConfigurationByDomainAsync(string domain, CancellationToken ct = default)
    {
        var norm = domain.Trim().ToLowerInvariant();
        var config = _idpConfigs.FirstOrDefault(c => c.IsActive && c.AllowedDomains.Any(d => d.Equals(norm, StringComparison.OrdinalIgnoreCase)));
        return Task.FromResult<InstitutionIdpConfiguration?>(config);
    }

    public Task<InstitutionIdpConfiguration?> GetIdpConfigurationByTenantAsync(string tenantId, SamlIdpType idpType, CancellationToken ct = default)
    {
        var config = _idpConfigs.FirstOrDefault(c => c.TenantId.Equals(tenantId, StringComparison.OrdinalIgnoreCase) && c.IdpType == idpType && c.IsActive);
        return Task.FromResult<InstitutionIdpConfiguration?>(config);
    }
}
