-- documento_versiones: one row per uploaded version of a documento (ADR-005).
-- estado moves PENDING -> EXTRACTING -> PENDING_REVIEW -> APPROVED -> INDEXED,
-- or PENDING_REVIEW -> NEEDS_CORRECTION -> (reprocess) -> EXTRACTING again
-- (ADR-011 revision).
CREATE TABLE documento_versiones (
    id              uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    documento_id    uuid NOT NULL REFERENCES documentos(id),
    version_numero  integer NOT NULL,
    s3_key          text NOT NULL,
    content_type    text NOT NULL,
    sha256          text,
    estado          text NOT NULL DEFAULT 'PENDING',
    campos          jsonb,
    diff_previo     jsonb,
    procesado_en    timestamptz,
    creado_en       timestamptz NOT NULL DEFAULT now(),
    UNIQUE (documento_id, version_numero)
);
