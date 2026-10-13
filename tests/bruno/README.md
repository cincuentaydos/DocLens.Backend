# DocLens API — Bruno collection

Open this folder directly in [Bruno](https://www.usebruno.com/) (File → Open Collection).

## Setup

1. Select the **sbx** environment (top-right dropdown).
2. Click the environment, open **Configure**, and fill in `testPassword` under
   the secret variables — value is in the team's password manager, not
   committed here on purpose (see `environments/sbx.bru`).

## Running the smoke test (ADR-011 pipeline, since 2026-09-28)

Run in order — each request after **Get Token** depends on environment
variables the previous one sets (`idToken`, `documentId`, `versionNumber`,
`uploadUrl`).

1. **Auth / Get Token** — logs in as the `test@doclens.org` Cognito test user
   (`custom:tenantId=test-tenant`), stores `idToken`.
2. **Smoke / Swagger Health** — unauthenticated reachability check.
3. **Smoke / Prepare Upload** — `POST /documents/prepare`. Stores
   `documentId`, `versionNumber`, `uploadUrl`.
4. **Smoke / Upload File to S3** — `PUT` a small plain-text fixture straight
   to the presigned URL (ADR-005, no auth header — the URL itself is the
   credential). This is what actually kicks off the pipeline: S3 → GuardDuty
   → EventBridge → the `doclens-document-extraction-{env}` Step Functions.
5. **Smoke / Get Document Version** — `GET .../versions/{n}`. Bruno has no
   built-in poll/retry, so re-run this one by hand to watch `estado` move
   `PENDING` → `EXTRACTING` → `PENDING_REVIEW` → `APPROVED` → `INDEXED`.

   **As of 2026-09-28, expect it to stall at `EXTRACTING`** — the account's
   Bedrock Haiku 4.5 quota is still 0 (a pre-existing new-account
   restriction, see `docs/infra-deployment-log.md`), and `ClassifyDocument`
   can't call `bedrock:InvokeModel` until that's resolved. Everything up to
   there (S3, GuardDuty, EventBridge, the state machine itself, native-text
   extraction) is real signal even while this is stuck.
6. **Smoke / Resolve Review** — `POST .../review`. Only resolves (200) once
   the document actually reaches `PENDING_REVIEW`; a 404 just means it's
   not there yet (expected while Bedrock is blocked).
7. **Smoke / Request Reprocess** — `POST .../reprocess`. Only meaningful for
   a version in `NEEDS_CORRECTION` — not part of the default run.
8. **Chat / Ask** — `POST /procesos/{procesoId}/chat`. Needs Bedrock
   unblocked *and* at least one document already `INDEXED` for the test
   tenant. Also a reminder: `IChatService` only isolates by tenant today,
   not by `proceso_id` (ADR-001's full isolation needs the `procesos`/
   `permisos` tables, which don't exist yet) — don't read a 200 here as
   that gap being closed.

## What each stage actually proves

Even fully blocked by the Bedrock quota, a successful run through step 5
(reaching `EXTRACTING` and staying there) validates: the JWT authorizer,
`TenantMiddleware`, the presigned-upload flow, GuardDuty's scan completing,
the EventBridge retarget to Step Functions, the `doclens-extraction-worker`
Lambda's `DetectFormat` action, and its S3 write to `doclens-kb-source-*` —
i.e. everything in the pipeline except the two Bedrock-calling steps.
