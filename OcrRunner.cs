using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Net.Http.Headers;
using System.Text;

namespace Danya.OcrWorker
{
    /// <summary>Mirrors Shield's NightlyOcrRunResult - the JSON body internal/runNightlyOcr returns.</summary>
    public class NightlyOcrRunResult
    {
        public bool Enabled { get; set; } = true;
        public string? RunId { get; set; }
        public int Scanned { get; set; }
        public int Saved { get; set; }
        public int Skipped { get; set; }
        public int Failed { get; set; }
        public int AttentionCount { get; set; }
    }

    /// <summary>
    /// Windows Task Scheduler runs this exe once per scheduled slot (daily, typically) - it owns
    /// the schedule; this class does not loop or sleep.
    ///
    /// Deliberately "dumb": it logs in, makes one HTTP call to Shield's internal/runNightlyOcr,
    /// which triggers one full pass in-process there (find candidates, resolve+extract, save
    /// back to Danya.Server), and logs out. The Worker does not know about invoices, PDFs, or
    /// Danya.Server; it only knows how to wake Shield up and log what came back. See
    /// Danya.Server.Shield/CLAUDE.md "Nightly OCR Pipeline" for the wider design.
    ///
    /// Authenticated like any other client: Shield's api/users/loginApi (Shield:LoginName,
    /// Shield:UserPassword, Shield:UserVerify) returns a JWT, sent as Authorization: Bearer.
    /// </summary>
    public class OcrRunner
    {
        private readonly System.Net.Http.IHttpClientFactory _httpClientFactory;
        private readonly IConfiguration _config;
        private readonly ILogger<OcrRunner> _logger;

        public OcrRunner(System.Net.Http.IHttpClientFactory httpClientFactory, IConfiguration config, ILogger<OcrRunner> logger)
        {
            _httpClientFactory = httpClientFactory;
            _config = config;
            _logger = logger;
        }

        /// <summary>Triggers one nightly OCR pass on Shield. Returns a process exit code: 0 if
        /// Shield ran (even with some per-invoice failures - that's normal and Shield's own
        /// problem to retry), 1 if the login or the trigger call itself could not be completed,
        /// so Task Scheduler can flag the run as failed.</summary>
        public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
        {
            var baseUrl = _config["Shield:BaseUrl"];

            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                _logger.LogError("Shield:BaseUrl is not configured");
                return 1;
            }

            baseUrl = baseUrl.TrimEnd('/');
            var client = _httpClientFactory.CreateClient("Shield");

            var token = await LoginAsync(client, baseUrl, cancellationToken);
            if (token == null)
                return 1;

            try
            {
                return await RunNightlyOcrAsync(client, baseUrl, token, cancellationToken);
            }
            finally
            {
                // Ends the session on Danya.Server, so the token stops working right after the run
                // instead of staying valid for a day.
                await LogoutAsync(client, baseUrl, token);
            }
        }

        private async Task<string?> LoginAsync(HttpClient client, string baseUrl, CancellationToken cancellationToken)
        {
            var loginName = _config["Shield:LoginName"];
            var userPassword = _config["Shield:UserPassword"];
            var userVerify = _config["Shield:UserVerify"];

            if (string.IsNullOrWhiteSpace(loginName) || string.IsNullOrWhiteSpace(userPassword) || string.IsNullOrWhiteSpace(userVerify))
            {
                _logger.LogError("Shield:LoginName / Shield:UserPassword / Shield:UserVerify are not configured");
                return null;
            }

            try
            {
                var json = JsonConvert.SerializeObject(new { loginName, userPassword, userVerify });
                using var response = await client.PostAsync($"{baseUrl}/api/users/loginApi",
                    new StringContent(json, Encoding.UTF8, "application/json"), cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);

                // A wrong password still answers 200, with an error message instead of a token.
                var token = response.IsSuccessStatusCode
                    ? JObject.Parse(body).GetValue("data", StringComparison.OrdinalIgnoreCase)?
                        .Value<JObject>()?.GetValue("token", StringComparison.OrdinalIgnoreCase)?.Value<string>()
                    : null;

                if (string.IsNullOrWhiteSpace(token))
                {
                    _logger.LogError("loginApi as {LoginName} failed with {Status}: {Body}", loginName, (int)response.StatusCode, body);
                    return null;
                }

                _logger.LogInformation("Logged in to Shield as {LoginName}", loginName);
                return token;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "loginApi call failed");
                return null;
            }
        }

        private async Task<int> RunNightlyOcrAsync(HttpClient client, string baseUrl, string token, CancellationToken cancellationToken)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/ocr/internal/runNightlyOcr");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

                using var response = await client.SendAsync(request, cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError("internal/runNightlyOcr returned {Status}: {Body}", (int)response.StatusCode, body);
                    return 1;
                }

                var result = JsonConvert.DeserializeObject<NightlyOcrRunResult>(body);

                if (result == null)
                {
                    _logger.LogError("internal/runNightlyOcr returned an unparsable body: {Body}", body);
                    return 1;
                }

                if (!result.Enabled)
                {
                    _logger.LogInformation("Shield reports Ocr:Enabled = false - nothing ran");
                    return 0;
                }

                _logger.LogInformation(
                    "[ocr:{RunId}] scanned:{Scanned} saved:{Saved} skipped:{Skipped} failed:{Failed}",
                    result.RunId, result.Scanned, result.Saved, result.Skipped, result.Failed);

                if (result.AttentionCount > 0)
                {
                    _logger.LogWarning("[ocr:{RunId}] {Count} invoice(s) need manual attention on Shield",
                        result.RunId, result.AttentionCount);
                }

                return 0;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "internal/runNightlyOcr call failed");
                return 1;
            }
        }

        private async Task LogoutAsync(HttpClient client, string baseUrl, string token)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/users/logout");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

                using var response = await client.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("logout returned {Status}; the session ends on its own when the token expires", (int)response.StatusCode);
                }
            }
            catch (Exception ex)
            {
                // The run itself already finished, so this does not change the exit code.
                _logger.LogWarning(ex, "logout call failed; the session ends on its own when the token expires");
            }
        }
    }
}
