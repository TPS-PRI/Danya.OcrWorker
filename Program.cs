using Danya.OcrWorker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NLog;
using NLog.Extensions.Logging;
using System.Net.Http;
using System.Threading;

// Loaded once, before the host even exists, so a failure that happens during startup itself
// (bad config, DI wiring, etc.) still gets written to file - not just successful runs. Mirrors
// Danya.Server.Shield/Program.cs's setup exactly, so both projects log the same way.
var nlogLogger = LogManager.Setup().LoadConfigurationFromFile("nlog.config", optional: false).GetCurrentClassLogger();

try
{
    var builder = Host.CreateApplicationBuilder(args);

    builder.Logging.ClearProviders();
    builder.Logging.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Trace);
    builder.Logging.AddNLog();

    // No custom header, no secret to store or rotate: this client sends the process's own Windows
    // identity (Kerberos/NTLM) on every request. Windows Task Scheduler runs this exe under a
    // dedicated AD service account, so that identity IS the account - Shield checks it's exactly
    // the expected one (Shield:AllowedServiceAccount) instead of an X-Internal-Api-Key header.
    builder.Services.AddHttpClient("Shield")
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            UseDefaultCredentials = true
        });

    builder.Services.AddSingleton<OcrRunner>();

    using var host = builder.Build();

    // No local Ocr:Enabled check here on purpose: the Worker is deliberately "dumb" - it always
    // calls Shield, and Shield's own Ocr:Enabled decides whether anything actually runs (see
    // NightlyOcrRunner.RunOnceAsync). One switch, not two, so there's only one place to look when
    // wondering why nothing ran.
    var runner = host.Services.GetRequiredService<OcrRunner>();

    // One call, then exit - Windows Task Scheduler owns the schedule now, not this process.
    return await runner.RunOnceAsync(CancellationToken.None);
}
catch (Exception exception)
{
    nlogLogger.Error(exception, "Stopped Danya.OcrWorker because of an unhandled exception");
    throw;
}
finally
{
    // Flushes buffered log writes to disk before the process exits - without this, a fast exit
    // (this process only ever runs once and exits, unlike Shield which stays up) can lose the
    // last few log lines still sitting in NLog's internal buffer.
    LogManager.Shutdown();
}
