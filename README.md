# Danya.OcrWorker

Small .NET 8 console app, triggered once per scheduled slot (daily) by
**Windows Task Scheduler**. It does not know about invoices, PDFs, or
Oracle — its only job is to wake up `Danya.Server.Shield`'s nightly OCR
pass and log what came back.

## What it does

1. Sends a single `POST` to `{Shield:BaseUrl}/api/ocr/internal/runNightlyOcr`.
2. Authenticates via **Windows Integrated Auth** (Kerberos/NTLM) — the
   process's own Windows identity, sent automatically. No API key, no
   secret to manage or rotate.
3. Shield runs one full OCR pass in-process (find candidates → resolve
   PDF → extract via Gemini → save back to `Danya.Server`) and returns a
   JSON summary.
4. Logs `scanned` / `saved` / `skipped` / `failed` / `attentionCount`,
   and exits with:
   - **0** — Shield ran (even with some per-invoice failures — that's
     normal, and Shield's own problem to retry).
   - **1** — the trigger call itself could not be completed (Shield
     unreachable, auth rejected, timeout, etc.), so Task Scheduler can
     flag the run as failed.

The Worker never touches invoices, PDFs, or Oracle directly — all of
that logic lives in `Danya.Server.Shield`.

## Project layout

| File | Purpose |
|---|---|
| `Program.cs` | Host bootstrap, NLog setup, registers the `"Shield"` `HttpClient` with `UseDefaultCredentials = true` |
| `OcrRunner.cs` | The actual trigger call + result logging |
| `appsettings.json` / `appsettings.Development.json` | Config (see below) |
| `nlog.config` | Logging configuration |

## Configuration

```json
{
  "Shield": {
    "BaseUrl": "https://<Danya.Server.Shield's real address>"
  }
}
```

`Shield:BaseUrl` must point at a real, reachable `Danya.Server.Shield`
instance — not `localhost`, in any environment where the two aren't on
the same machine.

## Build & run locally

```bash
dotnet build
dotnet run
```

Running it locally with no Shield reachable is expected to fail
gracefully (connection refused, exit code 1, clear log line) — that's
the intended behavior, not a bug.

## Deployment checklist

- [ ] **Task Scheduler** — create the task on the target server, under a
  dedicated **AD service account** (e.g. `DOMAIN\svc-ocrworker`), with
  **"Run whether user is logged on or not"** checked.
- [ ] That exact same account must also be:
  - Set as `Shield:AllowedServiceAccount` in `Danya.Server.Shield`'s
    `appsettings.json`.
  - Added to the allow-list in `Danya.Server.Shield`'s `web.config`
    (`<location path="api/ocr/internal">`).
  - All three (Task Scheduler, `appsettings.json`, `web.config`) must
    match **exactly**, including domain.
- [ ] `Shield:BaseUrl` here points at the real deployed Shield address.
- [ ] Both machines (this Worker's host and Shield's host) are joined to
  the same AD domain — Windows Integrated Auth requires it in
  production (local/dev testing falls back to NTLM under your own
  Windows login, which works without a domain).

## Logging

NLog, configured via `nlog.config`. Logs both the trigger call itself
and (via the response body) a summary of what Shield did on its end.
