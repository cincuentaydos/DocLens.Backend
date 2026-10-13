-- document_reviews (ADR-011 revision): the Review Step Functions' EnterReview
-- state persists its .waitForTaskToken token here; POST .../review resolves
-- it via SendTaskSuccess/Failure. Doubles as the human-review audit trail
-- (who approved/rejected what, when).
CREATE TABLE document_reviews (
    id              uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    documento_id    uuid NOT NULL,
    version_numero  integer NOT NULL,
    task_token      text NOT NULL,
    revisor_id      text,
    decision        text,
    comentario      text,
    creado_en       timestamptz NOT NULL DEFAULT now(),
    resuelto_en     timestamptz,
    FOREIGN KEY (documento_id, version_numero) REFERENCES documento_versiones (documento_id, version_numero)
);

-- Only one open (unresolved) review per document version at a time.
CREATE UNIQUE INDEX idx_document_reviews_pending
    ON document_reviews (documento_id, version_numero)
    WHERE resuelto_en IS NULL;
