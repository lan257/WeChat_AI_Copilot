CREATE TABLE copilot.account_expression (
    account_id uuid PRIMARY KEY REFERENCES copilot.chat_accounts(id) ON DELETE CASCADE,
    voice text NOT NULL DEFAULT '' CHECK(char_length(voice) <= 4000),
    boundary text NOT NULL DEFAULT '' CHECK(char_length(boundary) <= 4000),
    notes text NOT NULL DEFAULT '' CHECK(char_length(notes) <= 4000),
    revision bigint NOT NULL DEFAULT 1,
    updated_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE copilot.contact_portraits (
    portrait_id uuid PRIMARY KEY,
    account_id uuid NOT NULL,
    conversation_id uuid NOT NULL,
    calibration_version bigint NOT NULL,
    message_set_revision text NOT NULL,
    revision bigint NOT NULL,
    sections jsonb NOT NULL,
    evidence_message_ids uuid[] NOT NULL,
    generated_at timestamptz NOT NULL DEFAULT now(),
    FOREIGN KEY (conversation_id,account_id) REFERENCES copilot.conversations(id,account_id) ON DELETE CASCADE,
    UNIQUE(account_id,conversation_id,revision)
);
CREATE INDEX ix_contact_portraits_current ON copilot.contact_portraits(account_id,conversation_id,revision DESC);
