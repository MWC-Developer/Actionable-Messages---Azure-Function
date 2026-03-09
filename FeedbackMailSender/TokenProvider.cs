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

using System.Diagnostics;
using Microsoft.Identity.Client;
using TextCopy;

namespace FeedbackMailSender;

/// <summary>
/// Acquires Microsoft Graph access tokens using either delegated or application permissions.
/// </summary>
internal sealed class TokenProvider
{
    private readonly EntraIdOptions _options;
    private readonly IPublicClientApplication? _publicClient;
    private readonly IConfidentialClientApplication? _confidentialClient;

    /// <summary>
    /// Builds the appropriate MSAL client based on configuration at startup.
    /// </summary>
    public TokenProvider(EntraIdOptions options)
    {
        _options = options;

        if (options.AuthMode == EntraIdAuthMode.Delegated)
        {
            _publicClient = PublicClientApplicationBuilder
                .Create(options.ClientId!)
                .WithAuthority(options.Authority)
                .WithDefaultRedirectUri()
                .Build();
        }
        else
        {
            _confidentialClient = ConfidentialClientApplicationBuilder
                .Create(options.ClientId!)
                .WithAuthority(options.Authority)
                .WithClientSecret(options.ClientSecret!)
                .Build();
        }
    }

    /// <summary>
    /// Gets an access token using the configured authentication flow.
    /// </summary>
    public Task<string> GetTokenAsync(CancellationToken cancellationToken) => _options.AuthMode == EntraIdAuthMode.Delegated
        ? GetDelegatedTokenAsync(cancellationToken)
        : GetApplicationTokenAsync(cancellationToken);

	/// <summary>
	/// Uses the device code flow (with silent token reuse when possible) for delegated scenarios.
	/// </summary>
	private async Task<string> GetDelegatedTokenAsync(CancellationToken cancellationToken)
    {
        var scopes = _options.DelegatedScopes?.Length > 0 ? _options.DelegatedScopes! : EntraIdOptions.DefaultDelegatedScopes;
        var accounts = await _publicClient!.GetAccountsAsync().ConfigureAwait(false);

        if (accounts.Any())
        {
            try
            {
                var silentResult = await _publicClient
                    .AcquireTokenSilent(scopes, accounts.First())
                    .ExecuteAsync(cancellationToken)
                    .ConfigureAwait(false);
                return silentResult.AccessToken;
            }
            catch (MsalUiRequiredException)
            {
                // Fall back to an interactive device code prompt when the cache is insufficient.
            }
        }

        var result = await _publicClient
            .AcquireTokenWithDeviceCode(scopes, deviceCodeResult =>
            {
                Console.WriteLine(deviceCodeResult.Message);
                TryCopyDeviceCode(deviceCodeResult.UserCode);
                TryLaunchVerificationUri(deviceCodeResult.VerificationUrl);
                return Task.CompletedTask;
            })
            .ExecuteAsync(cancellationToken)
            .ConfigureAwait(false);

        return result.AccessToken;
    }

	/// <summary>
	/// Uses the client credentials flow for application permissions.
	/// </summary>
	private async Task<string> GetApplicationTokenAsync(CancellationToken cancellationToken)
    {
        var scopes = _options.ApplicationScopes?.Length > 0 ? _options.ApplicationScopes! : EntraIdOptions.DefaultApplicationScopes;
        var result = await _confidentialClient!
            .AcquireTokenForClient(scopes)
            .ExecuteAsync(cancellationToken)
            .ConfigureAwait(false);
        return result.AccessToken;
    }

	/// <summary>
	/// Copies the MSAL device code to the clipboard when available.
	/// </summary>
	private static void TryCopyDeviceCode(string? userCode)
    {
        if (string.IsNullOrWhiteSpace(userCode))
        {
            return;
        }

        try
        {
            ClipboardService.SetText(userCode);
            Console.WriteLine("Device code copied to clipboard.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Unable to copy device code to the clipboard: {ex.Message}");
        }
    }

	/// <summary>
	/// Attempts to open the browser to the verification URI required to complete device code auth.
	/// </summary>
	private static void TryLaunchVerificationUri(string? verificationUri)
    {
        if (string.IsNullOrWhiteSpace(verificationUri))
        {
            return;
        }

        try
        {
            // UseShellExecute launches the default browser on all supported platforms.
            var startInfo = new ProcessStartInfo
            {
                FileName = verificationUri,
                UseShellExecute = true
            };

            Process.Start(startInfo);
            Console.WriteLine("Opening the browser to complete authentication...");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Unable to open the authentication URL automatically: {ex.Message}");
        }
    }
}
