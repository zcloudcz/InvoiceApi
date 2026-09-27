# Release notes

## Unreleased

### Features
- **#444** — Added recurring invoice automation: auto-generate invoices on schedule. (PR #444, `2d89b73`)
- **#444** — Client bulk import via CSV with field validation and error recovery. (PR #444, `2d89b73`)
- **#444** — Rate limiting on authentication endpoints (login, 2FA, password reset, signup) per IP. (PR #444, `2d89b73`)
- **#444** — reCAPTCHA v3 hardening on login and forgot-password flows. (PR #444, `2d89b73`)
- **#444** — MCP Server 2.1.0: breaking change — `update_number_sequence` and `update_my_company` now marked `Destructive=true` (require user confirmation). (PR #444, `2d89b73`)
