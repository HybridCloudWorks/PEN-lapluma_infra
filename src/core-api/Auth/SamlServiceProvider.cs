using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using Microsoft.AspNetCore.WebUtilities;

namespace LaPluma.CoreApi.Auth;

/// <summary>
/// SAML 2.0 service provider for institutional Google Workspace and Microsoft Entra ID sign-in.
///
/// An assertion is accepted only when all of these hold:
/// <list type="bullet">
/// <item>the response holds exactly one Assertion, a direct child of the Response, whose ID is
/// unique in the document;</item>
/// <item>that Assertion carries an enveloped XML signature whose single Reference points at that
/// same element, using RSA with SHA-256 or stronger (SHA-1 is refused), and the signature verifies
/// against a certificate pinned in configuration for the tenant's identity provider — never
/// against a key the document supplies;</item>
/// <item>its Conditions are current (NotBefore and NotOnOrAfter, with two minutes of skew), its
/// Audience is this service provider, and a bearer SubjectConfirmation names the tenant's
/// assertion consumer URL as Recipient;</item>
/// <item>it has not been presented before to this instance.</item>
/// </list>
/// Everything is read from the verified Assertion element itself, so nothing outside the signed
/// bytes influences the identity that results.
///
/// Identity providers come from <see cref="IdentityProvidersSetting"/> (a JSON array; Secret
/// Manager in a deployment). With none configured, every sign-in fails closed.
/// </summary>
public sealed class SamlServiceProvider : ISamlServiceProvider
{
    public const string SpEntityIdSetting = "Saml:SpEntityId";
    public const string SpAcsBaseUrlSetting = "Saml:SpAcsBaseUrl";
    public const string IdentityProvidersSetting = "Saml:IdentityProvidersJson";

    public const string DefaultSpEntityId = "https://api.lapluma.app/auth/saml/metadata";
    public const string DefaultSpAcsBaseUrl = "https://api.lapluma.app/auth/saml/acs";

    private const string ProtocolNamespace = "urn:oasis:names:tc:SAML:2.0:protocol";
    private const string AssertionNamespace = "urn:oasis:names:tc:SAML:2.0:assertion";
    private const string SuccessStatus = "urn:oasis:names:tc:SAML:2.0:status:Success";
    private const string BearerMethod = "urn:oasis:names:tc:SAML:2.0:cm:bearer";

    // Base64 inflates by a third; a real Google or Entra response is a few kilobytes.
    private const int MaxResponseCharacters = 256 * 1024;
    private const int MaxEncodedCharacters = MaxResponseCharacters * 4 / 3 + 4;
    private const int ReplayCachePruneThreshold = 1024;

    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(2);

    private static readonly HashSet<string> DisallowedConsumerDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "gmail.com", "googlemail.com", "outlook.com", "hotmail.com",
        "live.com", "msn.com", "yahoo.com", "icloud.com", "aol.com"
    };

    private static readonly HashSet<string> AllowedSignatureMethods = new(StringComparer.Ordinal)
    {
        SignedXml.XmlDsigRSASHA256Url, SignedXml.XmlDsigRSASHA384Url, SignedXml.XmlDsigRSASHA512Url,
    };

    private static readonly HashSet<string> AllowedDigestMethods = new(StringComparer.Ordinal)
    {
        SignedXml.XmlDsigSHA256Url, SignedXml.XmlDsigSHA384Url, SignedXml.XmlDsigSHA512Url,
    };

    private static readonly HashSet<string> AllowedCanonicalizationMethods = new(StringComparer.Ordinal)
    {
        SignedXml.XmlDsigExcC14NTransformUrl, SignedXml.XmlDsigC14NTransformUrl,
    };

    private static readonly HashSet<string> AllowedTransforms = new(StringComparer.Ordinal)
    {
        SignedXml.XmlDsigEnvelopedSignatureTransformUrl,
        SignedXml.XmlDsigExcC14NTransformUrl,
        SignedXml.XmlDsigC14NTransformUrl,
    };

    private static readonly JsonSerializerOptions ConfigurationJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly List<RegisteredIdentityProvider> _identityProviders = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _consumedAssertions = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;

    public SamlServiceProvider(
        string spEntityId,
        string spAcsBaseUrl,
        IEnumerable<InstitutionIdpConfiguration> identityProviders,
        TimeProvider? clock = null)
    {
        SpEntityId = string.IsNullOrWhiteSpace(spEntityId) ? DefaultSpEntityId : spEntityId;
        SpAcsBaseUrl = (string.IsNullOrWhiteSpace(spAcsBaseUrl) ? DefaultSpAcsBaseUrl : spAcsBaseUrl).TrimEnd('/');
        _clock = clock ?? TimeProvider.System;

        var index = 0;
        foreach (var configuration in identityProviders)
        {
            _identityProviders.Add(Register(configuration, index++));
        }
    }

    public string SpEntityId { get; }

    public string SpAcsBaseUrl { get; }

    /// <summary>
    /// Builds the provider from configuration. Throws when an identity provider entry is unusable —
    /// a certificate that does not parse, a consumer domain, a non-HTTPS SSO URL — so a bad secret
    /// stops the host at startup rather than surfacing as failed sign-ins.
    /// </summary>
    public static SamlServiceProvider FromConfiguration(IConfiguration configuration, TimeProvider? clock = null)
    {
        var json = configuration[IdentityProvidersSetting];
        IReadOnlyList<InstitutionIdpConfiguration> identityProviders = [];
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                identityProviders = JsonSerializer.Deserialize<List<InstitutionIdpConfiguration>>(json, ConfigurationJson)
                    ?? throw new InvalidOperationException($"{IdentityProvidersSetting} must be a JSON array.");
            }
            catch (JsonException error)
            {
                throw new InvalidOperationException($"{IdentityProvidersSetting} is not a valid JSON array of identity providers.", error);
            }
        }

        return new SamlServiceProvider(
            configuration[SpEntityIdSetting] ?? DefaultSpEntityId,
            configuration[SpAcsBaseUrlSetting] ?? DefaultSpAcsBaseUrl,
            identityProviders,
            clock);
    }

    public string GenerateSpMetadataXml(string spEntityId, string acsUrl)
    {
        var entity = string.IsNullOrWhiteSpace(spEntityId) ? SpEntityId : spEntityId;
        var acs = string.IsNullOrWhiteSpace(acsUrl) ? SpAcsBaseUrl : acsUrl;

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

        var config = _identityProviders.Select(p => p.Configuration).FirstOrDefault(c =>
            c.IsActive &&
            c.AllowedDomains.Any(d => d.Equals(normalizedDomain, StringComparison.OrdinalIgnoreCase)) &&
            (!preferredIdp.HasValue || c.IdpType == preferredIdp.Value));

        if (config == null)
        {
            throw new InvalidOperationException($"No active enterprise IdP configured for domain '{normalizedDomain}'.");
        }

        var id = "_" + Guid.NewGuid().ToString("N");
        var issueInstant = _clock.GetUtcNow();
        var acsUrl = AssertionConsumerUrl(config.TenantId);

        var requestXml = AuthnRequestXml(id, issueInstant, config.SsoServiceUrl, acsUrl);
        var redirectUrl = RedirectBindingUrl(config.SsoServiceUrl, requestXml);

        return Task.FromResult(new SamlAuthnRequest(
            Id: id,
            IssueInstant: issueInstant,
            Destination: config.SsoServiceUrl,
            Issuer: SpEntityId,
            AssertionConsumerServiceUrl: acsUrl,
            RedirectUrl: redirectUrl
        ));
    }

    public Task<SamlValidationResult> ValidateAssertionResponseAsync(
        string tenantId,
        string samlResponseXmlOrBase64,
        string expectedAudience,
        CancellationToken ct = default) =>
        Task.FromResult(Validate(
            tenantId,
            samlResponseXmlOrBase64,
            string.IsNullOrWhiteSpace(expectedAudience) ? SpEntityId : expectedAudience));

    public Task<InstitutionIdpConfiguration?> GetIdpConfigurationByDomainAsync(string domain, CancellationToken ct = default)
    {
        var norm = domain.Trim().ToLowerInvariant();
        var config = _identityProviders.Select(p => p.Configuration)
            .FirstOrDefault(c => c.IsActive && c.AllowedDomains.Any(d => d.Equals(norm, StringComparison.OrdinalIgnoreCase)));
        return Task.FromResult(config);
    }

    public Task<InstitutionIdpConfiguration?> GetIdpConfigurationByTenantAsync(string tenantId, SamlIdpType idpType, CancellationToken ct = default)
    {
        var config = _identityProviders.Select(p => p.Configuration)
            .FirstOrDefault(c => c.TenantId.Equals(tenantId, StringComparison.OrdinalIgnoreCase) && c.IdpType == idpType && c.IsActive);
        return Task.FromResult(config);
    }

    private string AssertionConsumerUrl(string tenantId) => $"{SpAcsBaseUrl}/{tenantId}";

    /// <summary>
    /// The AuthnRequest, written through <see cref="XmlWriter"/> so a URL carrying <c>&amp;</c>
    /// (an SSO URL with more than one query parameter) is escaped rather than breaking the XML.
    /// IssueInstant is UTC with a <c>Z</c> designator, as SAML core section 1.3.3 requires.
    /// </summary>
    private string AuthnRequestXml(string id, DateTimeOffset issueInstant, string destination, string acsUrl)
    {
        var xml = new StringBuilder();
        var settings = new XmlWriterSettings { OmitXmlDeclaration = true, Indent = false };
        using (var writer = XmlWriter.Create(xml, settings))
        {
            writer.WriteStartElement("samlp", "AuthnRequest", ProtocolNamespace);
            writer.WriteAttributeString("xmlns", "saml", null, AssertionNamespace);
            writer.WriteAttributeString("ID", id);
            writer.WriteAttributeString("Version", "2.0");
            writer.WriteAttributeString("IssueInstant", issueInstant.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
            writer.WriteAttributeString("Destination", destination);
            writer.WriteAttributeString("AssertionConsumerServiceURL", acsUrl);
            writer.WriteAttributeString("ProtocolBinding", "urn:oasis:names:tc:SAML:2.0:bindings:HTTP-POST");
            writer.WriteElementString("saml", "Issuer", AssertionNamespace, SpEntityId);
            writer.WriteStartElement("samlp", "NameIDPolicy", ProtocolNamespace);
            writer.WriteAttributeString("Format", "urn:oasis:names:tc:SAML:1.1:nameid-format:emailAddress");
            writer.WriteAttributeString("AllowCreate", "true");
            writer.WriteEndElement();
            writer.WriteEndElement();
        }

        return xml.ToString();
    }

    /// <summary>
    /// The HTTP-Redirect binding (SAML bindings section 3.4.4.1): the request is DEFLATE-compressed
    /// as raw RFC 1951 data with no zlib header (<see cref="DeflateStream"/>, not
    /// <see cref="ZLibStream"/>), then Base64-encoded, then URL-encoded as the SAMLRequest
    /// parameter. The parameter is added to whatever query the SSO URL already carries; a Google
    /// Workspace URL already holds <c>?idpid=</c>, so it must be joined with <c>&amp;</c>. No
    /// RelayState is sent; if one is added it goes in its own parameter, after SAMLRequest.
    /// </summary>
    private static string RedirectBindingUrl(string ssoServiceUrl, string requestXml)
    {
        using var compressed = new MemoryStream();
        using (var deflate = new DeflateStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(Encoding.UTF8.GetBytes(requestXml));
        }

        var samlRequest = Convert.ToBase64String(compressed.ToArray());
        return QueryHelpers.AddQueryString(ssoServiceUrl, "SAMLRequest", samlRequest);
    }

    private SamlValidationResult Validate(string tenantId, string input, string expectedAudience)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return SamlValidationResult.Failure("empty-response", "SAML response is missing or empty.");
        }

        if (input.Length > MaxEncodedCharacters)
        {
            return SamlValidationResult.Failure("response-too-large", "SAML response exceeds the accepted size.");
        }

        string xml;
        var trimmed = input.Trim();
        if (trimmed.StartsWith('<'))
        {
            xml = trimmed;
        }
        else
        {
            try
            {
                xml = Encoding.UTF8.GetString(Convert.FromBase64String(trimmed));
            }
            catch (FormatException)
            {
                return SamlValidationResult.Failure("invalid-encoding", "SAML response is not valid Base64.");
            }
        }

        XmlDocument document;
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaxResponseCharacters,
            };
            using var reader = XmlReader.Create(new StringReader(xml), settings);
            // PreserveWhitespace: the signature covers the bytes as sent, whitespace included.
            document = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
            document.Load(reader);
        }
        catch (XmlException)
        {
            return SamlValidationResult.Failure("invalid-xml", "SAML response is not well-formed XML.");
        }

        var ns = new XmlNamespaceManager(document.NameTable);
        ns.AddNamespace("samlp", ProtocolNamespace);
        ns.AddNamespace("saml", AssertionNamespace);
        ns.AddNamespace("ds", SignedXml.XmlDsigNamespaceUrl);

        var response = document.DocumentElement;
        if (response is null || response.LocalName != "Response" || response.NamespaceURI != ProtocolNamespace)
        {
            return SamlValidationResult.Failure("invalid-response", "Document is not a SAML 2.0 Response.");
        }

        var statusCode = (response.SelectSingleNode("samlp:Status/samlp:StatusCode", ns) as XmlElement)?.GetAttribute("Value");
        if (statusCode != SuccessStatus)
        {
            return SamlValidationResult.Failure("idp-error", $"IdP returned error status: {statusCode}.");
        }

        if (document.GetElementsByTagName("EncryptedAssertion", AssertionNamespace).Count > 0)
        {
            return SamlValidationResult.Failure("encrypted-assertion-unsupported", "Encrypted assertions are not supported.");
        }

        // Exactly one Assertion anywhere in the document, sitting directly under the Response.
        // Counting every descendant, not just children, is what defeats signature wrapping: there
        // is no second Assertion to hide the signed one behind.
        var assertions = document.GetElementsByTagName("Assertion", AssertionNamespace);
        if (assertions.Count == 0)
        {
            return SamlValidationResult.Failure("missing-assertion", "SAML Response contains no Assertion element.");
        }

        if (assertions.Count > 1)
        {
            return SamlValidationResult.Failure("multiple-assertions", "SAML Response must contain exactly one Assertion.");
        }

        var assertion = (XmlElement)assertions[0]!;
        if (!ReferenceEquals(assertion.ParentNode, response))
        {
            return SamlValidationResult.Failure("assertion-misplaced", "The Assertion must be a direct child of the Response.");
        }

        var assertionId = assertion.GetAttribute("ID");
        if (string.IsNullOrEmpty(assertionId))
        {
            return SamlValidationResult.Failure("missing-assertion-id", "SAML Assertion has no ID.");
        }

        if (CountElementsWithId(document, assertionId) != 1)
        {
            return SamlValidationResult.Failure("duplicate-id", "The Assertion ID is not unique in the response.");
        }

        var issuer = assertion.SelectSingleNode("saml:Issuer", ns)?.InnerText.Trim();
        if (string.IsNullOrEmpty(issuer))
        {
            return SamlValidationResult.Failure("missing-issuer", "SAML Assertion contains no Issuer.");
        }

        // The Issuer is read before the signature is checked only to choose which pinned
        // certificate to check it against. It sits inside the Assertion, so a forged Issuer fails
        // verification below.
        var identityProvider = _identityProviders.FirstOrDefault(p =>
            p.Configuration.IsActive &&
            p.Configuration.TenantId.Equals(tenantId, StringComparison.OrdinalIgnoreCase) &&
            p.Configuration.EntityId.Equals(issuer, StringComparison.Ordinal));

        if (identityProvider is null)
        {
            return SamlValidationResult.Failure("idp-not-approved", $"Issuer is not an approved Identity Provider for tenant '{tenantId}'.");
        }

        var signatureFailure = VerifySignature(document, assertion, assertionId, identityProvider.Certificates, ns);
        if (signatureFailure is not null)
        {
            return signatureFailure;
        }

        // From here on, every value comes from the verified Assertion element.
        var config = identityProvider.Configuration;
        var nameId = assertion.SelectSingleNode("saml:Subject/saml:NameID", ns)?.InnerText.Trim();
        if (string.IsNullOrWhiteSpace(nameId))
        {
            return SamlValidationResult.Failure("missing-nameid", "Assertion Subject has no NameID.");
        }

        var now = _clock.GetUtcNow();
        if (assertion.SelectSingleNode("saml:Conditions", ns) is not XmlElement conditions ||
            !TryReadInstant(conditions, "NotBefore", out var notBefore) ||
            !TryReadInstant(conditions, "NotOnOrAfter", out var notOnOrAfter))
        {
            return SamlValidationResult.Failure("invalid-conditions", "Assertion Conditions must state NotBefore and NotOnOrAfter.");
        }

        if (now + ClockSkew < notBefore)
        {
            return SamlValidationResult.Failure("assertion-not-yet-valid", $"Assertion is not valid before {notBefore:O}.");
        }

        if (now - ClockSkew >= notOnOrAfter)
        {
            return SamlValidationResult.Failure("assertion-expired", $"Assertion expired at {notOnOrAfter:O}.");
        }

        // Every AudienceRestriction must name this service provider, and there must be one.
        var restrictions = conditions.SelectNodes("saml:AudienceRestriction", ns);
        if (restrictions is null || restrictions.Count == 0)
        {
            return SamlValidationResult.Failure("audience-mismatch", "Assertion carries no AudienceRestriction.");
        }

        foreach (XmlElement restriction in restrictions)
        {
            var audiences = restriction.SelectNodes("saml:Audience", ns)?.Cast<XmlNode>().Select(a => a.InnerText.Trim()) ?? [];
            if (!audiences.Contains(expectedAudience, StringComparer.Ordinal))
            {
                return SamlValidationResult.Failure("audience-mismatch", $"Assertion audience does not match expected '{expectedAudience}'.");
            }
        }

        // A bearer confirmation must be addressed to this tenant's consumer URL and still be current.
        var expectedRecipient = AssertionConsumerUrl(config.TenantId);
        var confirmations = assertion.SelectNodes(
            $"saml:Subject/saml:SubjectConfirmation[@Method='{BearerMethod}']/saml:SubjectConfirmationData", ns);
        if (confirmations is null || confirmations.Count == 0)
        {
            return SamlValidationResult.Failure("missing-subject-confirmation", "Assertion has no bearer SubjectConfirmation.");
        }

        var recipientMatched = false;
        var confirmationCurrent = false;
        foreach (XmlElement data in confirmations)
        {
            if (!string.Equals(data.GetAttribute("Recipient"), expectedRecipient, StringComparison.Ordinal))
            {
                continue;
            }

            recipientMatched = true;
            if (TryReadInstant(data, "NotOnOrAfter", out var confirmationExpiry) && now - ClockSkew < confirmationExpiry)
            {
                confirmationCurrent = true;
                break;
            }
        }

        if (!recipientMatched)
        {
            return SamlValidationResult.Failure("recipient-mismatch", $"Assertion is not addressed to '{expectedRecipient}'.");
        }

        if (!confirmationCurrent)
        {
            return SamlValidationResult.Failure("assertion-expired", "Assertion subject confirmation has expired.");
        }

        // Extract Attributes
        string email = nameId;
        string? givenName = null;
        string? surname = null;
        var roles = new List<string>();

        if (assertion.SelectSingleNode("saml:AttributeStatement", ns) is XmlElement attributeStatement)
        {
            foreach (XmlElement attr in attributeStatement.SelectNodes("saml:Attribute", ns)!)
            {
                var attrName = attr.GetAttribute("Name");
                var attrValues = attr.SelectNodes("saml:AttributeValue", ns)!.Cast<XmlNode>().Select(v => v.InnerText).ToList();

                if (attrName == config.AttributeMappings.GetValueOrDefault("email", "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress") ||
                    attrName.EndsWith("emailaddress", StringComparison.OrdinalIgnoreCase))
                {
                    if (attrValues.Count > 0) email = attrValues[0];
                }
                else if (attrName == config.AttributeMappings.GetValueOrDefault("given_name", "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/givenname") ||
                         attrName.EndsWith("givenname", StringComparison.OrdinalIgnoreCase))
                {
                    if (attrValues.Count > 0) givenName = attrValues[0];
                }
                else if (attrName == config.AttributeMappings.GetValueOrDefault("surname", "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/surname") ||
                         attrName.EndsWith("surname", StringComparison.OrdinalIgnoreCase))
                {
                    if (attrValues.Count > 0) surname = attrValues[0];
                }
                else if (attrName == config.AttributeMappings.GetValueOrDefault("role", "http://schemas.microsoft.com/ws/2008/06/identity/claims/role") ||
                         attrName.EndsWith("role", StringComparison.OrdinalIgnoreCase) ||
                         attrName.EndsWith("groups", StringComparison.OrdinalIgnoreCase))
                {
                    roles.AddRange(attrValues);
                }
            }
        }

        // Validate domain
        var emailParts = email.Split('@');
        if (emailParts.Length != 2)
        {
            return SamlValidationResult.Failure("invalid-email", "Subject is not a valid email address.");
        }

        var domain = emailParts[1].ToLowerInvariant();
        if (DisallowedConsumerDomains.Contains(domain))
        {
            return SamlValidationResult.Failure("consumer-domain-prohibited", $"Personal email domains like '@{domain}' are forbidden. Authentication must use institutional Google Workspace or Microsoft Entra ID accounts.");
        }

        if (!config.AllowedDomains.Any(d => d.Equals(domain, StringComparison.OrdinalIgnoreCase)))
        {
            return SamlValidationResult.Failure("domain-not-authorized", $"Domain '{domain}' is not authorized for tenant '{config.TenantId}'.");
        }

        if (roles.Count == 0)
        {
            roles.Add("preparer");
        }

        // Last, so a rejected assertion never consumes its ID.
        if (!TryConsume($"{issuer}|{assertionId}", notOnOrAfter, now))
        {
            return SamlValidationResult.Failure("assertion-replayed", "This assertion has already been used.");
        }

        var issueInstant = TryReadInstant(assertion, "IssueInstant", out var issued) ? issued : now;
        return SamlValidationResult.Success(new SamlAssertion(
            Issuer: issuer,
            SubjectNameId: nameId,
            TenantId: config.TenantId,
            Email: email,
            GivenName: givenName,
            Surname: surname,
            Roles: roles,
            IssueInstant: issueInstant,
            NotBefore: notBefore,
            NotOnOrAfter: notOnOrAfter,
            Audience: expectedAudience
        ));
    }

    private static SamlValidationResult? VerifySignature(
        XmlDocument document,
        XmlElement assertion,
        string assertionId,
        IReadOnlyList<X509Certificate2> certificates,
        XmlNamespaceManager ns)
    {
        var signatures = assertion.SelectNodes("ds:Signature", ns);
        if (signatures is null || signatures.Count == 0)
        {
            return SamlValidationResult.Failure("unsigned-assertion", "SAML Assertion is not signed.");
        }

        if (signatures.Count > 1)
        {
            return SamlValidationResult.Failure("invalid-signature", "SAML Assertion carries more than one signature.");
        }

        var signedXml = new SignedXml(document);
        try
        {
            signedXml.LoadXml((XmlElement)signatures[0]!);
        }
        catch (CryptographicException)
        {
            return SamlValidationResult.Failure("invalid-signature", "SAML Assertion signature is malformed.");
        }

        // The signature must cover this Assertion and nothing else: one Reference, to its ID.
        if (signedXml.SignedInfo is not { } signedInfo || signedInfo.References.Count != 1 ||
            signedInfo.References[0] is not Reference reference)
        {
            return SamlValidationResult.Failure("invalid-signature", "The signature must contain exactly one reference.");
        }

        if (!string.Equals(reference.Uri, "#" + assertionId, StringComparison.Ordinal))
        {
            return SamlValidationResult.Failure("signature-reference-mismatch", "The signature does not reference the Assertion.");
        }

        if (!AllowedCanonicalizationMethods.Contains(signedInfo.CanonicalizationMethod) ||
            !AllowedSignatureMethods.Contains(signedInfo.SignatureMethod ?? string.Empty) ||
            !AllowedDigestMethods.Contains(reference.DigestMethod ?? string.Empty))
        {
            return SamlValidationResult.Failure("unsupported-signature-algorithm", "The signature must use RSA with SHA-256 or stronger and exclusive or inclusive canonicalisation.");
        }

        foreach (Transform transform in reference.TransformChain)
        {
            if (!AllowedTransforms.Contains(transform.Algorithm ?? string.Empty))
            {
                return SamlValidationResult.Failure("unsupported-signature-algorithm", "The signature uses a transform that is not accepted.");
            }
        }

        try
        {
            // SignedXml resolves the reference by ID itself; confirm it lands on the element this
            // method was asked about, not on some other element sharing the ID.
            if (!ReferenceEquals(signedXml.GetIdElement(document, assertionId), assertion))
            {
                return SamlValidationResult.Failure("signature-reference-mismatch", "The signature does not reference the Assertion.");
            }

            foreach (var certificate in certificates)
            {
                using var key = certificate.GetRSAPublicKey();
                // The pinned certificate's key, never the KeyInfo the document carries.
                if (key is not null && signedXml.CheckSignature(key))
                {
                    return null;
                }
            }
        }
        catch (CryptographicException)
        {
            return SamlValidationResult.Failure("invalid-signature", "SAML Assertion signature could not be verified.");
        }

        return SamlValidationResult.Failure("invalid-signature", "SAML Assertion signature does not verify against the identity provider's certificate.");
    }

    private bool TryConsume(string key, DateTimeOffset expiresAt, DateTimeOffset now)
    {
        if (_consumedAssertions.Count >= ReplayCachePruneThreshold)
        {
            foreach (var entry in _consumedAssertions)
            {
                if (entry.Value + ClockSkew < now)
                {
                    _consumedAssertions.TryRemove(entry.Key, out _);
                }
            }
        }

        return _consumedAssertions.TryAdd(key, expiresAt);
    }

    private static int CountElementsWithId(XmlDocument document, string id)
    {
        var count = 0;
        foreach (XmlElement element in document.GetElementsByTagName("*"))
        {
            if (element.GetAttribute("ID") == id || element.GetAttribute("Id") == id || element.GetAttribute("id") == id)
            {
                count++;
            }
        }

        return count;
    }

    private static bool TryReadInstant(XmlElement element, string attribute, out DateTimeOffset value) =>
        DateTimeOffset.TryParse(
            element.GetAttribute(attribute),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out value);

    private static RegisteredIdentityProvider Register(InstitutionIdpConfiguration configuration, int index)
    {
        string Problem(string message) =>
            $"{IdentityProvidersSetting}[{index}] ({configuration?.TenantId ?? "no tenant"}): {message}";

        if (configuration is null)
        {
            throw new InvalidOperationException($"{IdentityProvidersSetting}[{index}] is empty.");
        }

        if (string.IsNullOrWhiteSpace(configuration.TenantId) || !TenantResolution.TenantIdRegex().IsMatch(configuration.TenantId))
        {
            throw new InvalidOperationException(Problem("tenantId is missing or malformed."));
        }

        if (string.IsNullOrWhiteSpace(configuration.EntityId))
        {
            throw new InvalidOperationException(Problem("entityId is required."));
        }

        if (!Uri.TryCreate(configuration.SsoServiceUrl, UriKind.Absolute, out var sso) || sso.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException(Problem("ssoServiceUrl must be an absolute https URL."));
        }

        if (configuration.AllowedDomains is not { Count: > 0 } ||
            configuration.AllowedDomains.Any(d => string.IsNullOrWhiteSpace(d) || DisallowedConsumerDomains.Contains(d.Trim())))
        {
            throw new InvalidOperationException(Problem("allowedDomains must list institutional domains only."));
        }

        if (configuration.SigningCertificates is not { Count: > 0 })
        {
            throw new InvalidOperationException(Problem("at least one signing certificate is required."));
        }

        var certificates = new List<X509Certificate2>();
        foreach (var encoded in configuration.SigningCertificates)
        {
            var certificate = LoadCertificate(encoded)
                ?? throw new InvalidOperationException(Problem("a signing certificate is not a valid X.509 certificate."));
            using var key = certificate.GetRSAPublicKey();
            if (key is null || key.KeySize < SessionTokenKeys.MinimumKeySizeBits)
            {
                certificate.Dispose();
                throw new InvalidOperationException(Problem($"a signing certificate must carry an RSA key of at least {SessionTokenKeys.MinimumKeySizeBits} bits."));
            }

            certificates.Add(certificate);
        }

        var normalized = configuration with
        {
            AllowedDomains = configuration.AllowedDomains.Select(d => d.Trim().ToLowerInvariant()).ToArray(),
            AttributeMappings = configuration.AttributeMappings ?? new Dictionary<string, string>(),
        };

        return new RegisteredIdentityProvider(normalized, certificates);
    }

    private static X509Certificate2? LoadCertificate(string? encoded)
    {
        if (string.IsNullOrWhiteSpace(encoded))
        {
            return null;
        }

        try
        {
            if (encoded.Contains("-----BEGIN", StringComparison.Ordinal))
            {
                return X509Certificate2.CreateFromPem(encoded);
            }

            // Bare Base64 DER, as it appears in IdP metadata and Entra's "Certificate (Base64)".
            var der = Convert.FromBase64String(string.Concat(encoded.Where(c => !char.IsWhiteSpace(c))));
            return X509CertificateLoader.LoadCertificate(der);
        }
        catch (Exception error) when (error is CryptographicException or FormatException or ArgumentException)
        {
            return null;
        }
    }

    private sealed record RegisteredIdentityProvider(
        InstitutionIdpConfiguration Configuration,
        IReadOnlyList<X509Certificate2> Certificates);
}
