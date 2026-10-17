using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;

namespace LaPluma.CoreApi.Auth;

/// <summary>
/// The native-app sign-in contract (INT-22): SAML sign-in that hands the app a short-lived,
/// single-use code instead of a session token, which the app then exchanges with PKCE.
///
/// <list type="number">
/// <item><c>GET /auth/saml/login</c> takes the app's <c>redirect_uri</c> (exact match against
/// <see cref="RedirectUrisSetting"/>), an S256 <c>code_challenge</c>, and the app's own
/// <c>state</c>. They travel to the IdP and back inside a signed <c>RelayState</c>; nothing is
/// stored on the server, so any Cloud Run instance can finish a sign-in another one started.</item>
/// <item><c>POST /auth/saml/acs/{tenantId}</c> verifies the RelayState (signature, tenant,
/// expiry) before it touches the assertion, requires the assertion to answer the AuthnRequest that
/// RelayState belongs to (<c>InResponseTo</c>), and answers <c>302</c> to
/// <c>redirect_uri?code=…&amp;state=…</c>. A rejected assertion goes back as
/// <c>error=access_denied</c> with the same state.</item>
/// <item><c>POST /auth/token</c> exchanges the code and the PKCE <c>code_verifier</c> for the
/// session JWT. The JWT never appears in a URL.</item>
/// </list>
///
/// The code is AES-256-GCM authenticated ciphertext over the subject, email, tenant, roles,
/// code challenge, and redirect URI, valid for <see cref="CodeLifetime"/>. It is single-use per
/// instance: each instance remembers the codes it redeemed until they expire. Because the cache is
/// not shared, a code could in principle be redeemed once on each instance inside its lifetime, but
/// every redemption also needs the code verifier, which never leaves the app.
///
/// Both keys are derived from the session signing key (<see cref="SessionTokenKeys.DeriveKey"/>),
/// so there is no extra secret to provision. Rotating that key invalidates sign-ins in flight
/// (at most <see cref="TransactionLifetime"/>) and unredeemed codes; the user signs in again.
/// </summary>
public sealed partial class SignInFlow
{
    public const string RedirectUrisSetting = "SignIn:RedirectUris";

    /// <summary>The iOS app's ASWebAuthenticationSession callback.</summary>
    public const string DefaultRedirectUri = "lapluma://auth/callback";

    public const string ChallengeMethod = "S256";

    /// <summary>How long a user has to finish the IdP sign-in, MFA included.</summary>
    public static readonly TimeSpan TransactionLifetime = TimeSpan.FromMinutes(10);

    /// <summary>How long the app has to redeem the code it receives.</summary>
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromSeconds(60);

    private const byte RelayStateVersion = 1;
    private const int CodeVersion = 1;
    private const string CodePrefix = "lpc1.";
    private const int MaxRelayStateCharacters = 1024;
    private const int MaxCodeCharacters = 8192;
    private const int MaxRedirectUriLength = 200;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int ReplayCachePruneThreshold = 1024;

    private static readonly byte[] CodeAssociatedData = Encoding.ASCII.GetBytes("lapluma authorization code v1");

    private static readonly HashSet<string> ForbiddenRedirectSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "http", "javascript", "data", "file", "vbscript", "about", "blob", "ftp",
    };

    private static readonly JsonSerializerOptions CodeJson = new(JsonSerializerDefaults.General);

    private readonly byte[]? _relayKey;
    private readonly byte[]? _codeKey;
    private readonly IReadOnlyList<string> _redirectUris;
    private readonly TimeProvider _clock;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _redeemedCodes = new(StringComparer.Ordinal);

    /// <summary>
    /// Throws when a configured redirect URI is unusable, so a bad setting stops the host at
    /// startup rather than surfacing as failed sign-ins.
    /// </summary>
    public SignInFlow(SessionTokenKeys keys, IConfiguration configuration, TimeProvider clock)
    {
        _relayKey = keys.DeriveKey("lapluma sign-in relay state v1");
        _codeKey = keys.DeriveKey("lapluma sign-in authorization code v1");
        _clock = clock;
        _redirectUris = ReadRedirectUris(configuration);
    }

    /// <summary>False when no signing key is configured: sign-in then fails closed.</summary>
    public bool IsAvailable => _relayKey is not null && _codeKey is not null;

    public IReadOnlyList<string> RedirectUris => _redirectUris;

    public SignInResult<SignInRequest> ValidateLoginRequest(
        string? redirectUri, string? codeChallenge, string? codeChallengeMethod, string? state)
    {
        if (string.IsNullOrEmpty(redirectUri) || !_redirectUris.Contains(redirectUri, StringComparer.Ordinal))
        {
            return SignInResult<SignInRequest>.Failure(
                "redirect-uri-not-allowed", "redirect_uri is missing or is not registered for this service.");
        }

        if (!string.Equals(codeChallengeMethod, ChallengeMethod, StringComparison.Ordinal))
        {
            return SignInResult<SignInRequest>.Failure(
                "code-challenge-method-unsupported", "code_challenge_method must be S256.");
        }

        if (codeChallenge is null || !CodeChallengePattern().IsMatch(codeChallenge) || DecodeOrNull(codeChallenge) is not { Length: 32 })
        {
            return SignInResult<SignInRequest>.Failure(
                "code-challenge-invalid", "code_challenge must be the base64url SHA-256 of the code verifier, 43 characters.");
        }

        if (state is null || !StatePattern().IsMatch(state))
        {
            return SignInResult<SignInRequest>.Failure(
                "state-invalid", "state must be 16 to 64 characters from A-Z, a-z, 0-9, '-', '.', '_' and '~'.");
        }

        return SignInResult<SignInRequest>.Success(new SignInRequest(redirectUri, codeChallenge, state));
    }

    /// <summary>
    /// Seals a validated request into RelayState for <paramref name="tenantId"/> and derives the
    /// AuthnRequest ID from it, so the IdP's InResponseTo ties the assertion to this sign-in.
    /// </summary>
    public SamlRequestBinding Begin(SignInRequest request, string tenantId)
    {
        var relayKey = _relayKey ?? throw new InvalidOperationException("Sign-in is not configured.");
        var redirect = Encoding.UTF8.GetBytes(request.RedirectUri);
        var state = Encoding.ASCII.GetBytes(request.State);
        var challenge = WebEncoders.Base64UrlDecode(request.CodeChallenge);
        var expires = _clock.GetUtcNow().Add(TransactionLifetime).ToUnixTimeSeconds();

        var payload = new byte[1 + 8 + challenge.Length + 1 + redirect.Length + 1 + state.Length];
        var offset = 0;
        payload[offset++] = RelayStateVersion;
        BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(offset, 8), expires);
        offset += 8;
        challenge.CopyTo(payload, offset);
        offset += challenge.Length;
        payload[offset++] = (byte)redirect.Length;
        redirect.CopyTo(payload, offset);
        offset += redirect.Length;
        payload[offset++] = (byte)state.Length;
        state.CopyTo(payload, offset);

        var tag = RelayTag(relayKey, tenantId, payload);
        return new SamlRequestBinding(
            RequestId: AuthnRequestId(tag),
            RelayState: $"{WebEncoders.Base64UrlEncode(payload)}.{WebEncoders.Base64UrlEncode(tag)}");
    }

    /// <summary>
    /// Verifies RelayState posted back with an assertion. Checked before the assertion is
    /// validated, so a response that cannot finish a sign-in never consumes its assertion.
    /// </summary>
    public SignInResult<SignInTransaction> ReadRelayState(string? relayState, string tenantId)
    {
        if (string.IsNullOrEmpty(relayState))
        {
            return SignInResult<SignInTransaction>.Failure(
                "relay-state-missing", "Sign-in must start at /auth/saml/login; this response carries no sign-in state.");
        }

        if (_relayKey is not { } relayKey)
        {
            return SignInResult<SignInTransaction>.Failure("sign-in-unavailable", "Sign-in is not configured.");
        }

        var invalid = SignInResult<SignInTransaction>.Failure("relay-state-invalid", "The sign-in state is not valid for this service.");
        if (relayState.Length > MaxRelayStateCharacters)
        {
            return invalid;
        }

        var separator = relayState.IndexOf('.');
        if (separator <= 0 || separator != relayState.LastIndexOf('.'))
        {
            return invalid;
        }

        var payload = DecodeOrNull(relayState[..separator]);
        var tag = DecodeOrNull(relayState[(separator + 1)..]);
        if (payload is null || tag is null ||
            !CryptographicOperations.FixedTimeEquals(tag, RelayTag(relayKey, tenantId, payload)))
        {
            return invalid;
        }

        // Authentic from here on; the parse below only guards against a version this build does
        // not understand.
        if (payload.Length < 1 + 8 + 32 + 1 || payload[0] != RelayStateVersion)
        {
            return invalid;
        }

        var offset = 1;
        var expires = DateTimeOffset.FromUnixTimeSeconds(BinaryPrimitives.ReadInt64BigEndian(payload.AsSpan(offset, 8)));
        offset += 8;
        var challenge = WebEncoders.Base64UrlEncode(payload, offset, 32);
        offset += 32;
        var redirectLength = payload[offset++];
        if (offset + redirectLength + 1 > payload.Length)
        {
            return invalid;
        }

        var redirectUri = Encoding.UTF8.GetString(payload, offset, redirectLength);
        offset += redirectLength;
        var stateLength = payload[offset++];
        if (offset + stateLength != payload.Length)
        {
            return invalid;
        }

        var state = Encoding.ASCII.GetString(payload, offset, stateLength);
        if (_clock.GetUtcNow() >= expires)
        {
            return SignInResult<SignInTransaction>.Failure(
                "relay-state-expired", "The sign-in took too long. Start again from the app.");
        }

        // Re-checked so that removing a URI from the allowlist takes effect for sign-ins in flight.
        if (!_redirectUris.Contains(redirectUri, StringComparer.Ordinal))
        {
            return invalid;
        }

        return SignInResult<SignInTransaction>.Success(new SignInTransaction(
            TenantId: tenantId,
            RedirectUri: redirectUri,
            CodeChallenge: challenge,
            State: state,
            ExpiresAt: expires,
            AuthnRequestId: AuthnRequestId(tag)));
    }

    /// <summary>Mints the one-time code for a verified assertion.</summary>
    public string IssueCode(SignInTransaction transaction, SamlAssertion assertion)
    {
        var codeKey = _codeKey ?? throw new InvalidOperationException("Sign-in is not configured.");
        if (!string.Equals(transaction.TenantId, assertion.TenantId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The assertion belongs to a different tenant than the sign-in.");
        }

        var payload = new CodePayload(
            Version: CodeVersion,
            ExpiresAt: _clock.GetUtcNow().Add(CodeLifetime).ToUnixTimeSeconds(),
            Id: WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(16)),
            CodeChallenge: transaction.CodeChallenge,
            RedirectUri: transaction.RedirectUri,
            TenantId: assertion.TenantId,
            Subject: assertion.SubjectNameId,
            Email: assertion.Email,
            Roles: assertion.Roles.ToArray());

        var plaintext = JsonSerializer.SerializeToUtf8Bytes(payload, CodeJson);
        var sealedCode = new byte[NonceSize + plaintext.Length + TagSize];
        var nonce = sealedCode.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using (var aes = new AesGcm(codeKey, TagSize))
        {
            aes.Encrypt(
                nonce,
                plaintext,
                sealedCode.AsSpan(NonceSize, plaintext.Length),
                sealedCode.AsSpan(NonceSize + plaintext.Length, TagSize),
                CodeAssociatedData);
        }

        CryptographicOperations.ZeroMemory(plaintext);
        return CodePrefix + WebEncoders.Base64UrlEncode(sealedCode);
    }

    /// <summary>
    /// Redeems a code for the identity it carries. A failed attempt does not consume the code, so
    /// only a successful exchange (one that proved the verifier) uses it up.
    /// </summary>
    public SignInResult<SessionIdentity> RedeemCode(string? code, string? codeVerifier, string? redirectUri)
    {
        if (_codeKey is not { } codeKey)
        {
            return SignInResult<SessionIdentity>.Failure("sign-in-unavailable", "Sign-in is not configured.");
        }

        if (codeVerifier is null || !CodeVerifierPattern().IsMatch(codeVerifier))
        {
            return SignInResult<SessionIdentity>.Failure(
                "code-verifier-invalid", "code_verifier must be 43 to 128 characters from A-Z, a-z, 0-9, '-', '.', '_' and '~'.");
        }

        var invalid = SignInResult<SessionIdentity>.Failure("auth-code-invalid", "The sign-in code is not valid.");
        if (code is null || code.Length > MaxCodeCharacters || !code.StartsWith(CodePrefix, StringComparison.Ordinal) ||
            DecodeOrNull(code[CodePrefix.Length..]) is not { Length: > NonceSize + TagSize } sealedCode)
        {
            return invalid;
        }

        var plaintextLength = sealedCode.Length - NonceSize - TagSize;
        var plaintext = new byte[plaintextLength];
        CodePayload? payload;
        try
        {
            using (var aes = new AesGcm(codeKey, TagSize))
            {
                aes.Decrypt(
                    sealedCode.AsSpan(0, NonceSize),
                    sealedCode.AsSpan(NonceSize, plaintextLength),
                    sealedCode.AsSpan(NonceSize + plaintextLength, TagSize),
                    plaintext,
                    CodeAssociatedData);
            }

            payload = JsonSerializer.Deserialize<CodePayload>(plaintext, CodeJson);
        }
        catch (Exception error) when (error is CryptographicException or JsonException)
        {
            return invalid;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }

        if (payload is null || payload.Version != CodeVersion || string.IsNullOrEmpty(payload.Id) ||
            string.IsNullOrEmpty(payload.Subject) || string.IsNullOrEmpty(payload.TenantId))
        {
            return invalid;
        }

        var now = _clock.GetUtcNow();
        var expires = DateTimeOffset.FromUnixTimeSeconds(payload.ExpiresAt);
        if (now >= expires)
        {
            return SignInResult<SessionIdentity>.Failure("auth-code-expired", "The sign-in code has expired. Sign in again.");
        }

        if (!string.Equals(redirectUri, payload.RedirectUri, StringComparison.Ordinal))
        {
            return SignInResult<SessionIdentity>.Failure(
                "redirect-uri-mismatch", "redirect_uri does not match the one the sign-in started with.");
        }

        var expected = Encoding.ASCII.GetBytes(payload.CodeChallenge);
        var actual = Encoding.ASCII.GetBytes(WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier))));
        if (!CryptographicOperations.FixedTimeEquals(expected, actual))
        {
            return SignInResult<SessionIdentity>.Failure(
                "code-verifier-mismatch", "code_verifier does not match the code_challenge the sign-in started with.");
        }

        // Last, so a rejected attempt never uses the code up.
        if (!TryConsume(payload.Id, expires, now))
        {
            return SignInResult<SessionIdentity>.Failure("auth-code-replayed", "This sign-in code has already been used.");
        }

        return SignInResult<SessionIdentity>.Success(new SessionIdentity(
            Subject: payload.Subject,
            Email: string.IsNullOrEmpty(payload.Email) ? payload.Subject : payload.Email,
            TenantId: payload.TenantId,
            Roles: payload.Roles ?? Array.Empty<string>()));
    }

    /// <summary>The <c>302</c> target after a verified assertion.</summary>
    public static string CodeRedirect(SignInTransaction transaction, string code) =>
        QueryHelpers.AddQueryString(transaction.RedirectUri, new Dictionary<string, string?>
        {
            ["code"] = code,
            ["state"] = transaction.State,
        });

    /// <summary>
    /// The <c>302</c> target after a rejected assertion. Carries the problem code only — never the
    /// assertion, an email address, or any other detail.
    /// </summary>
    public static string ErrorRedirect(SignInTransaction transaction, string errorCode) =>
        QueryHelpers.AddQueryString(transaction.RedirectUri, new Dictionary<string, string?>
        {
            ["error"] = "access_denied",
            ["error_description"] = errorCode,
            ["state"] = transaction.State,
        });

    private bool TryConsume(string id, DateTimeOffset expiresAt, DateTimeOffset now)
    {
        if (_redeemedCodes.Count >= ReplayCachePruneThreshold)
        {
            foreach (var entry in _redeemedCodes)
            {
                if (entry.Value < now)
                {
                    _redeemedCodes.TryRemove(entry.Key, out _);
                }
            }
        }

        return _redeemedCodes.TryAdd(id, expiresAt);
    }

    private static byte[] RelayTag(byte[] relayKey, string tenantId, byte[] payload)
    {
        var context = Encoding.UTF8.GetBytes($"lapluma relay state|{tenantId.ToLowerInvariant()}|");
        var input = new byte[context.Length + payload.Length];
        context.CopyTo(input, 0);
        payload.CopyTo(input, context.Length);
        return HMACSHA256.HashData(relayKey, input);
    }

    /// <summary>
    /// An XML NCName (it starts with an underscore) derived from the RelayState tag, so the
    /// AuthnRequest ID is unpredictable and needs no storage.
    /// </summary>
    private static string AuthnRequestId(byte[] relayTag)
    {
        var input = new byte[relayTag.Length + 1];
        input[0] = 0x01;
        relayTag.CopyTo(input, 1);
        return "_" + Convert.ToHexStringLower(SHA256.HashData(input).AsSpan(0, 20));
    }

    private static byte[]? DecodeOrNull(string value)
    {
        try
        {
            return WebEncoders.Base64UrlDecode(value);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static IReadOnlyList<string> ReadRedirectUris(IConfiguration configuration)
    {
        var configured = configuration.GetSection(RedirectUrisSetting).GetChildren()
            .Select(child => child.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .ToArray();
        if (configured.Length == 0)
        {
            configured = [DefaultRedirectUri];
        }

        foreach (var value in configured)
        {
            if (value.Length > MaxRedirectUriLength ||
                value.Any(c => c is < '!' or > '~') ||
                !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
                ForbiddenRedirectSchemes.Contains(uri.Scheme) ||
                string.IsNullOrEmpty(uri.Host) ||
                !string.IsNullOrEmpty(uri.Fragment) ||
                !string.IsNullOrEmpty(uri.Query) ||
                !string.IsNullOrEmpty(uri.UserInfo))
            {
                throw new InvalidOperationException(
                    $"{RedirectUrisSetting} entries must be an https URL or an app's custom scheme URL with a host, " +
                    $"no query, fragment, or user info, printable ASCII only, and at most {MaxRedirectUriLength} characters.");
            }
        }

        return configured;
    }

    [GeneratedRegex(@"\A[A-Za-z0-9_-]{43}\z")]
    private static partial Regex CodeChallengePattern();

    [GeneratedRegex(@"\A[A-Za-z0-9._~-]{43,128}\z")]
    private static partial Regex CodeVerifierPattern();

    [GeneratedRegex(@"\A[A-Za-z0-9._~-]{16,64}\z")]
    private static partial Regex StatePattern();

    private sealed record CodePayload(
        [property: JsonPropertyName("v")] int Version,
        [property: JsonPropertyName("exp")] long ExpiresAt,
        [property: JsonPropertyName("jti")] string Id,
        [property: JsonPropertyName("cc")] string CodeChallenge,
        [property: JsonPropertyName("ru")] string RedirectUri,
        [property: JsonPropertyName("tid")] string TenantId,
        [property: JsonPropertyName("sub")] string Subject,
        [property: JsonPropertyName("email")] string Email,
        [property: JsonPropertyName("roles")] string[]? Roles);
}

/// <summary>What the app asked for when it started a sign-in.</summary>
public sealed record SignInRequest(string RedirectUri, string CodeChallenge, string State);

/// <summary>A verified RelayState: the sign-in an assertion has to finish.</summary>
public sealed record SignInTransaction(
    string TenantId,
    string RedirectUri,
    string CodeChallenge,
    string State,
    DateTimeOffset ExpiresAt,
    string AuthnRequestId);

/// <summary>The AuthnRequest ID and RelayState a SAML request carries for one sign-in.</summary>
public sealed record SamlRequestBinding(string RequestId, string? RelayState);

public sealed record SignInResult<T>(T? Value, string? ErrorCode, string? ErrorMessage)
    where T : class
{
    public bool IsValid => Value is not null;

    public static SignInResult<T> Success(T value) => new(value, null, null);

    public static SignInResult<T> Failure(string errorCode, string errorMessage) => new(null, errorCode, errorMessage);
}
