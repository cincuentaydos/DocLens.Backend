-- documentos: one row per logical document; all its versions share this id.
CREATE TABLE documentos (
    id              uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    empresa_id      text NOT NULL,
    latest_version  integer NOT NULL DEFAULT 0,
    creado_en       timestamptz NOT NULL DEFAULT now(),
    actualizado_en  timestamptz NOT NULL DEFAULT now()
);
