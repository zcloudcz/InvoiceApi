# Feedback Reporting Implementation Plan

> **For agentic workers:** Use `superpowers:subagent-driven-development` or `superpowers:executing-plans` to implement these tracked tasks. The coordinating agent owns execution and review.

**Goal:** Let authenticated users submit and follow bugs, ideas, and observations through the application and MCP, with a central SysAdmin inbox.

**Architecture:** Persist reports in MasterDb and expose one application service through authenticated API endpoints. Blazor and MCP consume the same API. Current develop uses the single ASP.NET Core App Service host; this request-driven feature needs no worker or timer.

**Tech stack:** C#, ASP.NET Core, EF Core/PostgreSQL, shared Blazor/MudBlazor components, resource localization, existing MCP client/tools and test projects.

**Spec and authorization:** This document records the October 1, 2026 conversation requirement. The user explicitly authorized implementation after the production deployment. A central SysAdmin inbox is the working destination choice inferred from assent to that recommendation. This is an implementation detail for review, not an additional approval gate. The coordinator verifies the deployment prerequisite before execution.

## Scope and constraints

- A top-right bug icon in the authenticated layout opens a localized form: Bug, Idea, or Observation; subject; description; optional relative page; application version.
- Users list and view their own reports and public responses. SysAdmin lists and views all reports and changes status to New, InProgress, Resolved, or Declined with an optional public response.
- MCP provides the same submit/list/get operations. Expose status updates only through the API's existing SysAdmin authorization policy, never by trusting MCP arguments or granting API keys new roles implicitly.
- No screenshots, attachments, GitHub integration, or email integration in this version.
- Reuse shared grids, dialogs, enum inputs, validation, and error presentation. Add Czech and English resources and follow existing supported-language conventions.
- Server derives submitter user and effective authorized company from authentication/tenant context. Requests contain no writable owner/company/status fields on creation. Scope non-admin reads by both user and company; return the existing not-found response for inaccessible IDs.
- Apply existing API-key/OAuth read/write scope conventions as well as identity/tenant checks. Reject credentials that cannot establish a submitting user and authorized company. SysAdmin authorization remains required for every global inbox read and status write.
- Required subject: trimmed 1-200 characters. Required description: trimmed 1-10,000 characters. Optional public response: at most 10,000 characters. Optional page: at most 2,048 characters. Optional application version: at most 100 characters. Reject undefined enum values.
- Strip query and fragment from page context on client and server. Accept only a local path beginning with one slash; reject absolute URLs, double-slash paths, backslashes, and control characters. Do not fetch the supplied page or render feedback as HTML/Markdown. Avoid logging submitted text or sensitive request metadata.
- Page size defaults to 25, maximum 100; reject invalid page values. Use stable newest-first ordering with ID as a tie-breaker, projected reads, and indexes supporting owner/company and status queries.
- Preserve others' changes. Generate a real MasterDb migration with EF tooling; do not hand-author a placeholder migration. Document public boundaries in junior-friendly English. MCP functionality bumps its package to 2.4.0, without implying publication.

## Contracts and file ownership

New paths below are proposed names following inspected project structure. Register them through the existing composition roots and use existing ID, pagination, error, and DTO mapping conventions.

| Owner | Create or modify |
| --- | --- |
| Backend | `Fakvio.Domain/Entities/FeedbackReport.cs`, `Fakvio.Domain/Enums/EFeedbackType.cs`, `Fakvio.Domain/Enums/EFeedbackStatus.cs`; `Fakvio.Contracts/Dto/Feedback/`; `Fakvio.Application/Service/IFeedbackService.cs`; `Fakvio.Infrastructure/Service/FeedbackService.cs`; `Fakvio.Infrastructure/Data/MasterDbContext.cs`; generated `Fakvio.Infrastructure/Migrations/Master/*`; `Fakvio.API/Controller/FeedbackController.cs`, `Fakvio.API/Controller/FeedbackSysAdminController.cs` |
| UI | `Fakvio.UI.Shared/Components/Layout/MainLayout.razor`; new feedback form/list/detail components and API adapter under existing `Components` and `Services` conventions; existing navigation; `Fakvio.UI.Shared/Resources/SharedResource.resx` and `.en.resx` |
| MCP | `Fakvio.McpServer/Tools/FeedbackTools.cs`; `Fakvio.McpServer/Client/IFakvioApiClient.cs`, `FakvioApiClient.cs`; tool registration and `Fakvio.McpServer/Fakvio.McpServer.csproj` version |
| Verification and docs | Existing `Fakvio.Tests.Unit`, `Fakvio.Tests.Integration`, and `Fakvio.Tests.Playwright` projects; `DEVGUIDE.md`, `USERGUIDE.md`, `ADMINGUIDE.md`, `TODO.md` |

`FeedbackReport` stores ID, type, subject, description, safe page, app version, user ID, company ID, status, optional public response, and existing audit timestamps. Start at New; only SysAdmin can update status/response. No tenant database connection is needed to read the central inbox.

Proposed HTTP contract:

- `POST /api/feedback`: create from type/subject/description/page/appVersion; return persisted report with its ID and New status.
- `GET /api/feedback`: paginated current-user/current-company reports, optional type/status filters.
- `GET /api/feedback/{id}`: scoped detail with public response.
- `GET /api/sysadmin/feedback` and `GET /api/sysadmin/feedback/{id}`: SysAdmin-only inbox and detail.
- `PATCH /api/sysadmin/feedback/{id}`: SysAdmin-only status/public-response update.

Use `CreateFeedbackDto`, `FeedbackDto`, `FeedbackFilterDto`, and `UpdateFeedbackStatusDto`; list results use the existing `PagedResult<FeedbackDto>`. Service operations accept cancellation tokens and return those contracts. Derive authorization context within the trusted service/controller boundary, never from client DTOs. UI and MCP must consume these shared contracts.

## Review focus

- A guessed report ID or switched tenant must not expose another user's report.
- API keys and OAuth credentials must not bypass user binding, scopes, or SysAdmin policy.
- Query secrets, malicious page strings, and HTML feedback must remain inert and excluded from stored page context.
- High-volume inboxes must remain bounded, stable, and pageable.
- Failed submission must preserve entered text; successful submission must not silently duplicate a report through repeated clicks.

## Task 1: Contracts, persistence, service, and authorized API

- [ ] Add focused failing tests for creation, required fields and exact limits, enum rejection, New default, owner derivation, query/fragment stripping, malicious page rejection, and text preservation.
- [ ] Add authorization tests for anonymous callers, another user in the same company, another company, guessed IDs, restricted API-key/OAuth scopes, absent user binding, and non-SysAdmin inbox/status access. Pin expected unauthorized/forbidden/not-found behavior to existing conventions.
- [ ] Implement the contracts, MasterDb entity/configuration, service, endpoints, DI registration, and existing auth/scope mapping. Status updates set audit metadata and persist the optional public response; regular users cannot mutate reports.
- [ ] Add service tests for admin filtering, status/response updates, page size 100 versus invalid 101, deterministic ordering, and empty pages. Assert owner-scoped queries before pagination.
- [ ] Generate and inspect the MasterDb migration and snapshot. Verify migration discovery and application on an isolated test database, including required indexes and column lengths.
- [ ] Run focused unit and integration tests for feedback; record actual command, counts, and failures. Do not start the application or tests against production configuration.

## Task 2: Localized feedback UI

- [ ] Add navigation/localization tests for the logged-in top-right icon, accessible label, anonymous absence, all type/status labels, and user/SysAdmin routes.
- [ ] Implement the shared submission dialog with subject, description, type, sanitized current path, and application version. Disable submit while pending, preserve input on error, and show a localized success result with report navigation.
- [ ] Implement the user's paged list/detail and SysAdmin inbox/detail using shared grids/dialogs. Display report text and responses with escaped text rendering; show status controls only to SysAdmin.
- [ ] Test successful creation and detail navigation, server validation errors, failed submission retaining input, repeated-click prevention, public response visibility, and admin status changes. Exercise Czech and English resources.
- [ ] Run relevant UI tests and inspect the rendered authenticated header, dialog, and both list/detail flows at normal and narrow widths.

## Task 3: MCP parity

- [ ] Add failing client/tool tests for `submit_feedback`, `list_feedback`, and `get_feedback`, including shared DTO forwarding, pagination, cancellation, and API error propagation.
- [ ] Implement tools through the existing authenticated API client. Do not accept submitter/company overrides, bypass HTTP authorization, or access MasterDb directly.
- [ ] If existing API policy permits SysAdmin MCP credentials, add `update_feedback_status` against the same protected endpoint and test non-admin rejection. Otherwise document that existing policy restriction without broadening credentials.
- [ ] Verify API-key scope denial and cross-user/cross-company denial through the API path as well as correct tool forwarding. Bump MCP package version to 2.4.0 and update tool documentation.
- [ ] Run focused MCP tests and build the package; publication belongs to the release workflow, not this task.

## Task 4: Documentation, review, verification, and draft PR

- [ ] Update DEVGUIDE with MasterDb storage, contracts, authorization/scopes, migration, and tools; USERGUIDE with submission/tracking; ADMINGUIDE with inbox/status/public responses; TODO with completed and outstanding work.
- [ ] Run the affected solution build and appropriate unit, integration, and UI suites using isolated configuration. Broaden checks only for changed dependencies or unresolved failures; report skipped tests and baseline failures explicitly.
- [ ] Review security and performance with emphasis on the five review-focus cases. Fix findings and rerun affected tests. Inspect the generated migration and final diff for unrelated edits.
- [ ] Prepare a draft PR describing behavior, explicit scope limits, migration, version bump, and actual validation. Attach the PR to the task. Preserve parallel ops changes; leave `release-notes.md` to agent-ops at merge.

Done means the authenticated UI and MCP share a persisted, authorized feedback flow; SysAdmin can respond and change status; tests and guides cover the shipped behavior; and a reviewable draft PR exists. It does not mean the new feature has been published or deployed.

## Execution ledger and authorized addendum (2026-10-01)

- User subsequently authorized removal of the header role chip and localized Accountant / Účetní for internal `EUserRole.Admin`. Explicit clarification: preserve existing working rights **except user management**, now SysAdmin-only in the API and UI. Self-service profile/password/preferences remain available. UserController and its authorization tests are owned by the coordinating agent's separate worker.
- Multiple companies were investigated only: the current global unique email and single `User.CompanyId` prevent registering another company with the same email. No membership/registration feature is included.
- Backend implements active persisted user and owner/company verification, plus a persisted SysAdmin check for global reads/status writes. No request accepts writable owner/company/status on creation.
- Generated EF migration: `20261001105750_AddFeedbackReports` and real model snapshot. Generation used an explicitly unused localhost port 1; migration verification uses a disposable PostgreSQL container on localhost:55481 and a generated temporary schema.
- Ruling: preserve existing API-key authorization policy. Actual authentication copies the persisted role; the new admin routes require SysAdmin, and PATCH additionally passes the unchanged write-scope guard. OAuth retains its existing grant/impersonation restrictions. No credential is promoted by feedback.
- Added focused service, HTTP-pipeline, MCP/SDK, bUnit/localization and relational migration tests. Early test failures were fixture/test wiring issues, corrected before the final run; actual final results recorded below when available.
- Source is reviewable; pending final combined verification and independent review. No feedback commit, push, package publication or deployment performed.

### Final verification evidence

- `dotnet test Fakvio.Tests.Unit/Fakvio.Tests.Unit.csproj -c Release --logger trx --verbosity quiet`: **3,986 passed, 4 skipped, 0 failed**. Four skips are the existing `DatabaseConnectivitySmokeTests`. All 70 feedback service/UI/MCP cases passed.
- `dotnet test Fakvio.Tests.Integration/Fakvio.Tests.Integration.csproj -c Release --logger trx --verbosity quiet`: **239 passed, 3 skipped, 0 failed**. Three skips are the existing live EPO/Ares checks. Includes all 14 feedback HTTP/migration cases and 33 user-management cases.
- Database environment explicitly pinned to unused localhost port 1 for the API configuration; `FAKVIO_TEST_POSTGRES` points to disposable localhost:55481 for relational fixtures. Migration application, real column limits, owner FK rejection and paging indexes passed.
- Initial full run found three actual consistency omissions (MCP documented count and navigation catalog after adding feedback/restricting users); corrected. One existing bUnit `MyCompanyEpoSectionTests` stale-event-handler race occurred on that first run and passed in the final full rerun without unrelated source edits.
- Independent feedback security review and coordinator's user-management review found no issues. `git diff --check` passes. Existing package/analyzer warnings remain.
- Browser screenshot/responsive verification was not performed; bUnit covers rendered components, localized labels, anonymous/authenticated header, form failure retention/repeated-click guard and SysAdmin responses.
- No commit, push, draft PR, MCP publication or feedback deployment performed by the implementation worker. Coordinator owns those next actions. Multiple-company membership work is separately authorized and intentionally excluded here.
