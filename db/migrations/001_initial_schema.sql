CREATE SCHEMA IF NOT EXISTS copilot;

CREATE TABLE copilot.chat_accounts (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    provider text NOT NULL CHECK (length(btrim(provider)) > 0),
    external_account_id text NOT NULL CHECK (length(btrim(external_account_id)) > 0),
    display_name text NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (provider, external_account_id),
    UNIQUE (id, provider)
);

CREATE TABLE copilot.contacts (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    account_id uuid NOT NULL REFERENCES copilot.chat_accounts(id),
    external_contact_id text NOT NULL CHECK (length(btrim(external_contact_id)) > 0),
    display_name text NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (account_id, external_contact_id),
    UNIQUE (id, account_id)
);

CREATE TABLE copilot.conversations (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    account_id uuid NOT NULL REFERENCES copilot.chat_accounts(id),
    contact_id uuid NOT NULL,
    external_conversation_id text NOT NULL CHECK (length(btrim(external_conversation_id)) > 0),
    conversation_type text NOT NULL DEFAULT 'Direct' CHECK (conversation_type = 'Direct'),
    last_message_cursor text NULL,
    message_set_revision bigint NOT NULL DEFAULT 0 CHECK (message_set_revision >= 0),
    created_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (account_id, external_conversation_id),
    UNIQUE (id, account_id),
    FOREIGN KEY (contact_id, account_id)
        REFERENCES copilot.contacts(id, account_id)
);

CREATE TABLE copilot.chat_messages (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    account_id uuid NOT NULL,
    conversation_id uuid NOT NULL,
    provider text NOT NULL,
    message_type text NOT NULL CHECK (length(btrim(message_type)) > 0),
    text_content text NULL,
    source_occurred_at timestamptz NULL,
    ingested_at timestamptz NOT NULL DEFAULT now(),
    direction text NOT NULL DEFAULT 'Unknown'
        CHECK (direction IN ('Incoming', 'Outgoing', 'Unknown')),
    direction_evidence text NOT NULL DEFAULT 'Unresolved'
        CHECK (direction_evidence IN ('Unresolved', 'DefaultSmallSenderId', 'UserConfirmed', 'SourceConfirmed')),
    external_message_id text NULL,
    matching_strategy_version text NOT NULL DEFAULT 'wx4-legacy',
    UNIQUE (id, account_id, provider),
    FOREIGN KEY (account_id, provider)
        REFERENCES copilot.chat_accounts(id, provider),
    FOREIGN KEY (conversation_id, account_id)
        REFERENCES copilot.conversations(id, account_id),
    CHECK (message_type <> 'Text' OR text_content IS NOT NULL)
);

CREATE UNIQUE INDEX ux_chat_messages_provider_account_external_id
    ON copilot.chat_messages(provider, account_id, external_message_id)
    WHERE external_message_id IS NOT NULL;

CREATE INDEX ix_chat_messages_conversation_page
    ON copilot.chat_messages(
        conversation_id,
        source_occurred_at DESC NULLS LAST,
        ingested_at DESC,
        id DESC
    );

CREATE TABLE copilot.message_sources (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    message_id uuid NULL,
    account_id uuid NOT NULL,
    conversation_id uuid NOT NULL,
    provider text NOT NULL CHECK (length(btrim(provider)) > 0),
    source_kind text NOT NULL CHECK (source_kind IN ('Realtime', 'HistoryBackfill')),
    external_message_id text NULL,
    server_id text NULL,
    local_id text NULL,
    sort_seq text NULL,
    shard_fingerprint text NULL,
    message_table text NULL,
    raw_sender_id text NULL,
    identity_namespace text NULL,
    job_id text NULL,
    match_status text NOT NULL CHECK (match_status IN ('Matched', 'Created', 'NeedsReview')),
    source_occurred_at timestamptz NULL,
    ingested_at timestamptz NOT NULL DEFAULT now(),
    FOREIGN KEY (account_id, provider)
        REFERENCES copilot.chat_accounts(id, provider),
    FOREIGN KEY (conversation_id, account_id)
        REFERENCES copilot.conversations(id, account_id),
    FOREIGN KEY (message_id, account_id, provider)
        REFERENCES copilot.chat_messages(id, account_id, provider),
    CHECK (match_status = 'NeedsReview' OR message_id IS NOT NULL)
);

CREATE UNIQUE INDEX ux_message_sources_identity
    ON copilot.message_sources(
        provider,
        account_id,
        source_kind,
        external_message_id,
        COALESCE(message_table, ''),
        COALESCE(shard_fingerprint, ''),
        COALESCE(job_id, '')
    )
    WHERE external_message_id IS NOT NULL AND match_status IN ('Created', 'Matched');

CREATE UNIQUE INDEX ux_message_sources_review_identity
    ON copilot.message_sources(
        provider,
        account_id,
        source_kind,
        external_message_id,
        COALESCE(message_table, ''),
        COALESCE(shard_fingerprint, ''),
        COALESCE(job_id, '')
    )
    WHERE external_message_id IS NOT NULL AND match_status = 'NeedsReview';

CREATE INDEX ix_message_sources_review
    ON copilot.message_sources(account_id, conversation_id, ingested_at)
    WHERE match_status = 'NeedsReview';

CREATE TABLE copilot.import_reviews (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    account_id uuid NOT NULL,
    conversation_id uuid NOT NULL,
    review_key text NOT NULL,
    job_id text NULL,
    source_ref jsonb NOT NULL,
    reason_code text NOT NULL,
    status text NOT NULL DEFAULT 'Pending' CHECK (status IN ('Pending', 'Resolved', 'Ignored')),
    created_at timestamptz NOT NULL DEFAULT now(),
    resolved_at timestamptz NULL,
    UNIQUE (account_id, review_key),
    FOREIGN KEY (conversation_id, account_id)
        REFERENCES copilot.conversations(id, account_id)
);

CREATE TABLE copilot.history_jobs (
    job_id text NOT NULL CHECK (length(btrim(job_id)) > 0),
    account_id uuid NOT NULL,
    conversation_id uuid NOT NULL,
    requested_from_utc timestamptz NULL,
    requested_to_utc timestamptz NULL,
    status text NOT NULL CHECK (status IN ('Pending', 'Running', 'Completed', 'Partial', 'Failed', 'Cancelled')),
    read_count integer NOT NULL DEFAULT 0 CHECK (read_count >= 0),
    matched_count integer NOT NULL DEFAULT 0 CHECK (matched_count >= 0),
    created_count integer NOT NULL DEFAULT 0 CHECK (created_count >= 0),
    needs_review_count integer NOT NULL DEFAULT 0 CHECK (needs_review_count >= 0),
    ignored_unsupported_count integer NOT NULL DEFAULT 0 CHECK (ignored_unsupported_count >= 0),
    failed_count integer NOT NULL DEFAULT 0 CHECK (failed_count >= 0),
    is_last_batch boolean NOT NULL DEFAULT false,
    started_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    completed_at timestamptz NULL,
    PRIMARY KEY (account_id, conversation_id, job_id),
    FOREIGN KEY (conversation_id, account_id)
        REFERENCES copilot.conversations(id, account_id),
    CHECK (requested_from_utc IS NULL OR requested_to_utc IS NULL OR requested_from_utc <= requested_to_utc)
);

CREATE TABLE copilot.history_job_batches (
    account_id uuid NOT NULL,
    conversation_id uuid NOT NULL,
    job_id text NOT NULL,
    batch_key text NOT NULL,
    status text NOT NULL CHECK (status IN ('Completed', 'Partial', 'Failed')),
    read_count integer NOT NULL DEFAULT 0 CHECK (read_count >= 0),
    matched_count integer NOT NULL DEFAULT 0 CHECK (matched_count >= 0),
    created_count integer NOT NULL DEFAULT 0 CHECK (created_count >= 0),
    needs_review_count integer NOT NULL DEFAULT 0 CHECK (needs_review_count >= 0),
    ignored_unsupported_count integer NOT NULL DEFAULT 0 CHECK (ignored_unsupported_count >= 0),
    failed_count integer NOT NULL DEFAULT 0 CHECK (failed_count >= 0),
    is_last_batch boolean NOT NULL DEFAULT false,
    processed_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (account_id, conversation_id, job_id, batch_key),
    FOREIGN KEY (account_id, conversation_id, job_id)
        REFERENCES copilot.history_jobs(account_id, conversation_id, job_id)
);

CREATE TABLE copilot.sender_calibrations (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    account_id uuid NOT NULL,
    conversation_id uuid NOT NULL,
    identity_namespace text NOT NULL CHECK (length(btrim(identity_namespace)) > 0),
    message_table text NULL,
    shard_fingerprint text NULL,
    account_self_raw_sender_id text NULL,
    contact_raw_sender_id text NULL,
    calibration_version bigint NOT NULL CHECK (calibration_version > 0),
    perspective_state text NOT NULL CHECK (perspective_state IN ('Unresolved', 'PendingConfirmation', 'Confirmed')),
    evidence text NULL CHECK (evidence IS NULL OR evidence IN ('DefaultSmallSenderId', 'UserConfirmed')),
    confirmed_at timestamptz NULL,
    updated_at timestamptz NOT NULL DEFAULT now(),
    FOREIGN KEY (conversation_id, account_id)
        REFERENCES copilot.conversations(id, account_id),
    CHECK (
        (perspective_state = 'Unresolved'
            AND account_self_raw_sender_id IS NULL
            AND contact_raw_sender_id IS NULL
            AND evidence IS NULL)
        OR
        (perspective_state = 'PendingConfirmation'
            AND account_self_raw_sender_id IS NOT NULL
            AND contact_raw_sender_id IS NOT NULL
            AND evidence = 'DefaultSmallSenderId'
            AND confirmed_at IS NULL)
        OR
        (perspective_state = 'Confirmed'
            AND account_self_raw_sender_id IS NOT NULL
            AND contact_raw_sender_id IS NOT NULL
            AND evidence = 'UserConfirmed'
            AND confirmed_at IS NOT NULL)
    ),
    CHECK (account_self_raw_sender_id IS DISTINCT FROM contact_raw_sender_id)
);

CREATE UNIQUE INDEX ux_sender_calibrations_scope
    ON copilot.sender_calibrations(
        account_id,
        conversation_id,
        identity_namespace,
        COALESCE(message_table, ''),
        COALESCE(shard_fingerprint, '')
    );

CREATE TABLE copilot.sender_calibration_events (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    calibration_id uuid NOT NULL REFERENCES copilot.sender_calibrations(id),
    calibration_version bigint NOT NULL CHECK (calibration_version > 0),
    account_self_raw_sender_id text NOT NULL,
    contact_raw_sender_id text NOT NULL,
    idempotency_key text NOT NULL CHECK (length(btrim(idempotency_key)) > 0),
    affected_message_count bigint NOT NULL DEFAULT 0 CHECK (affected_message_count >= 0),
    occurred_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (calibration_id, calibration_version),
    UNIQUE (calibration_id, idempotency_key)
);

CREATE TABLE copilot.conversation_change_outbox (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    account_id uuid NOT NULL,
    conversation_id uuid NOT NULL,
    event_type text NOT NULL CHECK (event_type IN (
        'message.created',
        'conversation.perspective-changed',
        'conversation.perspective-confirmed'
    )),
    message_cursor text NULL,
    calibration_version bigint NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    published_at timestamptz NULL,
    FOREIGN KEY (conversation_id, account_id)
        REFERENCES copilot.conversations(id, account_id)
);

CREATE INDEX ix_conversation_change_outbox_pending
    ON copilot.conversation_change_outbox(created_at, id)
    WHERE published_at IS NULL;
