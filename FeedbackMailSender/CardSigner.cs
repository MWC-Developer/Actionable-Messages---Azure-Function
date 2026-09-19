/*
 * By David Barrett, Microsoft Ltd. Use at your own risk.  No warranties are given.
 * 
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 * AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 * OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
 * THE SOFTWARE.
 * */

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FeedbackMailSender;

/// <summary>
/// Signs adaptive card payloads as compact JWS tokens (RS256) for signed-card sender verification.
/// See: https://learn.microsoft.com/en-us/outlook/actionable-messages/security-requirements#signed-card-payloads
/// </summary>
internal sealed class CardSigner
{
    private static readonly JsonSerializerOptions PayloadSerializerOptions = new()
    {
        WriteIndented = false
    };

    // Fixed RS256/JWT header — encoded once at startup.
    private static readonly string EncodedHeader =
        Base64UrlEncode(Encoding.UTF8.GetBytes("""{"alg":"RS256","typ":"JWT"}"""));

    private readonly GraphMailOptions _options;

    /// <summary>
    /// Initializes the signer with the mail options that supply the certificate
    /// thumbprint, originator ID, and sender e-mail address.
    /// </summary>
    public CardSigner(GraphMailOptions options)
    {
        _options = options;
    }

    /// <summary>
    /// Builds the compact JWS string (header.payload.signature) that represents
    /// the signed adaptive card payload.
    /// </summary>
    /// <param name="adaptiveCardJson">The serialised Adaptive Card JSON string.</param>
    /// <param name="recipients">All To/CC recipient e-mail addresses for the outgoing message.</param>
    /// <returns>The compact JWS token to embed inside the SignedAdaptiveCard HTML section.</returns>
    public string CreateSignedPayload(string adaptiveCardJson, string[] recipients)
    {
        long issuedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string recipientsSerialized = JsonSerializer.Serialize(recipients, PayloadSerializerOptions);

        SignedCardClaims claims = new(
            Sender: _options.SenderUserId!,
            Originator: _options.OriginatorId!,
            RecipientsSerialized: recipientsSerialized,
            AdaptiveCardSerialized: adaptiveCardJson,
            IssuedAt: issuedAt);

        string payloadJson = JsonSerializer.Serialize(claims, PayloadSerializerOptions);
        string encodedPayload = Base64UrlEncode(Encoding.UTF8.GetBytes(payloadJson));

        string signingInput = $"{EncodedHeader}.{encodedPayload}";

        X509Certificate2 certificate = LoadCertificate(_options.SigningCertificateThumbprint!);
        using RSA rsa = certificate.GetRSAPrivateKey()
            ?? throw new InvalidOperationException(
                $"Certificate '{_options.SigningCertificateThumbprint}' does not have an accessible RSA private key.");

        byte[] signatureBytes = rsa.SignData(
            Encoding.UTF8.GetBytes(signingInput),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        string encodedSignature = Base64UrlEncode(signatureBytes);
        return $"{signingInput}.{encodedSignature}";
    }

    /// <summary>
    /// Opens <c>CurrentUser\My</c> and returns the first certificate matching <paramref name="thumbprint"/>.
    /// </summary>
    private static X509Certificate2 LoadCertificate(string thumbprint)
    {
        using X509Store store = new(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);

        X509Certificate2Collection matches = store.Certificates.Find(
            X509FindType.FindByThumbprint,
            thumbprint,
            validOnly: false);

        if (matches.Count == 0)
        {
            throw new InvalidOperationException(
                $"No certificate with thumbprint '{thumbprint}' was found in the CurrentUser\\My store.");
        }

        return matches[0];
    }

    /// <summary>
    /// Encodes bytes using Base64Url (RFC 4648 §5) without padding, as required by the JWS compact serialisation.
    /// </summary>
    private static string Base64UrlEncode(byte[] input) =>
        Convert.ToBase64String(input)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    // -------------------------------------------------------------------------
    // Private model used only for JWS payload serialisation.
    // -------------------------------------------------------------------------

    private sealed record SignedCardClaims(
        [property: JsonPropertyName("sender")]                 string Sender,
        [property: JsonPropertyName("originator")]             string Originator,
        [property: JsonPropertyName("recipientsSerialized")]   string RecipientsSerialized,
        [property: JsonPropertyName("adaptiveCardSerialized")] string AdaptiveCardSerialized,
        [property: JsonPropertyName("iat")]                    long IssuedAt);
}
