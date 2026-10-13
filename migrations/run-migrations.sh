#!/usr/bin/env bash
# Applies migrations/*.sql against Aurora via the RDS Data API, in filename
# order, tracking what's already run in a schema_migrations table (created
# here on first run). No VPC networking, no ORM — consistent with ADR-008's
# "RDS Data API only" decision, and with the rest of the codebase.
#
# ExecuteStatement runs exactly one SQL statement per call ("Multistatements
# aren't supported" — confirmed against the real API, not documented
# clearly), so each file is split on top-level semicolons before being sent.
# Keep migration files free of semicolons inside string literals or
# dollar-quoted bodies (plain DDL only, no PL/pgSQL functions) or this
# splitting will cut a statement in half.
set -euo pipefail

: "${AURORA_CLUSTER_ARN:?AURORA_CLUSTER_ARN is required}"
: "${AURORA_SECRET_ARN:?AURORA_SECRET_ARN is required}"
: "${AURORA_DATABASE:=doclens}"
: "${AWS_REGION:=eu-west-1}"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

exec_sql() {
  aws rds-data execute-statement \
    --region "$AWS_REGION" \
    --resource-arn "$AURORA_CLUSTER_ARN" \
    --secret-arn "$AURORA_SECRET_ARN" \
    --database "$AURORA_DATABASE" \
    --sql "$1"
}

query_sql() {
  aws rds-data execute-statement \
    --region "$AWS_REGION" \
    --resource-arn "$AURORA_CLUSTER_ARN" \
    --secret-arn "$AURORA_SECRET_ARN" \
    --database "$AURORA_DATABASE" \
    --sql "$1" \
    --query "records[*][0].stringValue" \
    --output text
}

echo "Ensuring schema_migrations table exists..."
exec_sql "CREATE TABLE IF NOT EXISTS schema_migrations (filename text PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now())" > /dev/null

# aws cli's `--output text` joins a flat list with tabs, not newlines — turn
# it back into one filename per line so the exact-match grep below works.
applied="$(query_sql "SELECT filename FROM schema_migrations" | tr '\t' '\n')"

for file in "$SCRIPT_DIR"/*.sql; do
  name="$(basename "$file")"

  if grep -qxF "$name" <<< "$applied"; then
    echo "Skipping $name (already applied)"
    continue
  fi

  echo "Applying $name..."
  sql_body="$(grep -v '^--' "$file")"
  # `read -d ';'` (not the default newline) is what lets a single "read" span
  # a multi-line CREATE TABLE statement instead of stopping at its first
  # newline, which is what `read -ra` with only IFS=';' would otherwise do.
  while IFS= read -r -d ';' stmt || [ -n "$stmt" ]; do
    trimmed="$(echo "$stmt" | sed 's/^[[:space:]]*//;s/[[:space:]]*$//')"
    [ -z "$trimmed" ] && continue
    exec_sql "$trimmed" > /dev/null
  done <<< "$sql_body"

  exec_sql "INSERT INTO schema_migrations (filename) VALUES ('$name')" > /dev/null
  echo "Applied $name"
done

echo "All migrations up to date."
