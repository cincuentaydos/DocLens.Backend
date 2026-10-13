-- textract_jobs: correlates a Textract async job to the Step Functions task
-- token waiting on it. Textract's JobTag has a 64-char limit, too short for
-- a task token, so the token lives here instead, keyed by a UUID job_tag we
-- generate ourselves (see ExtractionWorkerHandler.StartTextractJobAsync /
-- TextractBridgeHandler).
CREATE TABLE textract_jobs (
    job_tag         uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    documento_id    uuid NOT NULL,
    version_numero  integer NOT NULL,
    task_token      text NOT NULL,
    tenant_id       text NOT NULL DEFAULT '',
    creado_en       timestamptz NOT NULL DEFAULT now()
);
