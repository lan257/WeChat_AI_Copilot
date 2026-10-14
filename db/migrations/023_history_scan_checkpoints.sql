-- Resumable history scan cursor (ADR-2026-10-14-02).
-- The cursor records where a scan has already been persisted, not merely where it was read to, so it is
-- written inside the ingestion transaction that accepts each batch and a crashed reader cannot advance it.
CREATE TABLE copilot.history_scan_checkpoints (
    account_id uuid NOT NULL,
    conversation_id uuid NOT NULL,
    window_key text NOT NULL CHECK (length(btrim(window_key)) > 0),
    message_table text NOT NULL CHECK (length(btrim(message_table)) > 0),
    last_local_id bigint NOT NULL DEFAULT 0 CHECK (last_local_id >= 0),
    scanned_rows integer NOT NULL DEFAULT 0 CHECK (scanned_rows >= 0),
    last_job_id text NOT NULL CHECK (length(btrim(last_job_id)) > 0),
    updated_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (account_id, conversation_id, window_key, message_table),
    FOREIGN KEY (conversation_id, account_id)
        REFERENCES copilot.conversations(id, account_id) ON DELETE CASCADE
);

-- A whole-local-store import is no longer bounded by the 90 day window, so the row cap that used to
-- size a bounded task has to grow. The reader keeps its own row guard as the real upper bound.
ALTER TABLE copilot.history_jobs DROP CONSTRAINT history_jobs_maximum_rows_check;
ALTER TABLE copilot.history_jobs ADD CONSTRAINT history_jobs_maximum_rows_check
    CHECK (maximum_rows BETWEEN 1 AND 200000);
