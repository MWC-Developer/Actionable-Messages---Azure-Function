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
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FeedbackMailSender;

/// <summary>
/// Builds Graph sendMail payloads that embed actionable feedback adaptive cards.
/// When a <see cref="CardSigner"/> is provided the card is embedded as a
/// SignedAdaptiveCard (JWS) section instead of a plain script tag.
/// </summary>
internal sealed class GraphMailPayloadFactory
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    private readonly GraphMailOptions _options;
    private readonly CardSigner? _signer;

    /// <summary>
    /// Initializes the factory with the current mail composition options and an optional card signer.
    /// </summary>
    /// <param name="options">Mail composition options.</param>
    /// <param name="signer">
    /// When provided, cards are signed and embedded as a SignedAdaptiveCard.
    /// Pass <see langword="null"/> to use plain script-tag embedding (DKIM/SPF required).
    /// </param>
    public GraphMailPayloadFactory(GraphMailOptions options, CardSigner? signer = null)
    {
        _options = options;
        _signer = signer;
    }

    /// <summary>
    /// Creates a unique card id and corresponding Graph payload ready for submission.
    /// </summary>
    public GraphMailRequest CreatePayload()
    {
        var cardId = $"feedback-{Guid.NewGuid():N}";
        var adaptiveCardJson = BuildAdaptiveCardJson(cardId);
        var htmlBody = BuildHtmlBody(adaptiveCardJson);

        var request = new GraphSendMailPayload
        {
            Message = new GraphMessage
            {
                Subject = _options.Subject,
                Body = new GraphItemBody
                {
                    ContentType = "HTML",
                    Content = htmlBody
                },
                ToRecipients = _options.Recipients
                    .Select(address => new GraphRecipient
                    {
                        EmailAddress = new GraphEmailAddress { Address = address }
                    })
                    .ToArray()
            },
            SaveToSentItems = _options.SaveToSentItems
        };

        var payloadJson = JsonSerializer.Serialize(request, SerializerOptions);
        return new GraphMailRequest(cardId, payloadJson);
    }

    /// <summary>
    /// Produces an HTML body that hosts the adaptive card payload.
    /// When a signer is configured, appends a SignedAdaptiveCard section;
    /// otherwise falls back to the plain <c>application/adaptivecard+json</c> script tag.
    /// </summary>
    private string BuildHtmlBody(string adaptiveCardJson)
    {
        string introText = string.IsNullOrWhiteSpace(_options.IntroText)
            ? "We value your feedback."
            : WebUtility.HtmlEncode(_options.IntroText);

        StringBuilder builder = new();
        builder.AppendLine("<html>");
        builder.AppendLine("<body style=\"font-family:'Segoe UI', Arial, sans-serif;\">");
        builder.AppendLine($"<p>{introText}</p>");

        if (_signer is not null)
        {
            // Signed card: embed a JWS-signed payload in Microdata format.
            // The plain <script> fallback is omitted; Outlook renders the
            // SignedAdaptiveCard section when the signature is valid.
            string signedPayload = _signer.CreateSignedPayload(adaptiveCardJson, _options.Recipients);
            builder.AppendLine(BuildSignedCardSection(signedPayload));
        }
        else
        {
            // Unsigned fallback: relies on DKIM/SPF for sender verification.
            builder.AppendLine("<div>");
            builder.AppendLine("<script type=\"application/adaptivecard+json\">");
            builder.AppendLine(adaptiveCardJson);
            builder.AppendLine("</script>");
            builder.AppendLine("</div>");
        }

        builder.AppendLine("</body>");
        builder.AppendLine("</html>");
        return builder.ToString();
    }

    /// <summary>
    /// Produces the Microdata HTML section that wraps the compact JWS token,
    /// as required by the SignedAdaptiveCard specification.
    /// </summary>
    private static string BuildSignedCardSection(string signedPayload)
    {
        StringBuilder section = new();
        section.AppendLine("<section itemscope itemtype=\"http://schema.org/SignedAdaptiveCard\">");
        section.AppendLine("    <meta itemprop=\"@context\" content=\"http://schema.org/extensions\" />");
        section.AppendLine("    <meta itemprop=\"@type\" content=\"SignedAdaptiveCard\" />");
        section.AppendLine($"    <div itemprop=\"signedAdaptiveCard\" style=\"mso-hide:all;display:none;max-height:0px;overflow:hidden;\">{signedPayload}</div>");
        section.AppendLine("</section>");
        return section.ToString();
    }

    /// <summary>
    /// Builds the card definition including inputs and submit/status actions.
    /// </summary>
    private string BuildAdaptiveCardJson(string cardId)
    {
		var submitUrl = BuildActionUrl(_options.FeedbackSubmitUrl!);
		var statusUrl = BuildActionUrl(_options.FeedbackStatusUrl!);
		var autoInvokeAction = BuildAutoInvokeAction(cardId, statusUrl);

        var card = new AdaptiveCardModel
        {
            Body = new object[]
            {
                new
                {
                    type = "TextBlock",
                    text = _options.CardTitle,
                    weight = "Bolder",
                    size = "Medium",
                    wrap = true
                },
                new
                {
                    type = "TextBlock",
                    text = _options.CardPrompt,
                    isSubtle = true,
                    wrap = true,
                    spacing = "Small"
                },
                new
                {
                    type = "TextBlock",
                    text = "Rating",
                    weight = "Bolder",
                    spacing = "Medium",
                    wrap = true
                },
                new
                {
                    type = "Input.ChoiceSet",
                    id = "Rating",
                    style = "expanded",
                    value = "5",
                    choices = Enumerable.Range(1, 5)
                        .Select(i => new { title = i.ToString(), value = i.ToString() })
                        .ToArray()
                },
                new
                {
                    type = "TextBlock",
                    text = "Comments (optional)",
                    weight = "Bolder",
                    spacing = "Medium",
                    wrap = true
                },
                new
                {
                    type = "Input.Text",
                    id = "Comment",
                    placeholder = "Add an optional comment",
                    isMultiline = true,
                    maxLength = 2048
                }
            },
            Actions = new object[]
            {
				new
                {
                    type = "Action.Http",
                    title = _options.ActionDisplayName,
                    method = "POST",
					url = submitUrl,
					headers = BuildActionHeaders(cardId),
					body = BuildActionBody(cardId, _options.OriginatorId!)
                }
            },
            Originator = _options.OriginatorId,
			FallbackText = "Open this message in Outlook to submit your feedback.",
			AutoInvokeAction = autoInvokeAction,
			HideOriginalBody = true
        };

        return JsonSerializer.Serialize(card, SerializerOptions);
    }

	/// <summary>
	/// Adds an auto-invoke POST action so Outlook can refresh the card body after submission.
	/// </summary>
	private object? BuildAutoInvokeAction(string cardId, string statusUrl)
	{
		if (string.IsNullOrWhiteSpace(statusUrl))
		{
			return null;
		}

		return new
		{
			type = "Action.Http",
			method = "POST",
			url = statusUrl,
			headers = BuildActionHeaders(cardId),
			body = BuildAutoInvokeBody(cardId, _options.OriginatorId!)
		};
	}

	/// <summary>
	/// Includes card metadata headers expected by actionable message platform.
	/// </summary>
	private object[] BuildActionHeaders(string cardId)
	{
		var headers = new List<object>
		{
			new { name = "x-ms-card-id", value = cardId },
			new { name = "Content-Type", value = "application/json" }
		};

		return headers.ToArray();
	}

    /// <summary>
    /// Constructs the JSON payload Outlook submits, including the adaptive payload tokens for rating/comment.
    /// </summary>
    private static string BuildActionBody(string cardId, string originator)
    {
        const string ratingToken = "{{Rating.value}}";
        const string commentToken = "{{Comment.value}}";
        var escapedCardId = JsonEncodedText.Encode(cardId).ToString();
        var escapedOriginator = JsonEncodedText.Encode(originator).ToString();

        return $"{{\"CardId\":\"{escapedCardId}\",\"Rating\":\"{ratingToken}\",\"Comment\":\"{commentToken}\",\"OriginatorId\":\"{escapedOriginator}\"}}";
    }

	/// <summary>
	/// Builds the status polling request body for actionable message refresh events.
	/// </summary>
	private static string BuildAutoInvokeBody(string cardId, string originator)
	{
		var escapedCardId = JsonEncodedText.Encode(cardId).ToString();
		var escapedOriginator = JsonEncodedText.Encode(originator).ToString();
		return $"{{\"CardId\":\"{escapedCardId}\",\"OriginatorId\":\"{escapedOriginator}\"}}";
	}

	/// <summary>
	/// Appends the Azure Functions key query parameter when configured.
	/// </summary>
	private string BuildActionUrl(string baseUrl)
	{
		if (string.IsNullOrWhiteSpace(_options.FunctionKey))
		{
			return baseUrl;
		}

		var builder = new UriBuilder(baseUrl);
		var keyParam = $"code={Uri.EscapeDataString(_options.FunctionKey!)}";
		if (string.IsNullOrEmpty(builder.Query) || builder.Query == "?")
		{
			builder.Query = keyParam;
		}
		else
		{
			var trimmed = builder.Query.TrimStart('?');
			builder.Query = $"{trimmed}&{keyParam}";
		}

		return builder.Uri.ToString();
	}

    private sealed class GraphSendMailPayload
    {
        public GraphMessage Message { get; set; } = new();

        public bool SaveToSentItems { get; set; }
    }

    private sealed class GraphMessage
    {
        public string? Subject { get; set; }

        public GraphItemBody Body { get; set; } = new();

        public GraphRecipient[] ToRecipients { get; set; } = Array.Empty<GraphRecipient>();
    }

    private sealed class GraphItemBody
    {
        public string? ContentType { get; set; }

        public string? Content { get; set; }
    }

    private sealed class GraphRecipient
    {
        public GraphEmailAddress EmailAddress { get; set; } = new();
    }

    private sealed class GraphEmailAddress
    {
        public string? Address { get; set; }
    }

    private sealed class AdaptiveCardModel
    {
        public string Type { get; } = "AdaptiveCard";

        [JsonPropertyName("$schema")]
        public string Schema { get; } = "http://adaptivecards.io/schemas/adaptive-card.json";

		public string Version { get; } = "1.0";

        public object[] Body { get; set; } = Array.Empty<object>();

        public object[] Actions { get; set; } = Array.Empty<object>();

		public object? AutoInvokeAction { get; set; }

        public string? Originator { get; set; }

        public string? FallbackText { get; set; }

		public bool? HideOriginalBody { get; set; }
    }

}

/// <summary>
/// Represents the serialized Graph sendMail JSON along with its associated card identifier.
/// </summary>
internal sealed record GraphMailRequest(string CardId, string Payload);
