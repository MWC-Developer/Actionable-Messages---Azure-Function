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

using System.Net;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure;
using Azure.Data.Tables;
using FeedbackSample.Security;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace FeedbackSample
{
    /// <summary>
    /// Azure Function endpoints for submitting and querying actionable message feedback.
    /// </summary>
    public class FeedbackFunction
    {
        private const int MinRating = 1;
        private const int MaxRating = 5;
        private const int MaxCommentLength = 2048;

        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            NumberHandling = JsonNumberHandling.AllowReadingFromString
        };

        private readonly ILogger _logger;
        private readonly TableClient _tableClient;
        private readonly string? _expectedOriginatorId;
        private readonly IActionableMessageTokenValidator _tokenValidator;

        /// <summary>
        /// Creates a new feedback function handler configured with storage, logging, and token validation dependencies.
        /// </summary>
        public FeedbackFunction(ILoggerFactory loggerFactory, TableClient tableClient, IConfiguration configuration, IActionableMessageTokenValidator tokenValidator)
        {
            _logger = loggerFactory.CreateLogger<FeedbackFunction>();
            _tableClient = tableClient;
            _expectedOriginatorId = configuration["ActionableMessageOriginatorId"];
            _tokenValidator = tokenValidator;
        }

		/// <summary>
		/// Handles actionable message submissions and persists validated feedback in table storage.
		/// </summary>
		[Function("SubmitFeedback")]
		public async Task<HttpResponseData> Run([HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequestData req)
		{
			var invocationId = req.FunctionContext.InvocationId;
			var functionName = req.FunctionContext.FunctionDefinition?.Name ?? "SubmitFeedback";
			using var scope = _logger.BeginScope(new Dictionary<string, object>
			{
				["InvocationId"] = invocationId,
				["FunctionName"] = functionName
			});

			string? cardId = null;
			string? submittingUser = null;
			string? sender = null;

			_logger.LogInformation("Handling {FunctionName} invocation {InvocationId}.", functionName, invocationId);

			try
			{
				// Validate bearer token so only trusted actionable messages can submit.
				var (principal, validationError) = await ValidateTokenAsync(req, invocationId, "feedback submission").ConfigureAwait(false);
				if (validationError is not null)
				{
					return validationError;
				}

				var claimsPrincipal = principal!;

				submittingUser = claimsPrincipal.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? claimsPrincipal.FindFirst("sub")?.Value;
				sender = claimsPrincipal.FindFirst("sender")?.Value;

				FeedbackRequest? payload;
				try
				{
					payload = await JsonSerializer.DeserializeAsync<FeedbackRequest>(req.Body, JsonOptions).ConfigureAwait(false);
				}
				catch (JsonException ex)
				{
					_logger.LogWarning(ex, "Invalid JSON payload for {FunctionName} invocation {InvocationId}.", functionName, invocationId);
					return await CreateJsonResponse(req, HttpStatusCode.BadRequest, new { error = "Request body must be valid JSON." }).ConfigureAwait(false);
				}

				if (payload is null)
				{
					return await CreateJsonResponse(req, HttpStatusCode.BadRequest, new { error = "Request body is required." }).ConfigureAwait(false);
				}

				// Enforce allowed rating bounds before storing.
				if (!payload.Rating.HasValue || payload.Rating is < MinRating or > MaxRating)
				{
					return await CreateJsonResponse(req, HttpStatusCode.BadRequest, new { error = $"Rating must be between {MinRating} and {MaxRating}." }).ConfigureAwait(false);
				}

				// Prefer card id from body headers; this is the storage row key.
				cardId = GetCardId(req, payload);
				if (string.IsNullOrWhiteSpace(cardId))
				{
					return await CreateJsonResponse(req, HttpStatusCode.BadRequest, new { error = "Card identifier is required, but missing." }).ConfigureAwait(false);
				}

				// Originator validation prevents spoofed actionable message payloads.
				var originator = ExtractOriginatorId(payload);
				if (!string.IsNullOrWhiteSpace(_expectedOriginatorId) &&
					!string.Equals(_expectedOriginatorId, originator, StringComparison.OrdinalIgnoreCase))
				{
					_logger.LogWarning("Originator validation failed for card {CardId}.", cardId);
					return await CreateJsonResponse(req, HttpStatusCode.Unauthorized, new { error = "Invalid originator." }).ConfigureAwait(false);
				}

				// Normalize and limit optional free-text feedback to avoid storage abuse.
				var sanitizedComment = string.IsNullOrWhiteSpace(payload.Comment) ? null : payload.Comment!.Trim();
				if (sanitizedComment is { Length: > MaxCommentLength })
				{
					return await CreateJsonResponse(req, HttpStatusCode.BadRequest, new { error = $"Comments are limited to {MaxCommentLength} characters." }).ConfigureAwait(false);
				}

				var entity = new FeedbackEntity(cardId!)
				{
					Rating = payload.Rating.Value,
					Comment = sanitizedComment,
					SubmittedOn = DateTimeOffset.UtcNow,
					SubmittedBy = submittingUser
				};

				try
				{
					await _tableClient.AddEntityAsync(entity).ConfigureAwait(false);
				}
				catch (RequestFailedException ex) when (ex.Status == (int)HttpStatusCode.Conflict)
				{
					_logger.LogInformation("Duplicate feedback ignored for card {CardId} from sender {Sender}.", cardId, sender ?? "<unknown>");
					return await CreateCardResponse(req, HttpStatusCode.OK, CreateFeedbackReceivedCard(_expectedOriginatorId), "Feedback already received.").ConfigureAwait(false);
				}
				catch (RequestFailedException ex)
				{
					_logger.LogError(
						ex,
						"Storage failure while persisting feedback for card {CardId} during {FunctionName} invocation {InvocationId}.",
						cardId ?? "<unknown>",
						functionName,
						invocationId);
					return await CreateJsonResponse(req, HttpStatusCode.InternalServerError, new { error = "An unexpected storage error occurred while saving the feedback." }).ConfigureAwait(false);
				}

				_logger.LogInformation("Stored feedback for card {CardId} from {User} (sender {Sender}).", cardId, submittingUser ?? "unknown", sender ?? "unknown");

				return await CreateCardResponse(req, HttpStatusCode.OK, CreateFeedbackReceivedCard(_expectedOriginatorId), "Thank you for your feedback.").ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				_logger.LogError(
					ex,
					"Unhandled error while processing {FunctionName} invocation {InvocationId}. CardId: {CardId}. User: {User}. Sender: {Sender}.",
					functionName,
					invocationId,
					cardId ?? "<unknown>",
					submittingUser ?? "<unknown>",
					sender ?? "<unknown>");
				return await CreateJsonResponse(req, HttpStatusCode.InternalServerError, new { error = "An unexpected error occurred while processing the feedback." }).ConfigureAwait(false);
			}
			finally
			{
				_logger.LogInformation("Completed {FunctionName} invocation {InvocationId}.", functionName, invocationId);
			}
		}

		/// <summary>
		/// Returns an adaptive card or status headers that reflect whether feedback already exists for a card.
		/// </summary>
		[Function("GetFeedbackStatus")]
		public async Task<HttpResponseData> GetFeedbackStatus([HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequestData req)
		{
			var invocationId = req.FunctionContext.InvocationId;
			var functionName = req.FunctionContext.FunctionDefinition?.Name ?? "GetFeedbackStatus";
			using var scope = _logger.BeginScope(new Dictionary<string, object>
			{
				["InvocationId"] = invocationId,
				["FunctionName"] = functionName
			});

			string? cardId = null;
			_logger.LogInformation("Handling {FunctionName} invocation {InvocationId}.", functionName, invocationId);

			try
			{
				// Token validation ensures only trusted auto-invoke requests can query status.
				var (_, validationError) = await ValidateTokenAsync(req, invocationId, "feedback status request").ConfigureAwait(false);
				if (validationError is not null)
				{
					return validationError;
				}

				FeedbackRequest? payload;
				try
				{
					payload = await JsonSerializer.DeserializeAsync<FeedbackRequest>(req.Body, JsonOptions).ConfigureAwait(false);
				}
				catch (JsonException ex)
				{
					_logger.LogWarning(ex, "Invalid JSON payload for {FunctionName} invocation {InvocationId}.", functionName, invocationId);
					return await CreateJsonResponse(req, HttpStatusCode.BadRequest, new { error = "Request body must be valid JSON." }).ConfigureAwait(false);
				}

				cardId = GetCardId(req, payload);
				LogRefreshRequest(req, cardId);
				if (string.IsNullOrWhiteSpace(cardId))
				{
					return await CreateJsonResponse(req, HttpStatusCode.BadRequest, new { error = "Card identifier is required to evaluate status." }).ConfigureAwait(false);
				}

				// Match the originator in the payload with the configured id for extra trust.
				var originator = ExtractOriginatorId(payload);
				if (!string.IsNullOrWhiteSpace(_expectedOriginatorId) &&
					!string.Equals(_expectedOriginatorId, originator, StringComparison.OrdinalIgnoreCase))
				{
					_logger.LogWarning("Originator validation failed for card {CardId} during status check.", cardId);
					return await CreateJsonResponse(req, HttpStatusCode.Unauthorized, new { error = "Invalid originator." }).ConfigureAwait(false);
				}

				try
				{
					var entityResponse = await _tableClient.GetEntityIfExistsAsync<FeedbackEntity>("Feedback", cardId).ConfigureAwait(false);
					if (!entityResponse.HasValue)
					{
						_logger.LogInformation("No stored feedback found for card {CardId}. Returning no content for auto-invoke.", cardId);
						var noContentResponse = req.CreateResponse(HttpStatusCode.NoContent);
						noContentResponse.Headers.Add("CARD-ACTION-STATUS", "No feedback available yet.");
						return noContentResponse;
					}

					_logger.LogInformation("Card {CardId} already has feedback. Returning thank-you card.", cardId);
					return await CreateCardResponse(req, HttpStatusCode.OK, CreateFeedbackReceivedCard(_expectedOriginatorId), "Feedback already received.").ConfigureAwait(false);
				}
				catch (RequestFailedException ex)
				{
					_logger.LogError(
						ex,
						"Storage failure while reading feedback for card {CardId} during {FunctionName} invocation {InvocationId}.",
						cardId,
						functionName,
						invocationId);
					return await CreateJsonResponse(req, HttpStatusCode.InternalServerError, new { error = "An unexpected storage error occurred while reading the feedback." }).ConfigureAwait(false);
				}
			}
			catch (Exception ex)
			{
				_logger.LogError(
					ex,
					"Unhandled error while processing {FunctionName} invocation {InvocationId}. CardId: {CardId}.",
					functionName,
					invocationId,
					cardId ?? "<unknown>");
				return await CreateJsonResponse(req, HttpStatusCode.InternalServerError, new { error = "An unexpected error occurred while evaluating the feedback status." }).ConfigureAwait(false);
			}
			finally
			{
				_logger.LogInformation("Completed {FunctionName} invocation {InvocationId}.", functionName, invocationId);
			}
		}

		/// <summary>
		/// Validates any supplied bearer token, returning the resulting principal or an HTTP error response when missing or invalid.
		/// </summary>
		private async Task<(ClaimsPrincipal? Principal, HttpResponseData? ErrorResponse)> ValidateTokenAsync(HttpRequestData req, string invocationId, string missingTokenContext)
		{
			var bearerToken = GetBearerToken(req);
			if (string.IsNullOrWhiteSpace(bearerToken))
			{
				_logger.LogWarning("Missing bearer token on {Context}.", missingTokenContext);
				var response = await CreateJsonResponse(req, HttpStatusCode.Unauthorized, new { error = "Authorization header with bearer token is required." }).ConfigureAwait(false);
				return (null, response);
			}

			try
			{
				var principal = await _tokenValidator.ValidateAsync(bearerToken, req.FunctionContext.CancellationToken).ConfigureAwait(false);
				return (principal, null);
			}
			catch (SecurityTokenException ex)
			{
				_logger.LogWarning(ex, "Bearer token validation failed for invocation {InvocationId}.", invocationId);
				var response = await CreateJsonResponse(req, HttpStatusCode.Unauthorized, new { error = "Bearer token validation failed." }).ConfigureAwait(false);
				return (null, response);
			}
		}

		/// <summary>
		/// Extracts the actionable message card identifier from the body or from known headers.
		/// </summary>
		private static string? GetCardId(HttpRequestData req, FeedbackRequest? payload)
        {
			if (payload is not null && !string.IsNullOrWhiteSpace(payload.CardId))
            {
                return payload.CardId.Trim();
            }

            if (TryGetHeaderValue(req, "x-ms-card-instance-id", out var instanceId))
            {
                return instanceId;
            }

            if (TryGetHeaderValue(req, "x-ms-card-id", out var cardId))
            {
                return cardId;
            }

            return null;
        }

		/// <summary>
		/// Normalizes the optional originator id contained in the payload.
		/// </summary>
		private static string? ExtractOriginatorId(FeedbackRequest? payload) => payload is null || string.IsNullOrWhiteSpace(payload.OriginatorId)
			? null
			: payload.OriginatorId.Trim();

		/// <summary>
		/// Retrieves the first non-empty header value, trimming whitespace for downstream comparisons.
		/// </summary>
		private static bool TryGetHeaderValue(HttpRequestData req, string headerName, out string? value)
		{
			if (req.Headers.TryGetValues(headerName, out var values))
			{
				var first = values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
				if (first is not null)
				{
					value = first.Trim();
					return true;
				}
			}

			value = null;
			return false;
		}

		/// <summary>
		/// Emits structured logging for actionable message auto-invoke calls to aid diagnostics.
		/// </summary>
		private void LogRefreshRequest(HttpRequestData req, string? cardId)
		{
			_ = TryGetHeaderValue(req, "x-ms-card-action-type", out var actionType);
			_ = TryGetHeaderValue(req, "x-ms-card-action-tracking-id", out var trackingId);
			_ = TryGetHeaderValue(req, "x-ms-card-instance-id", out var instanceId);

			_logger.LogInformation(
				"Received actionable message refresh call. CardId: {CardId}. ActionType: {ActionType}. TrackingId: {TrackingId}. InstanceId: {InstanceId}.",
				cardId ?? "<unknown>",
				string.IsNullOrWhiteSpace(actionType) ? "<missing>" : actionType,
				string.IsNullOrWhiteSpace(trackingId) ? "<missing>" : trackingId,
				string.IsNullOrWhiteSpace(instanceId) ? "<missing>" : instanceId);
		}

		/// <summary>
		/// Minimal adaptive card representation returned to actionable message clients.
		/// </summary>
		private sealed class AdaptiveCardResponse
		{
			public string Type { get; } = "AdaptiveCard";

			[JsonPropertyName("$schema")]
			public string Schema { get; } = "http://adaptivecards.io/schemas/adaptive-card.json";

			public string Version { get; } = "1.0";

			public object[] Body { get; set; } = Array.Empty<object>();

			public string? FallbackText { get; set; }

			[JsonPropertyName("hideOriginalBody")]
			public bool HideOriginalBody { get; init; } = true;

			[JsonPropertyName("originator")]
			public string? Originator { get; set; }
		}

		/// <summary>
		/// Generates a simple thank-you adaptive card payload, optionally stamped with the originator id.
		/// </summary>
		private static AdaptiveCardResponse CreateFeedbackReceivedCard(string? originatorId) => new()
		{
			Body = new object[]
			{
				new
				{
					type = "TextBlock",
					text = "Thank you for your feedback.",
					wrap = true,
					weight = "Bolder"
				}
			},
			FallbackText = "Thank you for your feedback.",
			Originator = originatorId
		};

		/// <summary>
		/// Builds an HTTP response whose body contains an adaptive card, setting actionable message headers as needed.
		/// </summary>
		private static async Task<HttpResponseData> CreateCardResponse(HttpRequestData req, HttpStatusCode statusCode, AdaptiveCardResponse card, string? actionStatus = null)
		{
			var response = req.CreateResponse(statusCode);
			response.Headers.Add("CARD-UPDATE-IN-BODY", "true");
			if (!string.IsNullOrWhiteSpace(actionStatus))
			{
				response.Headers.Add("CARD-ACTION-STATUS", actionStatus);
			}
			await WriteJsonAsync(response, card).ConfigureAwait(false);
			return response;
		}

		/// <summary>
		/// Serializes <paramref name="payload"/> into a JSON HTTP response with common headers.
		/// </summary>
		private static async Task<HttpResponseData> CreateJsonResponse(HttpRequestData req, HttpStatusCode statusCode, object payload)
        {
            var response = req.CreateResponse(statusCode);
			await WriteJsonAsync(response, payload).ConfigureAwait(false);
            return response;
        }

		/// <summary>
		/// Writes JSON to the response stream using the shared serializer options.
		/// </summary>
		private static async Task WriteJsonAsync(HttpResponseData response, object payload)
		{
			response.Headers.Add("Content-Type", "application/json; charset=utf-8");
			await JsonSerializer.SerializeAsync(response.Body, payload, payload.GetType(), JsonOptions).ConfigureAwait(false);
		}

		/// <summary>
		/// Resolves a bearer token from either standard or actionable message authorization headers.
		/// </summary>
		private static string? GetBearerToken(HttpRequestData req)
        {
            if (TryGetHeaderValue(req, "Authorization", out var authorization))
            {
                var token = ExtractBearerToken(authorization);
                if (token is not null)
                {
                    return token;
                }
            }

            if (TryGetHeaderValue(req, "Action-Authorization", out var actionAuthorization))
            {
                var token = ExtractBearerToken(actionAuthorization);
                if (token is not null)
                {
                    return token;
                }
            }

            return null;
        }

		/// <summary>
		/// Removes the <c>Bearer</c> prefix from header values when present.
		/// </summary>
		private static string? ExtractBearerToken(string? headerValue)
        {
            if (string.IsNullOrWhiteSpace(headerValue))
            {
                return null;
            }

            const string bearerPrefix = "Bearer ";
            return headerValue.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase)
                ? headerValue[bearerPrefix.Length..].Trim()
                : null;
        }
    }

    /// <summary>
    /// Simple request payload describing incoming feedback.
    /// </summary>
    internal sealed record FeedbackRequest
    {
        [JsonPropertyName("CardId")]
        public string? CardId { get; init; }

        [JsonPropertyName("Rating")]
        public int? Rating { get; init; }

        [JsonPropertyName("Comment")]
        public string? Comment { get; init; }

        [JsonPropertyName("OriginatorId")]
        public string? OriginatorId { get; init; }
    }

    /// <summary>
    /// Table storage entity schema used for persisting feedback submissions.
    /// </summary>
    internal sealed class FeedbackEntity : ITableEntity
    {
        /// <summary>
        /// Initializes a new entity with the default partition for serialization.
        /// </summary>
        public FeedbackEntity()
        {
            PartitionKey = "Feedback";
            RowKey = string.Empty;
        }

        /// <summary>
        /// Initializes the entity using the actionable message card identifier as the row key.
        /// </summary>
        public FeedbackEntity(string cardId)
        {
            PartitionKey = "Feedback";
            RowKey = cardId;
        }

        public string PartitionKey { get; set; }

        public string RowKey { get; set; }

        public DateTimeOffset? Timestamp { get; set; }

        public ETag ETag { get; set; }

        public int Rating { get; set; }

        public string? Comment { get; set; }

        public DateTimeOffset SubmittedOn { get; set; }

        public string? SubmittedBy { get; set; }
    }
}
