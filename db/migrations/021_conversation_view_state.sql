CREATE TABLE copilot.conversation_view_state (
    viewer_key text NOT NULL,
    account_id uuid NOT NULL,
    conversation_id uuid NOT NULL,
    last_read_revision bigint NOT NULL DEFAULT 0,
    last_seen_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY(viewer_key,account_id,conversation_id),
    FOREIGN KEY(conversation_id,account_id) REFERENCES copilot.conversations(id,account_id) ON DELETE CASCADE
);
