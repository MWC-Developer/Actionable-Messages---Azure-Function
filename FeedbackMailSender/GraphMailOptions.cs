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
/// Defines the content and submission endpoints used when composing feedback actionable emails.
/// </summary>
internal sealed class GraphMailOptions
{
    public const string SectionName = "GraphMail";

    public string[] Recipients { get; set; } = Array.Empty<string>();

    public string? Subject { get; set; }

    public string? SenderUserId { get; set; }

    public string? IntroText { get; set; }

    public string? FeedbackSubmitUrl { get; set; }

	public string? FeedbackStatusUrl { get; set; }

    public string? ActionDisplayName { get; set; }

    public string? OriginatorId { get; set; }

	public string? FunctionKey { get; set; }

    public bool SaveToSentItems { get; set; } = true;

    public string? CardTitle { get; set; }

    public string? CardPrompt { get; set; }

    /// <summary>
    /// Applies defaults for missing strings and removes whitespace from configured values.
    /// </summary>
    public void Normalize()
    {
        Recipients = NormalizeArray(Recipients);
        Subject = NormalizeString(Subject) ?? "We value your feedback";
        SenderUserId = NormalizeString(SenderUserId);
        IntroText = NormalizeString(IntroText);
        FeedbackSubmitUrl = NormalizeString(FeedbackSubmitUrl);
		FeedbackStatusUrl = NormalizeString(FeedbackStatusUrl);
        ActionDisplayName = NormalizeString(ActionDisplayName) ?? "Submit feedback";
        OriginatorId = NormalizeString(OriginatorId);
		FunctionKey = NormalizeString(FunctionKey);
        CardTitle = NormalizeString(CardTitle) ?? "How did we do?";
        CardPrompt = NormalizeString(CardPrompt) ?? "Pick a rating and leave an optional comment.";
    }

    /// <summary>
    /// Validates recipients, URLs, and required identifiers before sending mail.
    /// </summary>
    public void EnsureValid(EntraIdAuthMode authMode)
    {
        if (Recipients.Length == 0)
        {
            throw new InvalidOperationException("GraphMail:Recipients must contain at least one email address.");
        }

        if (string.IsNullOrWhiteSpace(FeedbackSubmitUrl) || !Uri.TryCreate(FeedbackSubmitUrl, UriKind.Absolute, out _))
        {
            throw new InvalidOperationException("GraphMail:FeedbackSubmitUrl must be an absolute URL.");
        }

		if (string.IsNullOrWhiteSpace(FeedbackStatusUrl) || !Uri.TryCreate(FeedbackStatusUrl, UriKind.Absolute, out _))
		{
			throw new InvalidOperationException("GraphMail:FeedbackStatusUrl must be an absolute URL.");
		}

        if (string.IsNullOrWhiteSpace(OriginatorId))
        {
            throw new InvalidOperationException("GraphMail:OriginatorId must be provided.");
        }

        if (!Guid.TryParse(OriginatorId, out _))
        {
            throw new InvalidOperationException("GraphMail:OriginatorId must be a valid GUID.");
        }

		if (string.IsNullOrWhiteSpace(FunctionKey))
		{
			throw new InvalidOperationException("GraphMail:FunctionKey must be provided.");
		}

        if (authMode == EntraIdAuthMode.Application && string.IsNullOrWhiteSpace(SenderUserId))
        {
            throw new InvalidOperationException("GraphMail:SenderUserId is required when using application permissions.");
        }
    }

    private static string? NormalizeString(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string[] NormalizeArray(string[]? values) => values is { Length: > 0 }
        ? values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
        : Array.Empty<string>();
}
