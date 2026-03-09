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

using Microsoft.Extensions.Configuration;

namespace FeedbackMailSender;

/// <summary>
/// Entry point for the console sender that dispatches adaptive feedback cards.
/// </summary>
internal static class Program
{
    /// <summary>
    /// Loads configuration, validates options, and sends the Graph mail payload.
    /// </summary>
    private static async Task<int> Main()
    {
        var environment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production";
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
            .AddJsonFile($"appsettings.{environment}.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables()
            .Build();

        // Bind Entra ID settings and ensure required identifiers/secrets are present.
        var entraIdOptions = configuration.GetSection(EntraIdOptions.SectionName).Get<EntraIdOptions>()
            ?? throw new InvalidOperationException("EntraId configuration section is required.");
        entraIdOptions.Normalize();
        entraIdOptions.EnsureValid();

        // Bind Graph mail composition settings and verify all URLs/IDs.
        var mailOptions = configuration.GetSection(GraphMailOptions.SectionName).Get<GraphMailOptions>()
            ?? throw new InvalidOperationException("GraphMail configuration section is required.");
        mailOptions.Normalize();
        mailOptions.EnsureValid(entraIdOptions.AuthMode);

        using var httpClient = new HttpClient();
        using var cts = new CancellationTokenSource();

        Console.CancelKeyPress += (_, args) =>
        {
            args.Cancel = true;
            cts.Cancel();
        };

        var tokenProvider = new TokenProvider(entraIdOptions);
        var payloadFactory = new GraphMailPayloadFactory(mailOptions);
        var sender = new GraphMailSender(httpClient, tokenProvider, payloadFactory, mailOptions, entraIdOptions);

        try
        {
            var result = await sender.SendMailAsync(cts.Token).ConfigureAwait(false);
            Console.WriteLine($"Graph sendMail accepted card {result.CardId}.");
            return 0;
        }
        catch (TaskCanceledException)
        {
            Console.WriteLine("Operation canceled by user.");
            return -1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Sending feedback adaptive card failed: {ex.Message}");
            return -1;
        }
    }
}
