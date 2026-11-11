# Danya.OcrWorker

Small .NET 8 console app, triggered once per scheduled slot (daily) by
**Windows Task Scheduler**. It does not know about invoices, PDFs, or
Oracle — its only job is to wake up `Danya.Server.Shield`'s nightly jobs
and log what came back:

| Job (`--job`) | Shield endpoint | What Shield does |
|---|---|---|
| `invoices` | `POST /api/ocr/internal/runNightlyOcr` | Invoice OCR (`TPS_B2B_GET_OCR` → Gemini → `TPS_B2B_SET_IV_OCR`) |
| `shippingCerts` | `POST /api/ocr/internal/runNightlyShippingCertOcr` | Shipping-certificate check for a missing stamp / handwriting (`TPS_B2B_GET_OCR_DOC` → model → `TPS_B2B_SET_OCR_DOC`) |

With no arguments it runs both, in that order, with one login. To run only
some: `Danya.OcrWorker.exe --job shippingCerts` (repeatable).

## What it does

1. Logs in: `POST {Shield:BaseUrl}/api/users/loginApi` with
   `Shield:LoginName` / `Shield:UserPassword` / `Shield:UserVerify`, and
   gets a JWT back.
2. For each job, sends one `POST` to its Shield endpoint with
   `Authorization: Bearer <JWT>`.
3. Shield runs one full pass in-process (find candidates → download the
   file → model → save back to `Danya.Server`) and returns a JSON summary.
   A failed job does not stop the next one.
4. Logs out (`POST /api/users/logout`), so the session ends on
   `Danya.Server` right away instead of staying valid for a day.
5. Logs `scanned` / `saved` / `skipped` / `failed` / `attentionCount`,
   and exits with:
   - **0** — Shield ran (even with some per-invoice failures — that's
     normal, and Shield's own problem to retry).
   - **1** — the login or any trigger call could not be completed
     (Shield unreachable, wrong credentials, timeout, a job still running
     from before (409), etc.), so Task Scheduler can flag the run as failed.

The Worker never touches invoices, PDFs, or Oracle directly — all of
that logic lives in `Danya.Server.Shield`.

## Project layout

| File | Purpose |
|---|---|
| `Program.cs` | Host bootstrap, NLog setup, registers the `"Shield"` `HttpClient` |
| `OcrRunner.cs` | Login, the trigger call + result logging, logout |
| `appsettings.json` / `appsettings.Development.json` | Config (see below) |
| `nlog.config` | Logging configuration |

## Configuration

```json
{
  "Shield": {
    "BaseUrl": "https://<Danya.Server.Shield's real address>",
    "LoginName": "<loginApi user>",
    "UserPassword": "<its password>",
    "UserVerify": "<its 8-10 digit tax number>"
  }
}
```

The password is a secret: keep it out of git (environment variables
`Shield__UserPassword` etc., or an `appsettings.json` that is only on the
server).

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

- [ ] **Task Scheduler** — create the task on the target server, with
  **"Run whether user is logged on or not"** checked.
- [ ] `Shield:BaseUrl` here points at the real deployed Shield address.
- [ ] `Shield:LoginName` / `Shield:UserPassword` / `Shield:UserVerify` are
  set to a real `loginApi` user.

## Logging

NLog, configured via `nlog.config`. Logs both the trigger call itself
and (via the response body) a summary of what Shield did on its end.
