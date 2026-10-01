# Decision record: live company-membership enforcement

Date: 2026-10-01. Status: explicitly approved by the user and integrated after the initial automatic approval review required a concrete registration decision.

`CompanyMembershipMiddleware` is registered in `Fakvio.API/Program.cs` after authentication and before authorization. It checks the persisted active identity, selected active issuer membership and effective company role before endpoint policies run. Manual API-key company selection is limited to explicit stored grants and live membership; OAuth cannot override its consent company. Persisted SysAdmin authority and existing impersonation remain separate.

`TenantContextMiddleware` recognizes `/api/my-companies` as a master-only boundary. These endpoints enforce their own ownership checks. Explicit identity-recovery routes permit an active browser identity to list remaining memberships and switch after default-membership revocation, without authorizing data access to the revoked company. Scoped machine credentials cannot mint browser sessions or accept membership invitations.

The broader shared AuthService/login recovery refactor was deferred after automatic approval review rejected that expanded change. Existing initial login/default metadata remains unchanged; the integrated middleware is authoritative for subsequent requests. The approved narrow switch/refresh implementation is recorded separately.

Validation: full unit suite 4,046 passed, 4 skipped, 0 failed; full integration suite 249 passed, 3 skipped, 0 failed. Both exited successfully and include live membership, credential restrictions, real PostgreSQL migration and concurrent invitation coverage. Browser visual/end-to-end checks were not run.

This decision includes no production deployment, production database migration or package publication.
