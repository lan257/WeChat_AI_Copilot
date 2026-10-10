CREATE TABLE copilot.agent_suggestion_batches (
    batch_id uuid PRIMARY KEY,
    run_id uuid NOT NULL UNIQUE,
    account_id uuid NOT NULL,
    conversation_id uuid NOT NULL,
    persona_preset_id text NOT NULL CHECK (length(btrim(persona_preset_id)) > 0),
    context_revision text NOT NULL CHECK (length(btrim(context_revision)) > 0),
    status text NOT NULL CHECK (status = 'Completed'),
    created_at timestamptz NOT NULL,
    UNIQUE (batch_id, account_id, conversation_id),
    FOREIGN KEY (conversation_id, account_id)
        REFERENCES copilot.conversations(id, account_id)
);

CREATE INDEX ix_agent_suggestion_batches_scope_created
    ON copilot.agent_suggestion_batches(account_id, conversation_id, created_at DESC, batch_id DESC);

CREATE TABLE copilot.agent_suggestions (
    suggestion_id uuid PRIMARY KEY,
    batch_id uuid NOT NULL,
    account_id uuid NOT NULL,
    conversation_id uuid NOT NULL,
    ordinal smallint NOT NULL CHECK (ordinal BETWEEN 1 AND 3),
    text_content text NOT NULL CHECK (length(btrim(text_content)) > 0),
    UNIQUE (batch_id, ordinal),
    FOREIGN KEY (batch_id, account_id, conversation_id)
        REFERENCES copilot.agent_suggestion_batches(batch_id, account_id, conversation_id)
        ON DELETE CASCADE
);

CREATE TABLE copilot.agent_consultation_threads (
    thread_id uuid PRIMARY KEY,
    account_id uuid NOT NULL,
    conversation_id uuid NOT NULL,
    title text NULL,
    created_at timestamptz NOT NULL,
    UNIQUE (thread_id, account_id, conversation_id),
    FOREIGN KEY (conversation_id, account_id)
        REFERENCES copilot.conversations(id, account_id)
);

CREATE INDEX ix_agent_consultation_threads_scope_created
    ON copilot.agent_consultation_threads(account_id, conversation_id, created_at DESC, thread_id DESC);

CREATE TABLE copilot.agent_consultation_turns (
    run_id uuid PRIMARY KEY,
    thread_id uuid NOT NULL,
    account_id uuid NOT NULL,
    conversation_id uuid NOT NULL,
    user_input text NOT NULL CHECK (length(btrim(user_input)) > 0),
    answer text NOT NULL CHECK (length(btrim(answer)) > 0),
    evidence_message_ids jsonb NOT NULL DEFAULT '[]'::jsonb
        CHECK (jsonb_typeof(evidence_message_ids) = 'array'),
    status text NOT NULL CHECK (status = 'Completed'),
    created_at timestamptz NOT NULL,
    FOREIGN KEY (thread_id, account_id, conversation_id)
        REFERENCES copilot.agent_consultation_threads(thread_id, account_id, conversation_id)
        ON DELETE CASCADE
);

CREATE INDEX ix_agent_consultation_turns_thread_created
    ON copilot.agent_consultation_turns(thread_id, created_at, run_id);
