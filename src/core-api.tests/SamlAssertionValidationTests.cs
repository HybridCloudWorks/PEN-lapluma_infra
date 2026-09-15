using System.Text;
using LaPluma.CoreApi.Auth;
using Xunit;

namespace LaPluma.CoreApi.Tests;

public sealed class SamlAssertionValidationTests
{
    private readonly SamlServiceProvider _provider = new(
        spEntityId: "https://api.lapluma.app/auth/saml/metadata",
        spAcsBaseUrl: "https://api.lapluma.app/auth/saml/acs"
    );

    [Fact]
    public void GenerateSpMetadataXml_ProducesValidSaml2Metadata()
    {
        var xml = _provider.GenerateSpMetadataXml("https://api.lapluma.app/auth/saml/metadata", "https://api.lapluma.app/auth/saml/acs");

        Assert.Contains("md:EntityDescriptor", xml);
        Assert.Contains("entityID=\"https://api.lapluma.app/auth/saml/metadata\"", xml);
        Assert.Contains("md:AssertionConsumerService", xml);
        Assert.Contains("urn:oasis:names:tc:SAML:2.0:bindings:HTTP-POST", xml);
        Assert.Contains("urn:oasis:names:tc:SAML:1.1:nameid-format:emailAddress", xml);
    }

    [Fact]
    public async Task CreateAuthnRequest_ValidDomain_GeneratesRedirectUrl()
    {
        var request = await _provider.CreateAuthnRequestAsync("hybridcloudworks.com", SamlIdpType.GoogleWorkspace);

        Assert.NotNull(request);
        Assert.StartsWith("_", request.Id);
        Assert.Contains("https://accounts.google.com/o/saml2/idp", request.RedirectUrl);
        Assert.Contains("SAMLRequest=", request.RedirectUrl);
        Assert.Equal("https://api.lapluma.app/auth/saml/acs/tenant_hybridcloudworks", request.AssertionConsumerServiceUrl);
    }

    [Theory]
    [InlineData("gmail.com")]
    [InlineData("googlemail.com")]
    [InlineData("outlook.com")]
    [InlineData("hotmail.com")]
    [InlineData("yahoo.com")]
    [InlineData("icloud.com")]
    public async Task CreateAuthnRequest_PersonalDomain_FailsClosed(string personalDomain)
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _provider.CreateAuthnRequestAsync(personalDomain));

        Assert.Contains("Personal domain", ex.Message);
        Assert.Contains("Only verified institutional Google Workspace or Microsoft Entra ID accounts are allowed", ex.Message);
    }

    [Fact]
    public async Task ValidateAssertion_ValidGoogleWorkspaceAssertion_Succeeds()
    {
        var xml = CreateSampleSamlResponseXml(
            issuer: "https://accounts.google.com/o/saml2?idpid=C03hcwks",
            email: "spatino@hybridcloudworks.com",
            audience: "https://api.lapluma.app/auth/saml/metadata",
            signed: true,
            expired: false
        );

        var result = await _provider.ValidateAssertionResponseAsync(
            "tenant_hybridcloudworks",
            xml,
            "https://api.lapluma.app/auth/saml/metadata"
        );

        Assert.True(result.IsValid, result.ErrorMessage);
        Assert.NotNull(result.Assertion);
        Assert.Equal("spatino@hybridcloudworks.com", result.Assertion.Email);
        Assert.Equal("tenant_hybridcloudworks", result.Assertion.TenantId);
        Assert.Contains("preparer", result.Assertion.Roles);
    }

    [Fact]
    public async Task ValidateAssertion_ValidEntraIdAssertion_Succeeds()
    {
        var xml = CreateSampleSamlResponseXml(
            issuer: "https://sts.windows.net/4a2b9a76-2e4b-47e1-872f-5b1234567890/",
            email: "admin@hybridcloudworks.com",
            audience: "https://api.lapluma.app/auth/saml/metadata",
            signed: true,
            expired: false,
            role: "reviewer"
        );

        var result = await _provider.ValidateAssertionResponseAsync(
            "tenant_hybridcloudworks",
            xml,
            "https://api.lapluma.app/auth/saml/metadata"
        );

        Assert.True(result.IsValid, result.ErrorMessage);
        Assert.NotNull(result.Assertion);
        Assert.Equal("admin@hybridcloudworks.com", result.Assertion.Email);
        Assert.Contains("reviewer", result.Assertion.Roles);
    }

    [Fact]
    public async Task ValidateAssertion_UnapprovedIdpIssuer_FailsClosed()
    {
        var xml = CreateSampleSamlResponseXml(
            issuer: "https://malicious-idp.com/saml",
            email: "attacker@hybridcloudworks.com",
            audience: "https://api.lapluma.app/auth/saml/metadata",
            signed: true,
            expired: false
        );

        var result = await _provider.ValidateAssertionResponseAsync(
            "tenant_hybridcloudworks",
            xml,
            "https://api.lapluma.app/auth/saml/metadata"
        );

        Assert.False(result.IsValid);
        Assert.Equal("idp-not-approved", result.ErrorCode);
    }

    [Fact]
    public async Task ValidateAssertion_ConsumerEmailDomain_FailsClosed()
    {
        var xml = CreateSampleSamlResponseXml(
            issuer: "https://accounts.google.com/o/saml2?idpid=C03hcwks",
            email: "hybridcloudworks@gmail.com",
            audience: "https://api.lapluma.app/auth/saml/metadata",
            signed: true,
            expired: false
        );

        var result = await _provider.ValidateAssertionResponseAsync(
            "tenant_hybridcloudworks",
            xml,
            "https://api.lapluma.app/auth/saml/metadata"
        );

        Assert.False(result.IsValid);
        Assert.Equal("consumer-domain-prohibited", result.ErrorCode);
    }

    [Fact]
    public async Task ValidateAssertion_ExpiredAssertion_FailsClosed()
    {
        var xml = CreateSampleSamlResponseXml(
            issuer: "https://accounts.google.com/o/saml2?idpid=C03hcwks",
            email: "spatino@hybridcloudworks.com",
            audience: "https://api.lapluma.app/auth/saml/metadata",
            signed: true,
            expired: true
        );

        var result = await _provider.ValidateAssertionResponseAsync(
            "tenant_hybridcloudworks",
            xml,
            "https://api.lapluma.app/auth/saml/metadata"
        );

        Assert.False(result.IsValid);
        Assert.Equal("assertion-expired", result.ErrorCode);
    }

    [Fact]
    public async Task ValidateAssertion_AudienceMismatch_FailsClosed()
    {
        var xml = CreateSampleSamlResponseXml(
            issuer: "https://accounts.google.com/o/saml2?idpid=C03hcwks",
            email: "spatino@hybridcloudworks.com",
            audience: "https://different-app.com/saml",
            signed: true,
            expired: false
        );

        var result = await _provider.ValidateAssertionResponseAsync(
            "tenant_hybridcloudworks",
            xml,
            "https://api.lapluma.app/auth/saml/metadata"
        );

        Assert.False(result.IsValid);
        Assert.Equal("audience-mismatch", result.ErrorCode);
    }

    [Fact]
    public async Task ValidateAssertion_UnsignedAssertion_FailsClosed()
    {
        var xml = CreateSampleSamlResponseXml(
            issuer: "https://accounts.google.com/o/saml2?idpid=C03hcwks",
            email: "spatino@hybridcloudworks.com",
            audience: "https://api.lapluma.app/auth/saml/metadata",
            signed: false,
            expired: false
        );

        var result = await _provider.ValidateAssertionResponseAsync(
            "tenant_hybridcloudworks",
            xml,
            "https://api.lapluma.app/auth/saml/metadata"
        );

        Assert.False(result.IsValid);
        Assert.Equal("unsigned-assertion", result.ErrorCode);
    }

    [Fact]
    public async Task ExchangeAssertionForToken_ProducesScopedBearerJwt()
    {
        var assertion = new SamlAssertion(
            Issuer: "https://accounts.google.com/o/saml2?idpid=C03hcwks",
            SubjectNameId: "spatino@hybridcloudworks.com",
            TenantId: "tenant_hybridcloudworks",
            Email: "spatino@hybridcloudworks.com",
            GivenName: "Saul",
            Surname: "Patino",
            Roles: new[] { "admin", "reviewer" },
            IssueInstant: DateTimeOffset.UtcNow,
            NotBefore: DateTimeOffset.UtcNow.AddMinutes(-5),
            NotOnOrAfter: DateTimeOffset.UtcNow.AddHours(1),
            Audience: "https://api.lapluma.app/auth/saml/metadata"
        );

        var exchange = await _provider.ExchangeAssertionForTokenAsync(assertion);

        Assert.NotNull(exchange);
        Assert.StartsWith("lp_saml_", exchange.AccessToken);
        Assert.Equal("Bearer", exchange.TokenType);
        Assert.Equal("tenant_hybridcloudworks", exchange.TenantId);
        Assert.Equal("spatino@hybridcloudworks.com", exchange.UserId);
        Assert.Contains("admin", exchange.Roles);
    }

    private static string CreateSampleSamlResponseXml(
        string issuer,
        string email,
        string audience,
        bool signed,
        bool expired,
        string role = "preparer")
    {
        var now = DateTimeOffset.UtcNow;
        var notBefore = expired ? now.AddHours(-2) : now.AddMinutes(-5);
        var notOnOrAfter = expired ? now.AddHours(-1) : now.AddHours(1);

        var signatureXml = signed ? """
            <ds:Signature xmlns:ds="http://www.w3.org/2000/09/xmldsig#">
                <ds:SignedInfo>
                    <ds:CanonicalizationMethod Algorithm="http://www.w3.org/2001/10/xml-exc-c14n#"/>
                    <ds:SignatureMethod Algorithm="http://www.w3.org/2001/04/xmldsig-more#rsa-sha256"/>
                </ds:SignedInfo>
                <ds:SignatureValue>MOCK_SIGNATURE_BYTES</ds:SignatureValue>
            </ds:Signature>
        """ : "";

        return $"""
        <samlp:Response xmlns:samlp="urn:oasis:names:tc:SAML:2.0:protocol"
            xmlns:saml="urn:oasis:names:tc:SAML:2.0:assertion"
            ID="_resp12345"
            Version="2.0"
            IssueInstant="{now:O}">
            <saml:Issuer>{issuer}</saml:Issuer>
            <samlp:Status>
                <samlp:StatusCode Value="urn:oasis:names:tc:SAML:2.0:status:Success"/>
            </samlp:Status>
            <saml:Assertion ID="_asst12345" Version="2.0" IssueInstant="{now:O}">
                <saml:Issuer>{issuer}</saml:Issuer>
                {signatureXml}
                <saml:Subject>
                    <saml:NameID Format="urn:oasis:names:tc:SAML:1.1:nameid-format:emailAddress">{email}</saml:NameID>
                </saml:Subject>
                <saml:Conditions NotBefore="{notBefore:O}" NotOnOrAfter="{notOnOrAfter:O}">
                    <saml:AudienceRestriction>
                        <saml:Audience>{audience}</saml:Audience>
                    </saml:AudienceRestriction>
                </saml:Conditions>
                <saml:AttributeStatement>
                    <saml:Attribute Name="http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress">
                        <saml:AttributeValue>{email}</saml:AttributeValue>
                    </saml:Attribute>
                    <saml:Attribute Name="http://schemas.microsoft.com/ws/2008/06/identity/claims/role">
                        <saml:AttributeValue>{role}</saml:AttributeValue>
                    </saml:Attribute>
                </saml:AttributeStatement>
            </saml:Assertion>
        </samlp:Response>
        """;
    }
}
