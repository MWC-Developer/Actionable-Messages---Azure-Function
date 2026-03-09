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

using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace FeedbackSample.Security;

/// <summary>
/// Validates actionable message bearer tokens before allowing card submissions.
/// </summary>
public interface IActionableMessageTokenValidator
{
    Task<ClaimsPrincipal> ValidateAsync(string bearerToken, CancellationToken cancellationToken = default);
}

/// <summary>
/// Performs issuer, audience, and signing-key validation for actionable message tokens from Office 365 or Azure AD.
/// </summary>
public sealed class ActionableMessageTokenValidator : IActionableMessageTokenValidator
{
    // See https://learn.microsoft.com/en-us/outlook/actionable-messages/security-requirements#verifying-that-requests-come-from-microsoft

    private const string Issuer = "https://substrate.office.com/sts/";
    private const string MetadataAddress = "https://substrate.office.com/sts/common/.well-known/openid-configuration";
 
	private const string AzureAdIssuerPrefix = "https://sts.windows.net/";
	private const string DefaultAudiencePath = "api/SubmitFeedback";
	private readonly ConcurrentDictionary<string, IConfigurationManager<OpenIdConnectConfiguration>> _configurationManagers;
	private readonly string[] _validAudiences;
	private readonly string[] _validIssuers;
	private readonly HashSet<string> _allowedTenantIds;
	private readonly IReadOnlyDictionary<string, string?> _configurationSnapshot;
	private readonly string _configurationProvidersDescription;
	private readonly ILogger<ActionableMessageTokenValidator> _logger;
	private readonly JsonWebTokenHandler _tokenHandler = new();

	/// <summary>
	/// Creates a validator initialized with configuration-driven issuer and audience data.
	/// </summary>
	public ActionableMessageTokenValidator(IConfiguration configuration, ILogger<ActionableMessageTokenValidator> logger)
	{
		_logger = logger ?? throw new ArgumentNullException(nameof(logger));
		_validAudiences = ResolveAudiences(configuration) ?? throw new InvalidOperationException(
			"Actionable Message audience configuration is missing. Set 'ActionableMessageAudience' or 'ActionableMessageAudiences', or configure WEBSITE_HOSTNAME.");
		_allowedTenantIds = ResolveTenantIds(configuration);
		_validIssuers = ResolveIssuers(configuration);
		_configurationSnapshot = CaptureConfigurationSnapshot(configuration);
		_configurationProvidersDescription = DescribeConfigurationProviders(configuration);

		_configurationManagers = new ConcurrentDictionary<string, IConfigurationManager<OpenIdConnectConfiguration>>(StringComparer.OrdinalIgnoreCase);
		_configurationManagers.TryAdd(
			MetadataAddress,
			new ConfigurationManager<OpenIdConnectConfiguration>(
				MetadataAddress,
				new OpenIdConnectConfigurationRetriever(),
				new HttpDocumentRetriever { RequireHttps = true }));
	}

	/// <summary>
	/// Validates a bearer token and returns the resulting principal or throws a token validation exception.
	/// </summary>
	public async Task<ClaimsPrincipal> ValidateAsync(string bearerToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(bearerToken))
        {
            throw new ArgumentException("Bearer token is required.", nameof(bearerToken));
        }

     string[] tokenAudiences = Array.Empty<string>();
		try
		{
			// Read the token payload without validation to enhance diagnostic logs.
           JsonWebToken inspectedToken = _tokenHandler.ReadJsonWebToken(bearerToken);
			tokenAudiences = inspectedToken.Audiences?.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
				?? Array.Empty<string>();
           string tokenSubject = string.IsNullOrWhiteSpace(inspectedToken.Subject) ? "<null>" : inspectedToken.Subject;
			string tokenAudienceSummary = tokenAudiences.Length == 0 ? "<none>" : string.Join(", ", tokenAudiences);
			_logger.LogInformation("Incoming Actionable Message token subject {TokenSubject} includes audiences: {TokenAudiences}.", tokenSubject, tokenAudienceSummary);
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Unable to inspect Actionable Message token before validation.");
		}

		// Inspect the raw payload for issuer/tenant hints to select metadata endpoints.
      (string Issuer, string? TenantId)? tokenEnvelope = TryReadTokenEnvelope(bearerToken);
		string? tokenIssuer = tokenEnvelope?.Issuer;
		string? tokenTenantId = tokenEnvelope?.TenantId;

        HashSet<string> effectiveIssuers = new HashSet<string>(_validIssuers, StringComparer.OrdinalIgnoreCase);
		if (TryAugmentIssuersFromToken(bearerToken, effectiveIssuers, out string? inferredIssuer, tokenEnvelope))
		{
			_logger.LogInformation("Detected Azure AD issuer {Issuer} from the incoming Actionable Message token.", inferredIssuer);
		}

		_logger.LogInformation(
			"Validating Actionable Message token against {AudienceCount} configured audiences, {IssuerCount} issuers, and {TenantCount} allowed tenants.",
			_validAudiences.Length,
			effectiveIssuers.Count,
			_allowedTenantIds.Count);

		string metadataAddress = ResolveMetadataAddress(tokenIssuer, tokenTenantId);
		IConfigurationManager<OpenIdConnectConfiguration> configurationManager = GetConfigurationManager(metadataAddress);

		OpenIdConnectConfiguration configuration;
		try
		{
			configuration = await configurationManager.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			_logger.LogError(
				ex,
				"Unable to retrieve OpenID configuration from {MetadataAddress}. Config: {ConfigurationSnapshot}. Providers: {ConfigurationProviders}.",
				metadataAddress,
				BuildConfigurationSnapshotSummary(),
				_configurationProvidersDescription);
			throw;
		}

        int signingKeyCount = configuration.SigningKeys?.Count ?? 0;
		if (signingKeyCount == 0)
		{
			_logger.LogWarning("No signing keys are currently available from {MetadataAddress}. Token validation will likely fail.", metadataAddress);
		}

        TokenValidationParameters validationParameters = new TokenValidationParameters
		{
			ValidIssuers = effectiveIssuers,
			ValidateIssuer = true,
			ValidAudiences = _validAudiences,
			ValidateAudience = true,
			IssuerSigningKeys = configuration.SigningKeys,
			ValidateIssuerSigningKey = true,
			ValidateLifetime = true,
			ClockSkew = TimeSpan.FromMinutes(5)
		};

        TokenValidationResult result = await _tokenHandler
			.ValidateTokenAsync(bearerToken, validationParameters)
			.ConfigureAwait(false);

		if (!result.IsValid && result.Exception is SecurityTokenSignatureKeyNotFoundException)
		{
			_logger.LogWarning(
				"Token signing key mismatch detected. Refreshing OpenID configuration at {MetadataAddress}. Reason: {FailureReason}",
				metadataAddress,
				result.Exception.Message);
			configurationManager.RequestRefresh();
			configuration = await configurationManager.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
			validationParameters.IssuerSigningKeys = configuration.SigningKeys;
			result = await _tokenHandler
				.ValidateTokenAsync(bearerToken, validationParameters)
				.ConfigureAwait(false);
		}
       if (!result.IsValid || result.ClaimsIdentity is null)
        {
          string failureReason = result.Exception?.Message ?? "Token validation returned an empty ClaimsIdentity.";
			string failureType = result.Exception?.GetType().Name ?? nameof(SecurityTokenValidationException);
			string configuredAudiences = string.Join(", ", _validAudiences);
			string configuredIssuers = string.Join(", ", effectiveIssuers);
			string configurationSummary = BuildConfigurationSnapshotSummary();
			_logger.LogWarning(
				result.Exception,
				"Actionable Message token validation failed ({FailureType}). Reason: {FailureReason}. Issuers: {Issuers}. Audiences: {Audiences}. TokenAudiences: {TokenAudiences}. Config: {ConfigurationSnapshot}. Providers: {ConfigurationProviders}.",
				failureType,
				failureReason,
				configuredIssuers,
				configuredAudiences,
				string.Join(", ", tokenAudiences),
				configurationSummary,
				_configurationProvidersDescription);
			throw result.Exception ?? new SecurityTokenValidationException("Bearer token validation failed.");
        }

        return new ClaimsPrincipal(result.ClaimsIdentity);
    }

	/// <summary>
	/// Builds the list of acceptable audiences from explicit configuration or the Functions host name.
	/// </summary>
 private static string[]? ResolveAudiences(IConfiguration configuration)
	{
		List<string> rawAudiences = new List<string>();

		IConfigurationSection audienceSection = configuration.GetSection("ActionableMessageAudiences");
		if (audienceSection.Exists())
		{
			string[]? values = audienceSection.Get<string[]?>();
			if (values is { Length: > 0 })
			{
				rawAudiences.AddRange(values);
			}
		}

		string[]? explicitAudiences = ParseList(configuration["ActionableMessageAudiences"]) ??
			ParseList(configuration["ActionableMessageAudience"]);
		if (explicitAudiences is { Length: > 0 })
		{
			rawAudiences.AddRange(explicitAudiences);
		}

		if (rawAudiences.Count == 0)
		{
			string[]? hostAudiences = ResolveHostBasedAudiences(configuration);
			if (hostAudiences is null)
			{
				return null;
			}

			rawAudiences.AddRange(hostAudiences);
		}

		string[] normalizedAudiences = NormalizeAudiences(rawAudiences);
		return normalizedAudiences.Length == 0 ? null : normalizedAudiences;
	}

    /// <summary>
    /// Determines which issuers are accepted.  We allow the documented substrate issuer by default but also support explicit configuration.
    /// </summary>
    private static string[] ResolveIssuers(IConfiguration configuration)//, IEnumerable<string> tenantIds)
	{
        HashSet<string> issuers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		void AddIssuer(string? issuer)
		{
			if (string.IsNullOrWhiteSpace(issuer))
				return;

			issuers.Add(NormalizeIssuer(issuer));
		}

      IConfigurationSection issuerSection = configuration.GetSection("ActionableMessageIssuers");
		if (issuerSection.Exists())
		{
          string[]? values = issuerSection.Get<string[]?>();
			if (values is { Length: > 0 })
			{
               foreach (string value in values)
				{
					AddIssuer(value);
				}
			}
		}

      string[]? explicitIssuers = ParseList(configuration["ActionableMessageIssuers"]);
		if (explicitIssuers is { Length: > 0 })
		{
          foreach (string value in explicitIssuers)
			{
				AddIssuer(value);
			}
		}

		AddIssuer(configuration["ActionableMessageIssuer"]);

		// Add Substrate issuer
		AddIssuer(Issuer);

		return issuers.Count > 0 ? issuers.ToArray() : new[] { Issuer };
	}

	/// <summary>
	/// Reads the allowed tenant identifiers that can issue actionable message tokens.
	/// </summary>
   private static HashSet<string> ResolveTenantIds(IConfiguration configuration)
	{
        HashSet<string> tenants = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static void AddTenant(HashSet<string> target, string? candidate)
		{
			if (string.IsNullOrWhiteSpace(candidate))
			{
				return;
			}

            if (Guid.TryParse(candidate.Trim(), out Guid tenantGuid))
			{
				target.Add(tenantGuid.ToString("D"));
			}
		}

     IConfigurationSection tenantSection = configuration.GetSection("ActionableMessageTenantIds");
		if (tenantSection.Exists())
		{
            string[]? values = tenantSection.Get<string[]?>();
			if (values is { Length: > 0 })
			{
               foreach (string value in values)
				{
					AddTenant(tenants, value);
				}
			}
		}

      string[]? inlineTenantList = ParseList(configuration["ActionableMessageTenantIds"]);
		if (inlineTenantList is { Length: > 0 })
		{
         foreach (string value in inlineTenantList)
			{
				AddTenant(tenants, value);
			}
		}

        string[]? allowedTenantList = ParseList(configuration["ActionableMessageAllowedTenants"]);
		if (allowedTenantList is { Length: > 0 })
		{
            foreach (string value in allowedTenantList)
			{
				AddTenant(tenants, value);
			}
		}

		AddTenant(tenants, configuration["ActionableMessageTenantId"]);
		AddTenant(tenants, configuration["EntraId:TenantId"]);

		return tenants;
	}

	/// <summary>
	/// Adds the Azure AD issuer from the token when the tenant is explicitly allowed.
	/// </summary>
	private bool TryAugmentIssuersFromToken(
		string bearerToken,
		ISet<string> issuerSet,
		out string? inferredIssuer,
		(string Issuer, string? TenantId)? tokenEnvelope = null)
	{
		inferredIssuer = null;

      (string Issuer, string? TenantId)? envelope = tokenEnvelope ?? TryReadTokenEnvelope(bearerToken);
		if (envelope is null)
		{
			return false;
		}

     (string rawIssuer, string? tenantId) = envelope.Value;
		string normalizedIssuer = NormalizeIssuer(rawIssuer);
		if (issuerSet.Contains(normalizedIssuer))
		{
			return false;
		}

		if (!normalizedIssuer.StartsWith(AzureAdIssuerPrefix, StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}

        if (string.IsNullOrWhiteSpace(tenantId) || !Guid.TryParse(tenantId, out Guid tenantGuid))
		{
			_logger.LogWarning("Token issuer {Issuer} appears to be Azure AD, but it did not include a valid tenant id (tid) claim.", normalizedIssuer);
			return false;
		}

      string normalizedTenantId = tenantGuid.ToString("D");
		string expectedIssuer = $"{AzureAdIssuerPrefix}{normalizedTenantId}/";
		if (!string.Equals(normalizedIssuer, expectedIssuer, StringComparison.OrdinalIgnoreCase))
		{
			_logger.LogWarning("Token issuer {Issuer} does not match the tenant id claim {TenantId}.", normalizedIssuer, normalizedTenantId);
			return false;
		}

		if (_allowedTenantIds.Count > 0 && !_allowedTenantIds.Contains(normalizedTenantId))
		{
			_logger.LogWarning("Token tenant {TenantId} is not part of the configured Actionable Message tenant allow list.", normalizedTenantId);
			return false;
		}

		issuerSet.Add(expectedIssuer);
		inferredIssuer = expectedIssuer;
		return true;
	}

	/// <summary>
	/// Lightweight parsing of the JWT payload to capture issuer and tenant claims without signature validation.
	/// </summary>
	private static (string Issuer, string? TenantId)? TryReadTokenEnvelope(string bearerToken)
	{
		if (string.IsNullOrWhiteSpace(bearerToken))
		{
			return null;
		}

      string[] segments = bearerToken.Split('.');
		if (segments.Length < 2)
		{
			return null;
		}

		try
		{
           byte[] payloadBytes = Base64UrlEncoder.DecodeBytes(segments[1]);
			using JsonDocument document = JsonDocument.Parse(payloadBytes);

            if (!document.RootElement.TryGetProperty("iss", out JsonElement issuerProperty))
			{
				return null;
			}

            string? issuer = issuerProperty.GetString();
			if (string.IsNullOrWhiteSpace(issuer))
			{
				return null;
			}

			string? tenantId = null;
         if (document.RootElement.TryGetProperty("tid", out JsonElement tenantProperty))
			{
				tenantId = tenantProperty.GetString();
			}

			return (issuer, tenantId);
		}
		catch (Exception)
		{
			return null;
		}
	}

	/// <summary>
	/// Returns the OIDC metadata address appropriate for the token's issuer.
	/// Azure AD tokens are signed with keys published under the tenant-specific
	/// login.microsoftonline.com endpoint, not the Substrate STS endpoint.
	/// </summary>
	private static string ResolveMetadataAddress(string? tokenIssuer, string? tenantId)
	{
		if (string.IsNullOrWhiteSpace(tokenIssuer))
		{
			return MetadataAddress;
		}

		string normalizedIssuer = NormalizeIssuer(tokenIssuer);
		if (normalizedIssuer.StartsWith(AzureAdIssuerPrefix, StringComparison.OrdinalIgnoreCase)
			&& !string.IsNullOrWhiteSpace(tenantId)
			&& Guid.TryParse(tenantId, out Guid tenantGuid))
		{
			string normalizedTenantId = tenantGuid.ToString("D");
			return $"https://login.microsoftonline.com/{normalizedTenantId}/.well-known/openid-configuration";
		}

		return MetadataAddress;
	}

	/// <summary>
	/// Removes non-essential URI components to compare audiences consistently.
	/// </summary>
	private static string NormalizeAudience(Uri uri)
	{
		var builder = new UriBuilder(uri)
		{
			Fragment = string.Empty,
			Query = string.Empty
		};

		return builder.Uri.ToString().TrimEnd('/');
	}

	/// <summary>
	/// Ensures issuer strings are trimmed and end with a trailing slash.
	/// </summary>
	private static string NormalizeIssuer(string issuer)
	{
		var trimmed = issuer.Trim();
		return trimmed.EndsWith("/", StringComparison.Ordinal) ? trimmed : trimmed + "/";
	}

	/// <summary>
	/// Splits semicolon, comma, whitespace, or newline separated configuration values into trimmed entries.
	/// </summary>
	private static string[]? ParseList(string? raw) => string.IsNullOrWhiteSpace(raw)
		? null
		: raw.Split(new[] { ';', ',', ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

	/// <summary>
	/// Validates and normalizes audience URIs while ensuring https/api schemes and variant forms are included.
	/// </summary>
    private static string[] NormalizeAudiences(IEnumerable<string> rawAudiences)
	{
		HashSet<string> audiences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		foreach (string raw in rawAudiences)
		{
			if (string.IsNullOrWhiteSpace(raw))
			{
				continue;
			}

          if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out Uri uri))
			{
				throw new InvalidOperationException($"Actionable Message audience '{raw}' is not a valid absolute URL.");
			}

            bool isHttps = Uri.UriSchemeHttps.Equals(uri.Scheme, StringComparison.OrdinalIgnoreCase);
			bool isApiScheme = "api".Equals(uri.Scheme, StringComparison.OrdinalIgnoreCase);
			if (!isHttps && !isApiScheme)
			{
				throw new InvalidOperationException($"Actionable Message audience '{raw}' must use an HTTPS or api:// URL.");
			}

			AddAudienceVariants(audiences, uri);

         UriBuilder authorityBuilder = new UriBuilder(uri.Scheme, uri.Host, uri.IsDefaultPort ? -1 : uri.Port);
			AddAudienceVariants(audiences, authorityBuilder.Uri);
		}

		return audiences.ToArray();
	}

	/// <summary>
	/// Adds both full URIs and authority-only variants for comparison flexibility.
	/// </summary>
 private static void AddAudienceVariants(HashSet<string> audiences, Uri uri)
	{
		string normalizedAudience = NormalizeAudience(uri);
		AddAudienceVariants(audiences, normalizedAudience);
	}

	private static void AddAudienceVariants(HashSet<string> audiences, string? normalizedAudience)
	{
		if (string.IsNullOrWhiteSpace(normalizedAudience))
		{
			return;
		}

		audiences.Add(normalizedAudience);

		if (!normalizedAudience.EndsWith("/", StringComparison.Ordinal))
		{
			audiences.Add(normalizedAudience + "/");
		}
	}

	/// <summary>
	/// Derives default audiences based on the hosting environment when explicit settings are absent.
	/// </summary>
    private static string[]? ResolveHostBasedAudiences(IConfiguration configuration)
	{
		string? hostName = configuration["WEBSITE_HOSTNAME"];
		if (string.IsNullOrWhiteSpace(hostName))
		{
			return null;
		}

        string scheme = string.IsNullOrWhiteSpace(configuration["ActionableMessageAudienceScheme"])
			? "https"
			: configuration["ActionableMessageAudienceScheme"]!.Trim();
     string pathSetting = string.IsNullOrWhiteSpace(configuration["ActionableMessageAudiencePath"])
			? DefaultAudiencePath
			: configuration["ActionableMessageAudiencePath"]!.Trim();

     List<string> audiences = new List<string>();
		UriBuilder baseBuilder = new UriBuilder(scheme, hostName);
		audiences.Add(baseBuilder.Uri.ToString());

     string normalizedPath = pathSetting.Trim('/');
		if (!string.IsNullOrWhiteSpace(normalizedPath))
		{
           UriBuilder pathBuilder = new UriBuilder(baseBuilder.Uri)
			{
				Path = normalizedPath
			};
			audiences.Add(pathBuilder.Uri.ToString());
		}

		return audiences.ToArray();
	}

	/// <summary>
	/// Captures selected configuration keys for future troubleshooting logs.
	/// </summary>
  private static IReadOnlyDictionary<string, string?> CaptureConfigurationSnapshot(IConfiguration configuration)
	{
		Dictionary<string, string?> snapshot = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

		static string? FormatListValue(string[]? values) => values is not { Length: > 0 }
			? null
			: string.Join(", ", values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()));

		void AddValue(string key, string? value) => snapshot[key] = value;
     void AddSectionArray(string sectionKey)
		{
         string[]? values = configuration.GetSection(sectionKey).Get<string[]?>();
			AddValue($"{sectionKey}[]", FormatListValue(values));
		}

     string[] trackedKeys = new[]
		{
			"ActionableMessageAudiences",
			"ActionableMessageAudience",
			"ActionableMessageAudienceScheme",
			"ActionableMessageAudiencePath",
			"ActionableMessageIssuer",
			"ActionableMessageIssuers",
			"ActionableMessageTenantId",
			"ActionableMessageTenantIds",
			"ActionableMessageAllowedTenants",
			"ActionableMessageOriginatorId",
			"WEBSITE_HOSTNAME"
		};

        foreach (string key in trackedKeys)
		{
			AddValue(key, configuration[key]);
		}

		AddSectionArray("ActionableMessageAudiences");
		AddSectionArray("ActionableMessageIssuers");
		AddSectionArray("ActionableMessageTenantIds");

		return snapshot;
	}

	/// <summary>
	/// Produces a textual list of configuration providers backing the current options.
	/// </summary>
	private static string DescribeConfigurationProviders(IConfiguration configuration)
	{
		return configuration is IConfigurationRoot root
			? string.Join(", ", root.Providers.Select(p => p.ToString()))
			: "Unavailable";
	}

	/// <summary>
	/// Retrieves or creates a cached OpenID Connect configuration manager bound to the metadata endpoint.
	/// </summary>
	private IConfigurationManager<OpenIdConnectConfiguration> GetConfigurationManager(string metadataAddress)
	{
		return _configurationManagers.GetOrAdd(
			metadataAddress,
			address => new ConfigurationManager<OpenIdConnectConfiguration>(
				address,
				new OpenIdConnectConfigurationRetriever(),
				new HttpDocumentRetriever { RequireHttps = true }));
	}

	/// <summary>
	/// Formats the captured configuration snapshot for structured log output.
	/// </summary>
	private string BuildConfigurationSnapshotSummary()
	{
		return _configurationSnapshot.Count == 0
			? "No tracked configuration entries."
			: string.Join("; ", _configurationSnapshot.Select(kvp => $"{kvp.Key}='{kvp.Value ?? "<null>"}'"));
	}
}
