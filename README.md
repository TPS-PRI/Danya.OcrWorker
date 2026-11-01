# Danya.OcrWorker

Small .NET 8 console app, triggered once per scheduled slot (daily) by
**Windows Task Scheduler**. It does not know about invoices, PDFs, or
Oracle — its only job is to wake up `Danya.Server.Shield`'s nightly OCR
pass and log what came back.

## What it does

1. Logs in: `POST {Shield:BaseUrl}/api/users/loginApi` with
   `Shield:LoginName` / `Shield:UserPassword` / `Shield:UserVerify`, and
   gets a JWT back.
2. Sends a single `POST` to `{Shield:BaseUrl}/api/ocr/internal/runNightlyOcr`
   with `Authorization: Bearer <JWT>`.
3. Shield runs one full OCR pass in-process (find candidates → resolve
   PDF → extract via Gemini → save back to `Danya.Server`) and returns a
   JSON summary.
4. Logs out (`POST /api/users/logout`), so the session ends on
   `Danya.Server` right away instead of staying valid for a day.
5. Logs `scanned` / `saved` / `skipped` / `failed` / `attentionCount`,
   and exits with:
   - **0** — Shield ran (even with some per-invoice failures — that's
     normal, and Shield's own problem to retry).
   - **1** — the login or the trigger call itself could not be completed
     (Shield unreachable, wrong credentials, timeout, etc.), so Task
     Scheduler can flag the run as failed.

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
