ALTER TABLE copilot.history_jobs ADD COLUMN maximum_rows integer NOT NULL DEFAULT 5000 CHECK (maximum_rows BETWEEN 1 AND 5000);
ALTER TABLE copilot.history_jobs ADD COLUMN requested_by_web boolean NOT NULL DEFAULT false;
ALTER TABLE copilot.history_jobs ADD COLUMN claimed_at timestamptz;
ALTER TABLE copilot.history_jobs ADD COLUMN error_code text;
CREATE INDEX history_jobs_pending_web ON copilot.history_jobs (started_at) WHERE requested_by_web AND status IN ('Pending','Running');
CREATE TABLE copilot.application_preferences (
 preference_key text PRIMARY KEY,
 preference_json jsonb NOT NULL,
 updated_at timestamptz NOT NULL DEFAULT now()
);
