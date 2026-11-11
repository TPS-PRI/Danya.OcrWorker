using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Net.Http.Headers;
using System.Text;

namespace Danya.OcrWorker
{
    /// <summary>
    /// Mirrors Shield's NightlyOcrRunResult / NightlyShippingCertOcrRunResult - the JSON body both
    /// trigger endpoints return. Suspicious and Deferred are only sent by the shipping-cert run.
    /// </summary>
    public class NightlyOcrRunResult
    {
        public bool Enabled { get; set; } = true;
        public string? RunId { get; set; }
        public int Scanned { get; set; }
        public int Saved { get; set; }
        public int Suspicious { get; set; }
        public int Skipped { get; set; }
        public int Failed { get; set; }
        public int Deferred { get; set; }
        public int AttentionCount { get; set; }
    }

    /// <summary>One nightly job on Shield: a name for --job and the log, and its trigger endpoint.</summary>
    public record NightlyJob(string Name, string Path, string ItemName);

    /// <summary>
    /// Windows Task Scheduler runs this exe once per scheduled slot (daily, typically) - it owns
    /// the schedule; this class does not loop or sleep.
    ///
    /// Deliberately "dumb": it logs in, makes one HTTP call per nightly job to Shield, each of
    /// which triggers one full pass in-process there (find candidates, resolve+check, save back
    /// to Danya.Server), and logs out. Jobs (see Jobs below):
    ///   - invoices      -> internal/runNightlyOcr              (invoice OCR)
    ///   - shippingCerts -> internal/runNightlyShippingCertOcr  (missing stamp / handwriting check) The Worker does not know about invoices, PDFs, or
    /// Danya.Server; it only knows how to wake Shield up and log what came back. See
    /// Danya.Server.Shield/CLAUDE.md "Nightly OCR Pipeline" for the wider design.
    ///
    /// Authenticated like any other client: Shield's api/users/loginApi (Shield:LoginName,
    /// Shield:UserPassword, Shield:UserVerify) returns a JWT, sent as Authorization: Bearer.
    /// </summary>
    public class OcrRunner
    {
        public static readonly IReadOnlyList<NightlyJob> Jobs = new[]
        {
            new NightlyJob("invoices", "/api/ocr/internal/runNightlyOcr", "invoice"),
            new NightlyJob("shippingCerts", "/api/ocr/internal/runNightlyShippingCertOcr", "shipping certificate file"),
        };

        private readonly System.Net.Http.IHttpClientFactory _httpClientFactory;
        private readonly IConfiguration _config;
        private readonly ILogger<OcrRunner> _logger;

        public OcrRunner(System.Net.Http.IHttpClientFactory httpClientFactory, IConfiguration config, ILogger<OcrRunner> logger)
        {
            _httpClientFactory = httpClientFactory;
            _config = config;
            _logger = logger;
        }

        /// <summary>Triggers one pass of each given job on Shield, in order, with one login.
        /// Returns a process exit code: 0 if Shield ran every job (even with some per-item
        /// failures - that's normal and Shield's own problem to retry), 1 if the login or any
        /// trigger call could not be completed, so Task Scheduler can flag the run as failed. A
        /// failed job does not stop the next one.</summary>
        public async Task<int> RunOnceAsync(IReadOnlyList<NightlyJob> jobs, CancellationToken cancellationToken)
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
                var exitCode = 0;
                foreach (var job in jobs)
                {
                    if (await RunJobAsync(client, baseUrl, token, job, cancellationToken) != 0)
                        exitCode = 1;
                }
                return exitCode;
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

        private async Task<int> RunJobAsync(HttpClient client, string baseUrl, string token, NightlyJob job, CancellationToken cancellationToken)
        {
            try
            {
                _logger.LogInformation("[{Job}] triggering {Path}", job.Name, job.Path);

                using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + job.Path);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

                using var response = await client.SendAsync(request, cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    // 409 = the previous run of this job is still going on Shield.
                    _logger.LogError("[{Job}] {Path} returned {Status}: {Body}", job.Name, job.Path, (int)response.StatusCode, body);
                    return 1;
                }

                var result = JsonConvert.DeserializeObject<NightlyOcrRunResult>(body);

                if (result == null)
                {
                    _logger.LogError("[{Job}] {Path} returned an unparsable body: {Body}", job.Name, job.Path, body);
                    return 1;
                }

                if (!result.Enabled)
                {
                    _logger.LogInformation("[{Job}] Shield reports this job is disabled - nothing ran", job.Name);
                    return 0;
                }

                _logger.LogInformation(
                    "[{Job}:{RunId}] scanned:{Scanned} saved:{Saved} suspicious:{Suspicious} skipped:{Skipped} failed:{Failed} deferred:{Deferred}",
                    job.Name, result.RunId, result.Scanned, result.Saved, result.Suspicious, result.Skipped, result.Failed, result.Deferred);

                if (result.AttentionCount > 0)
                {
                    _logger.LogWarning("[{Job}:{RunId}] {Count} {Item}(s) need manual attention on Shield",
                        job.Name, result.RunId, result.AttentionCount, job.ItemName);
                }

                return 0;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{Job}] {Path} call failed", job.Name, job.Path);
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
