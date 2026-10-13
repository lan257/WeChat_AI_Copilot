CREATE TABLE copilot.reader_directory_jobs (
    job_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    account_id uuid NOT NULL REFERENCES copilot.chat_accounts(id),
    status text NOT NULL CHECK (status IN ('Pending', 'Running', 'Completed', 'Failed')),
    discovered_count integer NOT NULL DEFAULT 0 CHECK (discovered_count BETWEEN 0 AND 2000),
    error_code text NULL CHECK (error_code IS NULL OR error_code ~ '^[A-Z0-9_]{1,64}$'),
    created_at timestamptz NOT NULL DEFAULT now(),
    started_at timestamptz NULL,
    completed_at timestamptz NULL
);

CREATE INDEX ix_reader_directory_jobs_pending
    ON copilot.reader_directory_jobs(account_id, created_at)
    WHERE status = 'Pending';
