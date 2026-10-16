using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Text.Json;
using System.Xml;
using LaPluma.CoreApi.Auth;

namespace LaPluma.CoreApi.Tests;

/// <summary>What a test wants an assertion to say. Every default describes a valid sign-in.</summary>
internal sealed record AssertionSpec
{
    public string AssertionId { get; init; } = "_" + Guid.NewGuid().ToString("N");
    public string Issuer { get; init; } = TestIdentityProvider.GoogleEntityId;
    public string Email { get; init; } = "spatino@hybridcloudworks.com";
    public string Role { get; init; } = "preparer";
    public string Audience { get; init; } = TestIdentityProvider.SpEntityId;
    public string Recipient { get; init; } = TestIdentityProvider.AcsUrl;
    public DateTimeOffset NotBefore { get; init; } = DateTimeOffset.UtcNow.AddMinutes(-5);
    public DateTimeOffset NotOnOrAfter { get; init; } = DateTimeOffset.UtcNow.AddMinutes(10);
    public bool IncludeAudienceRestriction { get; init; } = true;

    public static AssertionSpec Expired() => new()
    {
        NotBefore = DateTimeOffset.UtcNow.AddHours(-2),
        NotOnOrAfter = DateTimeOffset.UtcNow.AddHours(-1),
    };
}

/// <summary>
/// A stand-in identity provider with its own self-signed certificate, generated per instance. The
/// production code carries no certificate at all; tests supply this one through the same
/// configuration a deployment uses.
/// </summary>
internal sealed class TestIdentityProvider : IDisposable
{
    public const string TenantId = "tenant_hybridcloudworks";
    public const string GoogleEntityId = "https://idp.example.invalid/google";
    public const string EntraEntityId = "https://idp.example.invalid/entra";
    public const string SpEntityId = SamlServiceProvider.DefaultSpEntityId;
    public const string AcsBaseUrl = SamlServiceProvider.DefaultSpAcsBaseUrl;
    public static string AcsUrl => $"{AcsBaseUrl}/{TenantId}";

    private const string ProtocolNamespace = "urn:oasis:names:tc:SAML:2.0:protocol";
    private const string AssertionNamespace = "urn:oasis:names:tc:SAML:2.0:assertion";

    public TestIdentityProvider()
    {
        Key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=LaPluma test identity provider", Key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        Certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }

    public RSA Key { get; }

    public X509Certificate2 Certificate { get; }

    public string CertificatePem => Certificate.ExportCertificatePem();

    /// <summary>The bare Base64 DER form IdP metadata and Entra's download carry.</summary>
    public string CertificateBase64 => Convert.ToBase64String(Certificate.RawData);

    public InstitutionIdpConfiguration Configuration(
        string entityId = GoogleEntityId, SamlIdpType type = SamlIdpType.GoogleWorkspace, string? certificate = null) =>
        new(
            TenantId: TenantId,
            IdpType: type,
            EntityId: entityId,
            SsoServiceUrl: $"{entityId}/sso",
            SigningCertificates: [certificate ?? CertificatePem],
            AllowedDomains: ["hybridcloudworks.com"],
            AttributeMappings: new Dictionary<string, string>
            {
                ["email"] = "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress",
                ["role"] = "http://schemas.microsoft.com/ws/2008/06/identity/claims/role",
            },
            AdminConsentVerified: true,
            IsActive: true);

    /// <summary>The JSON a deployment puts in Secret Manager for <c>Saml__IdentityProvidersJson</c>.</summary>
    public string IdentityProvidersJson(string? certificate = null) => JsonSerializer.Serialize(new[]
    {
        new
        {
            tenantId = TenantId,
            idpType = "GoogleWorkspace",
            entityId = GoogleEntityId,
            ssoServiceUrl = $"{GoogleEntityId}/sso",
            signingCertificates = new[] { certificate ?? CertificatePem },
            allowedDomains = new[] { "hybridcloudworks.com" },
            adminConsentVerified = true,
            isActive = true,
        },
    });

    public SamlServiceProvider CreateServiceProvider(TimeProvider? clock = null, params InstitutionIdpConfiguration[] extra) =>
        new(SpEntityId, AcsBaseUrl, [Configuration(), .. extra], clock);

    public static string UnsignedResponse(AssertionSpec spec) => $"""
        <samlp:Response xmlns:samlp="{ProtocolNamespace}" xmlns:saml="{AssertionNamespace}" ID="_resp{Guid.NewGuid():N}" Version="2.0" IssueInstant="{DateTimeOffset.UtcNow:O}" Destination="{spec.Recipient}">
            <saml:Issuer>{spec.Issuer}</saml:Issuer>
            <samlp:Status>
                <samlp:StatusCode Value="urn:oasis:names:tc:SAML:2.0:status:Success"/>
            </samlp:Status>
            <saml:Assertion ID="{spec.AssertionId}" Version="2.0" IssueInstant="{DateTimeOffset.UtcNow:O}">
                <saml:Issuer>{spec.Issuer}</saml:Issuer>
                <saml:Subject>
                    <saml:NameID Format="urn:oasis:names:tc:SAML:1.1:nameid-format:emailAddress">{spec.Email}</saml:NameID>
                    <saml:SubjectConfirmation Method="urn:oasis:names:tc:SAML:2.0:cm:bearer">
                        <saml:SubjectConfirmationData NotOnOrAfter="{spec.NotOnOrAfter:O}" Recipient="{spec.Recipient}"/>
                    </saml:SubjectConfirmation>
                </saml:Subject>
                <saml:Conditions NotBefore="{spec.NotBefore:O}" NotOnOrAfter="{spec.NotOnOrAfter:O}">
                    {(spec.IncludeAudienceRestriction ? $"<saml:AudienceRestriction><saml:Audience>{spec.Audience}</saml:Audience></saml:AudienceRestriction>" : "")}
                </saml:Conditions>
                <saml:AttributeStatement>
                    <saml:Attribute Name="http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress">
                        <saml:AttributeValue>{spec.Email}</saml:AttributeValue>
                    </saml:Attribute>
                    <saml:Attribute Name="http://schemas.microsoft.com/ws/2008/06/identity/claims/role">
                        <saml:AttributeValue>{spec.Role}</saml:AttributeValue>
                    </saml:Attribute>
                </saml:AttributeStatement>
            </saml:Assertion>
        </samlp:Response>
        """;

    /// <summary>A response whose Assertion is signed the way Google Workspace and Entra ID sign it.</summary>
    public string SignedResponse(
        AssertionSpec spec,
        string signatureMethod = SignedXml.XmlDsigRSASHA256Url,
        string digestMethod = SignedXml.XmlDsigSHA256Url) =>
        Sign(UnsignedResponse(spec), signatureMethod, digestMethod).OuterXml;

    public XmlDocument Sign(
        string responseXml,
        string signatureMethod = SignedXml.XmlDsigRSASHA256Url,
        string digestMethod = SignedXml.XmlDsigSHA256Url)
    {
        var document = Load(responseXml);
        var assertion = (XmlElement)document.GetElementsByTagName("Assertion", AssertionNamespace)[0]!;

        var signedXml = new SignedXml(assertion) { SigningKey = Key };
        signedXml.SignedInfo!.CanonicalizationMethod = SignedXml.XmlDsigExcC14NTransformUrl;
        signedXml.SignedInfo.SignatureMethod = signatureMethod;

        var reference = new Reference("#" + assertion.GetAttribute("ID")) { DigestMethod = digestMethod };
        reference.AddTransform(new XmlDsigEnvelopedSignatureTransform());
        reference.AddTransform(new XmlDsigExcC14NTransform());
        signedXml.AddReference(reference);

        var keyInfo = new KeyInfo();
        keyInfo.AddClause(new KeyInfoX509Data(Certificate));
        signedXml.KeyInfo = keyInfo;
        signedXml.ComputeSignature();

        // The schema places the signature directly after the Assertion's Issuer.
        var issuer = assertion.GetElementsByTagName("Issuer", AssertionNamespace)[0]!;
        assertion.InsertAfter(document.ImportNode(signedXml.GetXml(), deep: true), issuer);
        return document;
    }

    public static XmlDocument Load(string xml)
    {
        var document = new XmlDocument { PreserveWhitespace = true };
        document.LoadXml(xml);
        return document;
    }

    public static string Base64(string xml) => Convert.ToBase64String(Encoding.UTF8.GetBytes(xml));

    public void Dispose()
    {
        Certificate.Dispose();
        Key.Dispose();
    }
}
