-- Persistent cursor for the monitored-conversation pre-generation watcher.
-- The watcher previously kept "last seen message set revision" and "needs prep" only in memory, so any
-- message that arrived while the application was down was never scheduled, and restart silently skipped it.
CREATE TABLE copilot.conversation_prep_state (
    account_id uuid NOT NULL,
    conversation_id uuid NOT NULL,
    last_seen_revision bigint NOT NULL DEFAULT 0 CHECK (last_seen_revision >= 0),
    needs_prep boolean NOT NULL DEFAULT false,
    updated_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (account_id, conversation_id),
    FOREIGN KEY (conversation_id, account_id)
        REFERENCES copilot.conversations(id, account_id) ON DELETE CASCADE
);
