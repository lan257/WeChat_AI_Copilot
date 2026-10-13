CREATE TABLE copilot.agent_tasks (
    task_id uuid PRIMARY KEY,
    account_id uuid NOT NULL,
    conversation_id uuid NOT NULL,
    task_type text NOT NULL CHECK (task_type IN ('ReplyCandidates','FreeConsultation','Portrait')),
    status text NOT NULL DEFAULT 'Queued' CHECK (status IN ('Queued','Running','CancelRequested','Completed','Failed','Cancelled')),
    phase text NOT NULL DEFAULT 'Queued',
    payload jsonb NOT NULL,
    fingerprint text NULL,
    result_id uuid NULL,
    error_code text NULL,
    timings jsonb NOT NULL DEFAULT '{}',
    idempotency_key text NULL CHECK (idempotency_key IS NULL OR char_length(idempotency_key) BETWEEN 1 AND 200),
    created_at timestamptz NOT NULL DEFAULT now(),
    started_at timestamptz NULL,
    ended_at timestamptz NULL,
    FOREIGN KEY (conversation_id, account_id) REFERENCES copilot.conversations(id, account_id) ON DELETE CASCADE,
    UNIQUE (account_id, conversation_id, idempotency_key)
);
CREATE INDEX ix_agent_tasks_queue ON copilot.agent_tasks(created_at, task_id) WHERE status = 'Queued';
CREATE INDEX ix_agent_tasks_scope ON copilot.agent_tasks(account_id,conversation_id,created_at DESC);
