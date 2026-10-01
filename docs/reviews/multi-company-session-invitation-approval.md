# Decision record: company sessions and existing-account invitations

Date: 2026-10-01. Status: the user explicitly approved the narrow company switch/refresh and existing-account invitation flows; these components are integrated.

`CompanySessionService` is registered and serves `/api/my-companies/switch` and `/api/auth/refresh`. It requires an interactive authenticated identity and checks live company membership, effective role and provisioning readiness before signing the replacement JWT. It never changes `User.CompanyId`, passwords or machine grants. API keys and OAuth cannot use it to obtain browser credentials.

`MyCompaniesController` and `CompanyMembershipService` implement company listing, idempotent creation, provisioning recovery and invitations. SysAdmin may invite an active existing account. Tokens are random, hashed at rest, expire after 48 hours and are single-use; resend invalidates previous pending links. The logged-in invited identity must accept. Bilingual email and a one-time returned token support link delivery without password reset. Machine provisioning retries require an explicit grant for the target company.

Automatic approval review rejected a broader shared-signing refactor that also changed AuthService login/default-company recovery. That expansion was not applied. Password, external and two-factor login retain their existing initial metadata/signing behavior. The approved narrow session service handles switch/refresh, and live middleware enforces current membership on subsequent requests. A browser whose default membership was revoked may list remaining memberships and explicitly switch through recovery routes.

Validation: full unit suite 4,046 passed, 4 skipped, 0 failed; full integration suite 249 passed, 3 skipped, 0 failed. Both exited successfully, including actual PostgreSQL migration, concurrent invitation acceptance, OAuth company binding and session/credential regression checks. Focused UI/MCP/login checks passed 89 tests; subsequent integrations UI checks passed 22. Browser visual/end-to-end checks were not run.

No production deployment, production database migration or package publication was performed.
