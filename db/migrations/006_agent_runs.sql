CREATE TABLE copilot.agent_runs (
    run_id uuid PRIMARY KEY,
    task_type text NOT NULL CHECK (task_type IN ('ReplyCandidates', 'FreeConsultation')),
    account_id uuid NOT NULL,
    conversation_id uuid NOT NULL,
    runtime_name text NOT NULL CHECK (length(btrim(runtime_name)) BETWEEN 1 AND 100),
    skill_version text NOT NULL CHECK (length(btrim(skill_version)) BETWEEN 1 AND 200),
    context_revision text NOT NULL CHECK (length(btrim(context_revision)) BETWEEN 1 AND 256),
    status text NOT NULL CHECK (status IN ('Running', 'Completed', 'Failed', 'Cancelled')),
    started_at timestamptz NOT NULL,
    ended_at timestamptz NULL,
    error_code text NULL CHECK (error_code IS NULL OR error_code ~ '^[A-Z0-9_]{1,80}$'),
    budget_metadata jsonb NOT NULL DEFAULT '{}'::jsonb
        CHECK (jsonb_typeof(budget_metadata) = 'object'),
    UNIQUE (run_id, account_id, conversation_id),
    FOREIGN KEY (conversation_id, account_id)
        REFERENCES copilot.conversations(id, account_id),
    CHECK (
        (status = 'Running' AND ended_at IS NULL AND error_code IS NULL)
        OR (status = 'Completed' AND ended_at IS NOT NULL AND error_code IS NULL)
        OR (status IN ('Failed', 'Cancelled') AND ended_at IS NOT NULL AND error_code IS NOT NULL)
    )
);

CREATE INDEX ix_agent_runs_scope_started
    ON copilot.agent_runs(account_id, conversation_id, started_at DESC, run_id DESC);

-- Existing batches/turns predate run auditing. NOT VALID preserves those rows
-- while enforcing run/scope pairing for every new result written after this migration.
ALTER TABLE copilot.agent_suggestion_batches
    ADD CONSTRAINT fk_agent_suggestion_batches_run_scope
    FOREIGN KEY (run_id, account_id, conversation_id)
    REFERENCES copilot.agent_runs(run_id, account_id, conversation_id)
    NOT VALID;

ALTER TABLE copilot.agent_consultation_turns
    ADD CONSTRAINT fk_agent_consultation_turns_run_scope
    FOREIGN KEY (run_id, account_id, conversation_id)
    REFERENCES copilot.agent_runs(run_id, account_id, conversation_id)
    NOT VALID;
