using System.Security.Cryptography.Xml;
using System.Xml;
using LaPluma.CoreApi.Auth;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace LaPluma.CoreApi.Tests;

/// <summary>
/// An assertion is trusted because its signature verifies against the institution's pinned
/// certificate, and for no other reason. Every rejection below is a response an attacker can build
/// without that certificate's private key.
/// </summary>
public sealed class SamlAssertionValidationTests : IDisposable
{
    private const string AssertionNamespace = "urn:oasis:names:tc:SAML:2.0:assertion";
    private const string ProtocolNamespace = "urn:oasis:names:tc:SAML:2.0:protocol";

    private readonly TestIdentityProvider _idp = new();
    private readonly SamlServiceProvider _provider;

    public SamlAssertionValidationTests() => _provider = _idp.CreateServiceProvider();

    public void Dispose() => _idp.Dispose();

    private Task<SamlValidationResult> Validate(string response) =>
        _provider.ValidateAssertionResponseAsync(TestIdentityProvider.TenantId, response, TestIdentityProvider.SpEntityId);

    [Fact]
    public void GenerateSpMetadataXml_ProducesValidSaml2Metadata()
    {
        var xml = _provider.GenerateSpMetadataXml(_provider.SpEntityId, _provider.SpAcsBaseUrl);

        Assert.Contains("md:EntityDescriptor", xml);
        Assert.Contains("entityID=\"https://api.lapluma.app/auth/saml/metadata\"", xml);
        Assert.Contains("md:AssertionConsumerService", xml);
        Assert.Contains("WantAssertionsSigned=\"true\"", xml);
        Assert.Contains("urn:oasis:names:tc:SAML:2.0:bindings:HTTP-POST", xml);
        Assert.Contains("urn:oasis:names:tc:SAML:1.1:nameid-format:emailAddress", xml);
    }

    [Fact]
    public async Task CreateAuthnRequest_ValidDomain_GeneratesRedirectUrl()
    {
        var request = await _provider.CreateAuthnRequestAsync("hybridcloudworks.com", SamlIdpType.GoogleWorkspace);

        Assert.NotNull(request);
        Assert.StartsWith("_", request.Id);
        Assert.StartsWith($"{TestIdentityProvider.GoogleEntityId}/sso?SAMLRequest=", request.RedirectUrl);
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
    public async Task With_no_identity_provider_configured_every_sign_in_fails_closed()
    {
        // The state of a deployment whose secret has not been provisioned: nothing is built in.
        var provider = SamlServiceProvider.FromConfiguration(new ConfigurationBuilder().Build());

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.CreateAuthnRequestAsync("hybridcloudworks.com"));
        var result = await provider.ValidateAssertionResponseAsync(
            TestIdentityProvider.TenantId, _idp.SignedResponse(new AssertionSpec()), TestIdentityProvider.SpEntityId);

        Assert.False(result.IsValid);
        Assert.Equal("idp-not-approved", result.ErrorCode);
    }

    [Theory]
    [InlineData("MOCK_GOOGLE_WORKSPACE_CERT")]
    [InlineData("MOCK_MICROSOFT_ENTRA_ID_CERT")]
    [InlineData("-----BEGIN CERTIFICATE-----\nMIIDdDCCAlygAwIBAgIGAY...MOCK...\n-----END CERTIFICATE-----")]
    public void A_placeholder_certificate_stops_the_host_instead_of_loading(string certificate)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [SamlServiceProvider.IdentityProvidersSetting] = _idp.IdentityProvidersJson(certificate),
            })
            .Build();

        var error = Assert.Throws<InvalidOperationException>(() => SamlServiceProvider.FromConfiguration(configuration));
        Assert.Contains("certificate", error.Message);
    }

    [Fact]
    public async Task Certificates_load_from_configuration_in_PEM_or_bare_Base64()
    {
        foreach (var certificate in new[] { _idp.CertificatePem, _idp.CertificateBase64 })
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [SamlServiceProvider.IdentityProvidersSetting] = _idp.IdentityProvidersJson(certificate),
                })
                .Build();
            var provider = SamlServiceProvider.FromConfiguration(configuration);

            var result = await provider.ValidateAssertionResponseAsync(
                TestIdentityProvider.TenantId, _idp.SignedResponse(new AssertionSpec()), TestIdentityProvider.SpEntityId);

            Assert.True(result.IsValid, result.ErrorMessage);
        }
    }

    [Fact]
    public async Task ValidateAssertion_ValidGoogleWorkspaceAssertion_Succeeds()
    {
        var result = await Validate(TestIdentityProvider.Base64(_idp.SignedResponse(new AssertionSpec())));

        Assert.True(result.IsValid, result.ErrorMessage);
        Assert.NotNull(result.Assertion);
        Assert.Equal("spatino@hybridcloudworks.com", result.Assertion.Email);
        Assert.Equal("tenant_hybridcloudworks", result.Assertion.TenantId);
        Assert.Contains("preparer", result.Assertion.Roles);
    }

    [Fact]
    public async Task ValidateAssertion_ValidEntraIdAssertion_Succeeds()
    {
        using var entra = new TestIdentityProvider();
        var provider = _idp.CreateServiceProvider(
            clock: null, entra.Configuration(TestIdentityProvider.EntraEntityId, SamlIdpType.EntraIdSaml));
        var response = entra.SignedResponse(new AssertionSpec
        {
            Issuer = TestIdentityProvider.EntraEntityId,
            Email = "admin@hybridcloudworks.com",
            Role = "reviewer",
        });

        var result = await provider.ValidateAssertionResponseAsync(
            TestIdentityProvider.TenantId, response, TestIdentityProvider.SpEntityId);

        Assert.True(result.IsValid, result.ErrorMessage);
        Assert.Equal("admin@hybridcloudworks.com", result.Assertion!.Email);
        Assert.Contains("reviewer", result.Assertion.Roles);
    }

    [Fact]
    public async Task ValidateAssertion_UnsignedAssertion_FailsClosed()
    {
        var result = await Validate(TestIdentityProvider.UnsignedResponse(new AssertionSpec()));

        Assert.False(result.IsValid);
        Assert.Equal("unsigned-assertion", result.ErrorCode);
    }

    [Fact]
    public async Task A_signature_element_that_is_merely_present_is_not_enough()
    {
        // The shape the previous implementation accepted: a Signature element with no valid value.
        var document = TestIdentityProvider.Load(TestIdentityProvider.UnsignedResponse(new AssertionSpec()));
        var assertion = (XmlElement)document.GetElementsByTagName("Assertion", AssertionNamespace)[0]!;
        var fake = TestIdentityProvider.Load($"""
            <ds:Signature xmlns:ds="http://www.w3.org/2000/09/xmldsig#"><ds:SignedInfo><ds:CanonicalizationMethod Algorithm="http://www.w3.org/2001/10/xml-exc-c14n#"/><ds:SignatureMethod Algorithm="http://www.w3.org/2001/04/xmldsig-more#rsa-sha256"/><ds:Reference URI="#{assertion.GetAttribute("ID")}"><ds:Transforms><ds:Transform Algorithm="http://www.w3.org/2000/09/xmldsig#enveloped-signature"/></ds:Transforms><ds:DigestMethod Algorithm="http://www.w3.org/2001/04/xmlenc#sha256"/><ds:DigestValue>AAAA</ds:DigestValue></ds:Reference></ds:SignedInfo><ds:SignatureValue>AAAA</ds:SignatureValue></ds:Signature>
            """).DocumentElement!;
        assertion.InsertAfter(document.ImportNode(fake, deep: true), assertion.FirstChild!.NextSibling!);

        var result = await Validate(document.OuterXml);

        Assert.False(result.IsValid);
        Assert.Equal("invalid-signature", result.ErrorCode);
    }

    [Fact]
    public async Task A_tampered_assertion_fails_signature_verification()
    {
        var document = TestIdentityProvider.Load(_idp.SignedResponse(new AssertionSpec()));
        document.GetElementsByTagName("NameID", AssertionNamespace)[0]!.InnerText = "admin@hybridcloudworks.com";
        document.GetElementsByTagName("AttributeValue", AssertionNamespace)[0]!.InnerText = "admin@hybridcloudworks.com";

        var result = await Validate(document.OuterXml);

        Assert.False(result.IsValid);
        Assert.Equal("invalid-signature", result.ErrorCode);
    }

    [Fact]
    public async Task An_assertion_signed_by_a_different_key_is_rejected()
    {
        using var impostor = new TestIdentityProvider();

        var result = await Validate(impostor.SignedResponse(new AssertionSpec()));

        Assert.False(result.IsValid);
        Assert.Equal("invalid-signature", result.ErrorCode);
    }

    [Fact]
    public async Task A_forged_assertion_wrapped_around_the_signed_one_is_rejected()
    {
        // Signature wrapping: the genuine signed assertion is kept so its signature still
        // verifies, and a forged one is placed where a naive reader looks first.
        var document = TestIdentityProvider.Load(_idp.SignedResponse(new AssertionSpec()));
        var response = document.DocumentElement!;
        var genuine = (XmlElement)document.GetElementsByTagName("Assertion", AssertionNamespace)[0]!;
        var forged = (XmlElement)genuine.CloneNode(deep: true);
        forged.SetAttribute("ID", "_forged");
        forged.GetElementsByTagName("NameID", AssertionNamespace)[0]!.InnerText = "attacker@hybridcloudworks.com";
        forged.RemoveChild(forged.GetElementsByTagName("Signature", SignedXml.XmlDsigNamespaceUrl)[0]!);

        var extensions = document.CreateElement("samlp", "Extensions", ProtocolNamespace);
        response.ReplaceChild(forged, genuine);
        extensions.AppendChild(genuine);
        response.InsertBefore(extensions, response.FirstChild);

        var result = await Validate(document.OuterXml);

        Assert.False(result.IsValid);
        Assert.Equal("multiple-assertions", result.ErrorCode);
    }

    [Fact]
    public async Task A_forged_sibling_assertion_is_rejected()
    {
        var document = TestIdentityProvider.Load(_idp.SignedResponse(new AssertionSpec()));
        var genuine = (XmlElement)document.GetElementsByTagName("Assertion", AssertionNamespace)[0]!;
        var forged = (XmlElement)genuine.CloneNode(deep: true);
        forged.SetAttribute("ID", "_forged");
        forged.GetElementsByTagName("NameID", AssertionNamespace)[0]!.InnerText = "attacker@hybridcloudworks.com";
        document.DocumentElement!.InsertBefore(forged, genuine);

        var result = await Validate(document.OuterXml);

        Assert.False(result.IsValid);
        Assert.Equal("multiple-assertions", result.ErrorCode);
    }

    [Fact]
    public async Task A_signature_moved_onto_a_forged_assertion_is_rejected()
    {
        // The forged assertion carries the genuine signature, whose Reference still names the
        // genuine assertion's ID. The signature must cover the element that is then read.
        var document = TestIdentityProvider.Load(_idp.SignedResponse(new AssertionSpec()));
        var assertion = (XmlElement)document.GetElementsByTagName("Assertion", AssertionNamespace)[0]!;
        assertion.SetAttribute("ID", "_forged");
        assertion.GetElementsByTagName("NameID", AssertionNamespace)[0]!.InnerText = "attacker@hybridcloudworks.com";

        var result = await Validate(document.OuterXml);

        Assert.False(result.IsValid);
        Assert.Equal("signature-reference-mismatch", result.ErrorCode);
    }

    [Fact]
    public async Task A_second_element_sharing_the_assertion_ID_is_rejected()
    {
        var spec = new AssertionSpec();
        var document = TestIdentityProvider.Load(_idp.SignedResponse(spec));
        var decoy = document.CreateElement("samlp", "Extensions", ProtocolNamespace);
        decoy.SetAttribute("ID", spec.AssertionId);
        document.DocumentElement!.InsertBefore(decoy, document.DocumentElement.FirstChild);

        var result = await Validate(document.OuterXml);

        Assert.False(result.IsValid);
        Assert.Equal("duplicate-id", result.ErrorCode);
    }

    [Theory]
    [InlineData(SignedXml.XmlDsigRSASHA1Url, SignedXml.XmlDsigSHA256Url)]
    [InlineData(SignedXml.XmlDsigRSASHA256Url, SignedXml.XmlDsigSHA1Url)]
    public async Task SHA1_signatures_and_digests_are_refused(string signatureMethod, string digestMethod)
    {
        var result = await Validate(_idp.SignedResponse(new AssertionSpec(), signatureMethod, digestMethod));

        Assert.False(result.IsValid);
        Assert.Equal("unsupported-signature-algorithm", result.ErrorCode);
    }

    [Fact]
    public async Task ValidateAssertion_ExpiredAssertion_FailsClosed()
    {
        var result = await Validate(_idp.SignedResponse(AssertionSpec.Expired()));

        Assert.False(result.IsValid);
        Assert.Equal("assertion-expired", result.ErrorCode);
    }

    [Fact]
    public async Task An_assertion_that_is_not_yet_valid_is_rejected()
    {
        var result = await Validate(_idp.SignedResponse(new AssertionSpec
        {
            NotBefore = DateTimeOffset.UtcNow.AddHours(1),
            NotOnOrAfter = DateTimeOffset.UtcNow.AddHours(2),
        }));

        Assert.False(result.IsValid);
        Assert.Equal("assertion-not-yet-valid", result.ErrorCode);
    }

    [Fact]
    public async Task ValidateAssertion_AudienceMismatch_FailsClosed()
    {
        var result = await Validate(_idp.SignedResponse(new AssertionSpec { Audience = "https://different-app.example.invalid/saml" }));

        Assert.False(result.IsValid);
        Assert.Equal("audience-mismatch", result.ErrorCode);
    }

    [Fact]
    public async Task An_assertion_without_an_audience_restriction_is_rejected()
    {
        var result = await Validate(_idp.SignedResponse(new AssertionSpec { IncludeAudienceRestriction = false }));

        Assert.False(result.IsValid);
        Assert.Equal("audience-mismatch", result.ErrorCode);
    }

    [Fact]
    public async Task An_assertion_addressed_to_another_recipient_is_rejected()
    {
        var result = await Validate(_idp.SignedResponse(new AssertionSpec { Recipient = "https://elsewhere.example.invalid/acs" }));

        Assert.False(result.IsValid);
        Assert.Equal("recipient-mismatch", result.ErrorCode);
    }

    [Fact]
    public async Task An_assertion_cannot_be_presented_twice()
    {
        var response = _idp.SignedResponse(new AssertionSpec());

        var first = await Validate(response);
        var second = await Validate(response);

        Assert.True(first.IsValid, first.ErrorMessage);
        Assert.False(second.IsValid);
        Assert.Equal("assertion-replayed", second.ErrorCode);
    }

    [Fact]
    public async Task ValidateAssertion_UnapprovedIdpIssuer_FailsClosed()
    {
        var result = await Validate(_idp.SignedResponse(new AssertionSpec { Issuer = "https://malicious-idp.example.invalid/saml" }));

        Assert.False(result.IsValid);
        Assert.Equal("idp-not-approved", result.ErrorCode);
    }

    [Fact]
    public async Task An_assertion_for_another_tenant_is_rejected()
    {
        var result = await _provider.ValidateAssertionResponseAsync(
            "tenant_other", _idp.SignedResponse(new AssertionSpec()), TestIdentityProvider.SpEntityId);

        Assert.False(result.IsValid);
        Assert.Equal("idp-not-approved", result.ErrorCode);
    }

    [Fact]
    public async Task ValidateAssertion_ConsumerEmailDomain_FailsClosed()
    {
        var result = await Validate(_idp.SignedResponse(new AssertionSpec { Email = "hybridcloudworks@gmail.com" }));

        Assert.False(result.IsValid);
        Assert.Equal("consumer-domain-prohibited", result.ErrorCode);
    }

    [Fact]
    public async Task An_assertion_answering_the_expected_request_is_accepted()
    {
        var result = await _provider.ValidateAssertionResponseAsync(
            TestIdentityProvider.TenantId,
            _idp.SignedResponse(new AssertionSpec { InResponseTo = "_request1" }),
            TestIdentityProvider.SpEntityId,
            expectedInResponseTo: "_request1");

        Assert.True(result.IsValid, result.ErrorMessage);
    }

    [Theory]
    [InlineData("_another-request")]
    [InlineData(null)]
    public async Task An_assertion_answering_another_request_or_none_is_rejected_when_one_is_expected(string? inResponseTo)
    {
        var result = await _provider.ValidateAssertionResponseAsync(
            TestIdentityProvider.TenantId,
            _idp.SignedResponse(new AssertionSpec { InResponseTo = inResponseTo }),
            TestIdentityProvider.SpEntityId,
            expectedInResponseTo: "_request1");

        Assert.False(result.IsValid);
        Assert.Equal("unsolicited-response", result.ErrorCode);
    }

    [Fact]
    public async Task InResponseTo_is_read_from_the_signed_assertion_not_the_response_envelope()
    {
        // The Response element is outside the signature; naming the request there must not help.
        var xml = _idp.Sign(TestIdentityProvider.UnsignedResponse(new AssertionSpec())).OuterXml
            .Replace("<samlp:Response ", "<samlp:Response InResponseTo=\"_request1\" ", StringComparison.Ordinal);

        var result = await _provider.ValidateAssertionResponseAsync(
            TestIdentityProvider.TenantId, xml, TestIdentityProvider.SpEntityId, expectedInResponseTo: "_request1");

        Assert.False(result.IsValid);
        Assert.Equal("unsolicited-response", result.ErrorCode);
    }

    [Fact]
    public async Task A_rejected_InResponseTo_does_not_consume_the_assertion()
    {
        var response = _idp.SignedResponse(new AssertionSpec { InResponseTo = "_request1" });

        var wrong = await _provider.ValidateAssertionResponseAsync(
            TestIdentityProvider.TenantId, response, TestIdentityProvider.SpEntityId, expectedInResponseTo: "_request2");
        var right = await _provider.ValidateAssertionResponseAsync(
            TestIdentityProvider.TenantId, response, TestIdentityProvider.SpEntityId, expectedInResponseTo: "_request1");

        Assert.Equal("unsolicited-response", wrong.ErrorCode);
        Assert.True(right.IsValid, right.ErrorMessage);
    }
}
