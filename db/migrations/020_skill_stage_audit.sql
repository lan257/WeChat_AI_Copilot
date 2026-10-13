CREATE TABLE copilot.skill_stage_runs (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    account_id uuid NOT NULL,
    conversation_id uuid NOT NULL,
    skill_id text NOT NULL REFERENCES copilot.skills(skill_id),
    revision bigint NOT NULL CHECK (revision > 0),
    stage text NOT NULL CHECK (stage IN ('message','context','reply','review','consult','memory')),
    revision_digest text NOT NULL CHECK (length(revision_digest) = 64),
    occurred_at timestamptz NOT NULL DEFAULT now(),
    FOREIGN KEY(conversation_id,account_id) REFERENCES copilot.conversations(id,account_id)
);
CREATE INDEX ix_skill_stage_runs_scope ON copilot.skill_stage_runs(account_id,conversation_id,skill_id,occurred_at DESC,id DESC);
