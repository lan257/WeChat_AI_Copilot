ALTER TABLE copilot.agent_suggestion_batches
    ADD COLUMN execution_profile_id text NOT NULL DEFAULT 'Balanced'
    CHECK (execution_profile_id IN ('Quick', 'Balanced', 'Deep', 'Max'));
