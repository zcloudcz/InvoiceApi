# Multi-company accounts: implementation and verification record

Status: implemented and verified in `codex/multi-company` after feedback commit `fb4900e`, ready for draft PR review. No production deployment, production database migration, or MCP publication is authorized by this implementation.

## Approved goal and invariants

One existing identity can add companies and accept existing-account invitations, with one globally unique email and User/Admin company memberships. Admin is displayed as Accountant / Účetní; SysAdmin remains a platform role. Company switching must never change `User.CompanyId`, identity credentials, or machine credential grants.

Every company request checks active identity, live membership, available issuer and effective role before endpoint authorization. A stale JWT does not preserve access after revocation or downgrade. SysAdmin impersonation remains separate. Legacy keys retain only their original company; new manual keys explicitly select allowed companies. OAuth codes, grants, access tokens and refresh chains stay bound to the consent company.

## Implemented boundaries

- [x] `UserCompanyMembership` and `CompanyMembershipInvitation` in MasterDb, unique user/company and user/creation-operation indexes, constrained company roles, hashed invitation tokens and audit fields. Newly inserted users receive their default membership in the same save; saving an existing user never recreates revoked membership.
- [x] EF migration `20261001114014_AddUserCompanyMemberships` and generated snapshot/designer. Up backfills default-company memberships and explicit legacy credential bindings. Down revokes credentials and removes pending codes whose legacy interpretation would change company access or restore a revoked/downgraded membership before dropping bindings.
- [x] `CompanyMembershipMiddleware` registered after authentication and before authorization. It replaces stale role claims from live membership, checks manual-key grants and rejects OAuth company overrides. Explicit identity-recovery endpoints permit listing/switching after default membership revocation without granting old-company data access.
- [x] `CompanySessionService` provides interactive switch and refresh JWTs from live membership/readiness. API keys and OAuth cannot use these endpoints. Browser switching replaces stored auth only after success, then reloads to clear old company views and caches.
- [x] `CompanyMembershipService` creates company/settings/Accountant membership under the same identity with durable `OperationId`; PostgreSQL advisory locking serializes provisioning. Failed provisioning remains visible and retries the existing company. Machine retry additionally requires an explicit grant for the target company.
- [x] SysAdmin existing-account invitations send bilingual HTML-encoded email and return the raw token once for a manual link. Only its hash is stored. Tokens expire after 48 hours; resend consumes previous pending tokens. Acceptance locks the token, requires the invited logged-in identity, consumes once and creates/reactivates one membership without password reset.
- [x] `FeedbackService`, notification recipients and company-filtered user queries use memberships. Personal feedback retains both user and company ownership filters.
- [x] Manual keys persist `CompanyId` and `AllowedCompanyIds` (maximum 100). Issuance validates active issuer memberships. OAuth consent echoes the displayed company and rejects a decision after another tab switches company. Code exchange, refresh and authentication recheck live membership and the pinned company.
- [x] Localized CZ/EN header company selector with visible desktop company name, shared registration/company identity fields, add/retry dialog, existing-account invitation action and acceptance page. The invitation token stays in a fragment and per-tab sessionStorage during login/2FA/external-login return, never in a query or arbitrary return URL.
- [x] Integration key picker defaults to current company, displays explicit grants, and preserves SysAdmin platform keys with no memberships. Connected applications display consent company.
- [x] MCP package version 2.5.0: `list_companies`, `select_company`, `add_company`, `retry_company_setup`; 67 tools total. Optional `companyId` is request-local via AsyncLocal and an outgoing request header; discovery adds it and invocation removes it before method binding. No shared mutable session selection or token exchange.
- [x] DEVGUIDE, USERGUIDE, ADMINGUIDE, MCP README and TODO describe implemented behavior, grant limits and rollback impact. Release notes remain owned by the merge workflow.

## Final contracts and routes

All IDs are `long`. DTOs live in `Fakvio.Contracts.Dto.CompanyMembership`. `CompanyMembershipDto` contains CompanyId, CompanyName, Role, IsDefault and IsProvisioned. `CreateMyCompanyDto` contains a required stable Guid OperationId and company identity/address fields. Switch returns existing `LoginResponse`. Invitation issue returns membership invitation metadata and the one-time raw Token.

| Route | Implemented authorization and result |
| --- | --- |
| GET /api/my-companies | Active memberships; machine callers see only intersection with explicit credential grants. |
| POST /api/my-companies | Active verified identity; machine caller also needs write scope. Idempotent company creation, no automatic credential grant. |
| POST /api/my-companies/{id}/retry-provisioning | Authorized creator with active Accountant membership and recorded creation operation; machine caller also needs write scope and explicit target grant. |
| POST /api/my-companies/switch | Interactive session only, live membership and provisioned company, replacement JWT. |
| POST /api/my-companies/invitations | SysAdmin issue/resend for an active existing account; new accounts retain the existing new-user invitation flow. |
| POST /api/my-companies/invitations/accept | Interactive invited identity and single-use token. |

`X-Selected-Company-Id` is reserved for manual credential company selection; existing `X-Company-Id` remains SysAdmin impersonation. Conflicting headers and OAuth overrides are rejected. MCP `select_company` returns a validated hint; manual-key callers repeat `companyId` on each subsequent tool call. OAuth callers omit this argument, even for the consent company. To retry setup of a newly created company before issuing a credential that grants it, use the authenticated browser.

## Deliberate deviations from the initial proposal

- The proposed shared AuthService/login/2FA signing refactor was not applied. Existing initial login metadata may retain default `User.Role`; live middleware is authoritative, and the narrow approved CompanySessionService owns switch/refresh. If the default membership was revoked, users explicitly list and switch to another membership; automatic login fallback was not implemented.
- Separate invitation/access services and an API-key grant join table were unnecessary. Membership/invitation logic uses one service, access is enforced in middleware, and key grants are a bounded PostgreSQL bigint array. OAuth refresh tokens inherit company from their grant rather than duplicating a mutable binding.
- MCP selection is stateless per invocation, not persistent per HTTP or stdio session. Invitation acceptance intentionally remains an interactive browser operation; a scoped machine credential cannot turn an invitation into broader access.
- A new email follows existing registration/new-user invitation first. The new membership invitation endpoint requires an existing active identity and does not create a second or shadow account.
- Browser visual/journey validation has not been run. Automated bUnit checks cover the UI state changes; they do not substitute for visual review on desktop/mobile.

## Verification evidence and remaining checks

- [x] Focused UI/MCP/login suite: 89 passed, 0 failed. Includes real SDK discovery/calls, concurrent isolated company headers, OAuth consent echo, stable creation-operation retry and invitation login return.
- [x] Subsequent focused IntegrationsPageTests: 22 passed, 0 failed, including SysAdmin empty global grant and ordinary-user empty-grant denial.
- [x] Real PostgreSQL migration Up/Down test passed in an isolated local database, as reported by the coordinator. It covers legacy backfill and rollback credential narrowing; no production database was touched.
- [x] Independent review identified inactive issuer credential issuance and missing company-grant retry/superseded-invitation checks; the responsible implementers applied fixes and regression coverage.
- [x] Final coordinated unit suite: 4,046 passed, 4 skipped, 0 failed, exit 0. Final integration suite: 249 passed, 3 skipped, 0 failed, exit 0, including actual PostgreSQL migration, concurrent invitation acceptance and OAuth. Results reported by the backend coordinator after fixture corrections.
- [ ] Desktop/mobile browser layout and end-to-end journey validation, including switching with unsaved input and accepting a link after external login.
- [x] Coordinator final diff review, EF model consistency check (no pending changes) and Release Blazor WebAssembly build (0 warnings/errors).
- [ ] Draft PR review and merge through the normal release flow. No production deployment or package publication is part of this task.
