using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Security.Principal;

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
    /// Windows Task Scheduler runs this exe once per scheduled slot (daily, typically) under a
    /// dedicated AD service account - it owns the schedule; this class does not loop or sleep.
    ///
    /// Deliberately "dumb": its only job is one HTTP call to Shield's internal/runNightlyOcr,
    /// which triggers one full pass in-process there (find candidates, resolve+extract, save
    /// back to Danya.Server) - the exact same logic that used to run on an internal timer inside
    /// Shield. The Worker does not know about invoices, PDFs, or Danya.Server; it only knows how
    /// to wake Shield up and log what came back. See Danya.Server.Shield/CLAUDE.md
    /// "Nightly OCR Pipeline" for the wider design.
    ///
    /// Authenticated by Windows Integrated Auth (the HttpClient sends the process's own Windows
    /// identity - see Program.cs) instead of a shared-secret header - Shield checks that
    /// identity is exactly the expected service account.
    /// </summary>
    public class OcrRunner
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConfiguration _config;
        private readonly ILogger<OcrRunner> _logger;

        public OcrRunner(IHttpClientFactory httpClientFactory, IConfiguration config, ILogger<OcrRunner> logger)
        {
            _httpClientFactory = httpClientFactory;
            _config = config;
            _logger = logger;
        }

        /// <summary>Triggers one nightly OCR pass on Shield. Returns a process exit code: 0 if
        /// Shield ran (even with some per-invoice failures - that's normal and Shield's own
        /// problem to retry), 1 if the trigger call itself could not be completed, so Task
        /// Scheduler can flag the run as failed.</summary>
        public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
        {
            var baseUrl = _config["Shield:BaseUrl"];

            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                _logger.LogError("Shield:BaseUrl is not configured");
                return 1;
            }

            var url = $"{baseUrl.TrimEnd('/')}/api/ocr/internal/runNightlyOcr";

            // Debug aid only - proves which Windows identity this process actually has BEFORE
            // the request goes out, so a mismatch with Shield:AllowedServiceAccount is obvious
            // from the Worker's own log instead of guessing from Shield's 403. Not used for
            // authentication itself - that's UseDefaultCredentials + SSPI (see Program.cs).
            _logger.LogInformation("Running as Windows identity: {Identity}", WindowsIdentity.GetCurrent().Name);

            try
            {
                var client = _httpClientFactory.CreateClient("Shield");
                using var response = await client.PostAsync(url, content: null, cancellationToken);
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
    }
}
