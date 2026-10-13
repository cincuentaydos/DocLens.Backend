-- pii_mappings (ADR-014): stable placeholder -> real value substitutions
-- found by DetectAndRedactPII, reversed only at chat-response time for an
-- authorized reader. The redacted text (placeholders only) is what ever
-- reaches the KB source bucket / Bedrock Knowledge Base.
CREATE TABLE pii_mappings (
    id            uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    documento_id  uuid NOT NULL REFERENCES documentos(id),
    placeholder   text NOT NULL,
    valor_real    text NOT NULL,
    tipo_entidad  text NOT NULL,
    creado_en     timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX idx_pii_mappings_documento_id ON pii_mappings (documento_id);
