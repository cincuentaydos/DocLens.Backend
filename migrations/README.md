# Aurora migrations

Versioned, numbered SQL files for the `doclens` Aurora Postgres schema — applied via the RDS Data API (`run-migrations.sh`), no ORM, no VPC networking, consistent with ADR-008.

## Adding a migration

1. Create the next-numbered file: `NNNN_short_description.sql` (four digits, zero-padded, sequential — never reuse or renumber an already-applied number).
2. Write plain DDL only — no PL/pgSQL functions or anything else that needs a semicolon inside a string or a dollar-quoted body. The runner splits each file on `;` and sends one statement per `ExecuteStatement` call (the Data API doesn't support multi-statement calls), so an embedded `;` inside a statement will cut it in half.
3. Never edit a migration that's already been applied to `sbx` or `prod` — add a new one instead, even to fix a mistake.

## Running

```bash
export AURORA_CLUSTER_ARN=arn:aws:rds:eu-west-1:<account>:cluster:doclens-sbx
export AURORA_SECRET_ARN=arn:aws:secretsmanager:eu-west-1:<account>:secret:...
export AURORA_DATABASE=doclens   # default, only needed to override
./run-migrations.sh
```

Applied filenames are tracked in a `schema_migrations` table (created automatically on first run) — re-running is always safe, already-applied files are skipped.

Runs automatically in `cd.yml` (GitHub Actions) before the Lambda code deploy, using the `AURORA_CLUSTER_ARN`/`AURORA_SECRET_ARN` repo variables.

## Rollback: roll-forward only, no `down.sql`

There is no down-migration mechanism, and that's deliberate, not an oversight — a scripted rollback for something like `DROP COLUMN` can destroy data written after the migration ran, which is worse than the bug it was reverting. If a migration causes a problem, write a new migration that fixes it forward (e.g. a `NNNN_fix_x.sql` that adjusts what `NNNN-1` got wrong), never edit or "undo" the original.

This also means `.github/workflows/rollback.yml` (which redeploys a previous Lambda build) never touches the schema — code can go backward, the schema only ever goes forward. Check that the commit you're rolling back to is still compatible with whatever migrations have already run before using it; if it isn't, fix forward instead of rolling back.

## History

`0001`-`0005` are a retroactive snapshot of the schema as it already existed in `sbx` before this migrations setup existed — those 5 tables were created by hand via `aws rds-data execute-statement` across earlier sessions (see `docs/infra-deployment-log.md` in `DocLens.Docs`). They were marked as already-applied in `sbx`'s `schema_migrations` table at the same time this runner was introduced, rather than re-run.
