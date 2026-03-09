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

namespace FeedbackMailSender;

/// <summary>
/// Strongly typed configuration describing how to authenticate against Microsoft Entra ID.
/// </summary>
internal sealed class EntraIdOptions
{
    public const string SectionName = "EntraId";

    internal static readonly string[] DefaultDelegatedScopes = new[] { "Mail.Send" };
    internal static readonly string[] DefaultApplicationScopes = new[] { "https://graph.microsoft.com/.default" };

    public string? TenantId { get; set; }

    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    public EntraIdAuthMode AuthMode { get; set; } = EntraIdAuthMode.Delegated;

    public string[]? DelegatedScopes { get; set; } = DefaultDelegatedScopes;

    public string[]? ApplicationScopes { get; set; } = DefaultApplicationScopes;

    /// <summary>
    /// Trims string values and replaces empty scope arrays with defaults.
    /// </summary>
    public void Normalize()
    {
        TenantId = NormalizeString(TenantId);
        ClientId = NormalizeString(ClientId);
        ClientSecret = NormalizeString(ClientSecret);
        DelegatedScopes = NormalizeScopes(DelegatedScopes, DefaultDelegatedScopes);
        ApplicationScopes = NormalizeScopes(ApplicationScopes, DefaultApplicationScopes);
    }

    /// <summary>
    /// Ensures required identifiers and secrets are provided for the selected auth mode.
    /// </summary>
    public void EnsureValid()
    {
        if (string.IsNullOrWhiteSpace(TenantId))
        {
            throw new InvalidOperationException("EntraId:TenantId must be provided.");
        }

        if (string.IsNullOrWhiteSpace(ClientId))
        {
            throw new InvalidOperationException("EntraId:ClientId must be provided.");
        }

        if (AuthMode == EntraIdAuthMode.Application && string.IsNullOrWhiteSpace(ClientSecret))
        {
            throw new InvalidOperationException("EntraId:ClientSecret is required when using application permissions.");
        }
    }

    /// <summary>
    /// Returns either the fully qualified authority or constructs a tenant-specific login URL.
    /// </summary>
    public string Authority => TenantId!.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        ? TenantId!
        : $"https://login.microsoftonline.com/{TenantId}";

    private static string? NormalizeString(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string[] NormalizeScopes(string[]? scopes, string[] defaults) => scopes is { Length: > 0 }
        ? scopes.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
        : defaults;
}
