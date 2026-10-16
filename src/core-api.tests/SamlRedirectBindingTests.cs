using System.IO.Compression;
using System.Text;
using System.Xml;
using LaPluma.CoreApi.Auth;
using Microsoft.AspNetCore.WebUtilities;
using Xunit;

namespace LaPluma.CoreApi.Tests;

/// <summary>
/// The sign-in redirect follows the SAML HTTP-Redirect binding: SAMLRequest is the AuthnRequest,
/// raw-DEFLATE compressed, Base64-encoded and URL-encoded, added to the IdP's SSO URL without
/// disturbing the query that URL already carries (Google Workspace's holds <c>?idpid=</c>).
/// </summary>
public sealed class SamlRedirectBindingTests : IDisposable
{
    private const string ProtocolNamespace = "urn:oasis:names:tc:SAML:2.0:protocol";
    private const string AssertionNamespace = "urn:oasis:names:tc:SAML:2.0:assertion";
    private const string GoogleStyleSsoUrl = "https://idp.example.invalid/google/sso?idpid=C03hcwks";

    private readonly TestIdentityProvider _idp = new();

    public void Dispose() => _idp.Dispose();

    private Task<SamlAuthnRequest> SignInThrough(string ssoServiceUrl)
    {
        var provider = new SamlServiceProvider(
            TestIdentityProvider.SpEntityId,
            TestIdentityProvider.AcsBaseUrl,
            [_idp.Configuration() with { SsoServiceUrl = ssoServiceUrl }]);
        return provider.CreateAuthnRequestAsync("hybridcloudworks.com", SamlIdpType.GoogleWorkspace);
    }

    [Fact]
    public async Task An_sso_url_that_already_has_a_query_gets_SAMLRequest_joined_with_an_ampersand()
    {
        var request = await SignInThrough(GoogleStyleSsoUrl);

        Assert.StartsWith($"{GoogleStyleSsoUrl}&SAMLRequest=", request.RedirectUrl);
        Assert.Single(request.RedirectUrl, '?');

        var query = QueryHelpers.ParseQuery(new Uri(request.RedirectUrl).Query);
        Assert.Equal("C03hcwks", query["idpid"].ToString());
        Assert.Single(query["SAMLRequest"]);
    }

    [Fact]
    public async Task An_sso_url_without_a_query_gets_SAMLRequest_after_a_question_mark()
    {
        var ssoUrl = $"{TestIdentityProvider.GoogleEntityId}/sso";

        var request = await SignInThrough(ssoUrl);

        Assert.StartsWith($"{ssoUrl}?SAMLRequest=", request.RedirectUrl);
        Assert.Single(request.RedirectUrl, '?');
        Assert.DoesNotContain('&', request.RedirectUrl);
    }

    [Theory]
    [InlineData("https://idp.example.invalid/google/sso")]
    [InlineData(GoogleStyleSsoUrl)]
    [InlineData("https://idp.example.invalid/google/sso?idpid=C03hcwks&hl=en")]
    public async Task SAMLRequest_inflates_back_to_the_AuthnRequest_that_was_issued(string ssoUrl)
    {
        var request = await SignInThrough(ssoUrl);

        var samlRequest = QueryHelpers.ParseQuery(new Uri(request.RedirectUrl).Query)["SAMLRequest"].ToString();
        var document = Load(RawInflate(Convert.FromBase64String(samlRequest)));

        var root = document.DocumentElement!;
        Assert.Equal("AuthnRequest", root.LocalName);
        Assert.Equal(ProtocolNamespace, root.NamespaceURI);
        Assert.Equal(request.Id, root.GetAttribute("ID"));
        Assert.Equal(ssoUrl, root.GetAttribute("Destination"));
        Assert.Equal(TestIdentityProvider.AcsUrl, root.GetAttribute("AssertionConsumerServiceURL"));
        Assert.Equal("urn:oasis:names:tc:SAML:2.0:bindings:HTTP-POST", root.GetAttribute("ProtocolBinding"));
        Assert.Equal("2.0", root.GetAttribute("Version"));
        Assert.EndsWith("Z", root.GetAttribute("IssueInstant"));

        var issuer = Assert.Single(root.GetElementsByTagName("Issuer", AssertionNamespace).Cast<XmlElement>());
        Assert.Equal(TestIdentityProvider.SpEntityId, issuer.InnerText);

        Assert.Equal(request.Destination, root.GetAttribute("Destination"));
        Assert.Equal(request.AssertionConsumerServiceUrl, root.GetAttribute("AssertionConsumerServiceURL"));
    }

    /// <summary>RFC 1951 inflate with no zlib or gzip framing: what the binding prescribes and what an IdP runs.</summary>
    private static string RawInflate(byte[] compressed)
    {
        using var input = new MemoryStream(compressed);
        using var inflate = new DeflateStream(input, CompressionMode.Decompress);
        using var reader = new StreamReader(inflate, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static XmlDocument Load(string xml)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        using var reader = XmlReader.Create(new StringReader(xml), settings);
        var document = new XmlDocument { XmlResolver = null };
        document.Load(reader);
        return document;
    }
}
