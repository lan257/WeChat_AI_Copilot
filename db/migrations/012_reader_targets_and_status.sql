CREATE TABLE copilot.reader_target_sets (
    account_id uuid PRIMARY KEY REFERENCES copilot.chat_accounts(id),
    target_version bigint NOT NULL DEFAULT 0 CHECK (target_version >= 0),
    updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE copilot.monitored_conversation_targets (
    account_id uuid NOT NULL,
    conversation_id uuid NOT NULL,
    added_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (account_id, conversation_id),
    FOREIGN KEY (account_id) REFERENCES copilot.chat_accounts(id),
    FOREIGN KEY (conversation_id, account_id)
        REFERENCES copilot.conversations(id, account_id)
);

CREATE TABLE copilot.reader_status (
    account_id uuid PRIMARY KEY REFERENCES copilot.chat_accounts(id),
    reader_instance_id uuid NOT NULL,
    state text NOT NULL CHECK (state IN ('Starting', 'Listening', 'Idle', 'Error', 'Stopped')),
    target_version bigint NOT NULL DEFAULT 0 CHECK (target_version >= 0),
    listening_target_count integer NOT NULL DEFAULT 0 CHECK (listening_target_count BETWEEN 0 AND 20),
    last_error_code text NULL CHECK (
        last_error_code IS NULL OR last_error_code ~ '^[A-Z0-9_]{1,64}$'
    ),
    started_at timestamptz NOT NULL,
    heartbeat_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX ix_reader_status_heartbeat
    ON copilot.reader_status(heartbeat_at DESC);
