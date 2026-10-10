CREATE TABLE copilot.conversation_change_outbox_deliveries (
    consumer_name text NOT NULL CHECK (length(btrim(consumer_name)) > 0),
    event_id uuid NOT NULL
        REFERENCES copilot.conversation_change_outbox(id) ON DELETE CASCADE,
    attempts integer NOT NULL DEFAULT 0 CHECK (attempts >= 0),
    next_retry_at timestamptz NULL,
    lease_id uuid NULL,
    lease_until timestamptz NULL,
    last_error_code text NULL
        CHECK (last_error_code IS NULL OR length(btrim(last_error_code)) BETWEEN 1 AND 96),
    completed_at timestamptz NULL,
    updated_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (consumer_name, event_id),
    CHECK ((lease_id IS NULL) = (lease_until IS NULL)),
    CHECK (lease_id IS NULL OR next_retry_at IS NULL),
    CHECK (completed_at IS NULL OR (lease_id IS NULL AND next_retry_at IS NULL))
);

CREATE INDEX ix_conversation_change_outbox_delivery_claimable
    ON copilot.conversation_change_outbox_deliveries(
        consumer_name,
        next_retry_at,
        lease_until,
        event_id)
    WHERE completed_at IS NULL;

CREATE INDEX ix_conversation_change_outbox_scope_order
    ON copilot.conversation_change_outbox(account_id, conversation_id, created_at, id);
