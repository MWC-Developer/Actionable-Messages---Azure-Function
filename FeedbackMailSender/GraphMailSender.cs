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

using System.Net.Http.Headers;
using System.Text;

namespace FeedbackMailSender;

/// <summary>
/// Sends Graph sendMail requests containing the adaptive feedback card.
/// </summary>
internal sealed class GraphMailSender
{
    private const string GraphBaseUrl = "https://graph.microsoft.com/v1.0";

    private readonly HttpClient _httpClient;
    private readonly TokenProvider _tokenProvider;
    private readonly GraphMailPayloadFactory _payloadFactory;
    private readonly GraphMailOptions _mailOptions;
    private readonly EntraIdOptions _entraOptions;

    /// <summary>
    /// Provides the dependencies needed to create payloads and acquire tokens.
    /// </summary>
    public GraphMailSender(HttpClient httpClient, TokenProvider tokenProvider, GraphMailPayloadFactory payloadFactory, GraphMailOptions mailOptions, EntraIdOptions entraOptions)
    {
        _httpClient = httpClient;
        _tokenProvider = tokenProvider;
        _payloadFactory = payloadFactory;
        _mailOptions = mailOptions;
        _entraOptions = entraOptions;
    }

    /// <summary>
    /// Sends the actionable message and returns the card id so callers can correlate submissions.
    /// </summary>
    public async Task<GraphMailRequest> SendMailAsync(CancellationToken cancellationToken)
    {
        var requestPayload = _payloadFactory.CreatePayload();
        var accessToken = await _tokenProvider.GetTokenAsync(cancellationToken).ConfigureAwait(false);
        var endpoint = BuildEndpoint();

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(requestPayload.Payload, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var details = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException($"Graph sendMail failed: {(int)response.StatusCode} {response.ReasonPhrase}. {details}", null, response.StatusCode);
        }

        return requestPayload;
    }

    /// <summary>
    /// Selects the correct Graph sendMail endpoint for the configured authentication mode.
    /// </summary>
    private string BuildEndpoint()
    {
        if (_entraOptions.AuthMode == EntraIdAuthMode.Application)
        {
            var sender = Uri.EscapeDataString(_mailOptions.SenderUserId!);
            return $"{GraphBaseUrl}/users/{sender}/sendMail";
        }

        return $"{GraphBaseUrl}/me/sendMail";
    }
}
