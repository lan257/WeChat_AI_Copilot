ALTER TABLE copilot.memory_items
    ADD COLUMN identity_namespace text NULL;

ALTER TABLE copilot.memory_items
    ADD CONSTRAINT ck_memory_items_identity_namespace
    CHECK (
        (identity_namespace IS NULL OR length(btrim(identity_namespace)) > 0)
        AND (
            (origin = 'UserEntered' AND identity_namespace IS NULL)
            OR (origin = 'MessageExtracted' AND identity_namespace IS NOT NULL)
        )
    );

CREATE INDEX ix_memory_items_message_extracted_scope
    ON copilot.memory_items(
        account_id,
        conversation_id,
        identity_namespace,
        calibration_version,
        updated_at DESC,
        id)
    WHERE status = 'Active' AND origin = 'MessageExtracted';

CREATE TABLE copilot.memory_consolidation_state (
    account_id uuid NOT NULL,
    conversation_id uuid NOT NULL,
    identity_namespace text NOT NULL CHECK (length(btrim(identity_namespace)) > 0),
    calibration_version bigint NOT NULL CHECK (calibration_version > 0),
    message_set_revision text NOT NULL CHECK (length(btrim(message_set_revision)) > 0),
    consolidation_source_message_cursor text NULL,
    observed_message_cursor text NULL,
    unprofiled_text_characters integer NOT NULL DEFAULT 0 CHECK (unprofiled_text_characters >= 0),
    initial_consolidation_completed boolean NOT NULL DEFAULT false,
    reprocess_required boolean NOT NULL DEFAULT false,
    reprocess_required_revision text NULL,
    profile_status text NOT NULL DEFAULT 'FirstConsolidation'
        CHECK (profile_status IN (
            'WaitingForConfirmation',
            'FirstConsolidation',
            'Accumulating',
            'Consolidating',
            'Rebuilding',
            'Current',
            'Failed')),
    state_version bigint NOT NULL DEFAULT 1 CHECK (state_version > 0),
    active_batch_id uuid NULL,
    active_lease_until timestamptz NULL,
    updated_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (account_id, conversation_id, identity_namespace, calibration_version),
    FOREIGN KEY (conversation_id, account_id)
        REFERENCES copilot.conversations(id, account_id) ON DELETE CASCADE,
    CHECK ((active_batch_id IS NULL) = (active_lease_until IS NULL))
);

CREATE TABLE copilot.memory_consolidation_batches (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    account_id uuid NOT NULL,
    conversation_id uuid NOT NULL,
    identity_namespace text NOT NULL CHECK (length(btrim(identity_namespace)) > 0),
    calibration_version bigint NOT NULL CHECK (calibration_version > 0),
    message_set_revision text NOT NULL CHECK (length(btrim(message_set_revision)) > 0),
    high_water_message_cursor text NOT NULL CHECK (length(btrim(high_water_message_cursor)) > 0),
    batch_kind text NOT NULL CHECK (batch_kind IN ('Initial', 'Incremental', 'Rebuild')),
    eligible_message_count integer NOT NULL CHECK (eligible_message_count >= 0),
    eligible_text_code_points integer NOT NULL CHECK (eligible_text_code_points >= 0),
    completed_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (
        account_id,
        conversation_id,
        identity_namespace,
        calibration_version,
        message_set_revision,
        high_water_message_cursor),
    FOREIGN KEY (account_id, conversation_id, identity_namespace, calibration_version)
        REFERENCES copilot.memory_consolidation_state(
            account_id, conversation_id, identity_namespace, calibration_version)
        ON DELETE CASCADE
);

CREATE INDEX ix_memory_consolidation_batches_scope_recent
    ON copilot.memory_consolidation_batches(
        account_id,
        conversation_id,
        identity_namespace,
        calibration_version,
        completed_at DESC);

CREATE TABLE copilot.participant_profile_snapshots (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    account_id uuid NOT NULL,
    conversation_id uuid NOT NULL,
    identity_namespace text NOT NULL CHECK (length(btrim(identity_namespace)) > 0),
    subject_type text NOT NULL CHECK (subject_type IN ('WechatAccount', 'Contact')),
    subject_id uuid NOT NULL,
    profile_revision bigint NOT NULL CHECK (profile_revision > 0),
    calibration_version bigint NOT NULL CHECK (calibration_version > 0),
    status text NOT NULL CHECK (status IN ('Current', 'Superseded')),
    summary text NOT NULL CHECK (length(btrim(summary)) > 0 AND char_length(summary) <= 4000),
    source_message_cursor text NOT NULL CHECK (length(btrim(source_message_cursor)) > 0),
    generated_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (id, account_id, conversation_id),
    UNIQUE (
        account_id,
        conversation_id,
        identity_namespace,
        subject_type,
        subject_id,
        calibration_version,
        profile_revision),
    FOREIGN KEY (conversation_id, account_id)
        REFERENCES copilot.conversations(id, account_id) ON DELETE CASCADE,
    FOREIGN KEY (account_id, conversation_id, identity_namespace, calibration_version)
        REFERENCES copilot.memory_consolidation_state(
            account_id, conversation_id, identity_namespace, calibration_version)
        ON DELETE CASCADE
);

CREATE INDEX ix_participant_profile_current_scope
    ON copilot.participant_profile_snapshots(
        account_id,
        conversation_id,
        identity_namespace,
        calibration_version,
        subject_type,
        subject_id,
        profile_revision DESC)
    WHERE status = 'Current';

CREATE TABLE copilot.participant_profile_evidence (
    profile_id uuid NOT NULL,
    account_id uuid NOT NULL,
    conversation_id uuid NOT NULL,
    message_id uuid NOT NULL,
    ordinal integer NOT NULL CHECK (ordinal >= 0),
    created_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (profile_id, message_id),
    UNIQUE (profile_id, ordinal),
    FOREIGN KEY (profile_id, account_id, conversation_id)
        REFERENCES copilot.participant_profile_snapshots(id, account_id, conversation_id)
        ON DELETE CASCADE,
    FOREIGN KEY (message_id, account_id, conversation_id)
        REFERENCES copilot.chat_messages(id, account_id, conversation_id)
        ON DELETE CASCADE
);

CREATE INDEX ix_participant_profile_evidence_message_id
    ON copilot.participant_profile_evidence(message_id);

CREATE INDEX ix_participant_profile_evidence_scope
    ON copilot.participant_profile_evidence(profile_id, account_id, conversation_id);

CREATE TABLE copilot.memory_consolidation_event_receipts (
    event_id uuid PRIMARY KEY,
    account_id uuid NOT NULL,
    conversation_id uuid NOT NULL,
    identity_namespace text NOT NULL CHECK (length(btrim(identity_namespace)) > 0),
    calibration_version bigint NOT NULL CHECK (calibration_version > 0),
    outcome text NOT NULL CHECK (outcome IN ('Accumulated', 'Consolidated', 'NoOp')),
    processed_at timestamptz NOT NULL DEFAULT now(),
    FOREIGN KEY (account_id, conversation_id, identity_namespace, calibration_version)
        REFERENCES copilot.memory_consolidation_state(
            account_id, conversation_id, identity_namespace, calibration_version)
        ON DELETE CASCADE
);

ALTER TABLE copilot.memory_consolidation_state ENABLE ROW LEVEL SECURITY;
ALTER TABLE copilot.memory_consolidation_state FORCE ROW LEVEL SECURITY;
CREATE POLICY memory_consolidation_state_authorized_scope ON copilot.memory_consolidation_state
    FOR ALL
    USING (
        account_id = NULLIF(current_setting('copilot.account_id', true), '')::uuid
        AND conversation_id = NULLIF(current_setting('copilot.conversation_id', true), '')::uuid
    )
    WITH CHECK (
        account_id = NULLIF(current_setting('copilot.account_id', true), '')::uuid
        AND conversation_id = NULLIF(current_setting('copilot.conversation_id', true), '')::uuid
    );

ALTER TABLE copilot.memory_consolidation_batches ENABLE ROW LEVEL SECURITY;
ALTER TABLE copilot.memory_consolidation_batches FORCE ROW LEVEL SECURITY;
CREATE POLICY memory_consolidation_batches_authorized_scope ON copilot.memory_consolidation_batches
    FOR ALL
    USING (
        account_id = NULLIF(current_setting('copilot.account_id', true), '')::uuid
        AND conversation_id = NULLIF(current_setting('copilot.conversation_id', true), '')::uuid
    )
    WITH CHECK (
        account_id = NULLIF(current_setting('copilot.account_id', true), '')::uuid
        AND conversation_id = NULLIF(current_setting('copilot.conversation_id', true), '')::uuid
    );

ALTER TABLE copilot.participant_profile_snapshots ENABLE ROW LEVEL SECURITY;
ALTER TABLE copilot.participant_profile_snapshots FORCE ROW LEVEL SECURITY;
CREATE POLICY participant_profile_snapshots_authorized_scope ON copilot.participant_profile_snapshots
    FOR ALL
    USING (
        account_id = NULLIF(current_setting('copilot.account_id', true), '')::uuid
        AND conversation_id = NULLIF(current_setting('copilot.conversation_id', true), '')::uuid
    )
    WITH CHECK (
        account_id = NULLIF(current_setting('copilot.account_id', true), '')::uuid
        AND conversation_id = NULLIF(current_setting('copilot.conversation_id', true), '')::uuid
    );

ALTER TABLE copilot.participant_profile_evidence ENABLE ROW LEVEL SECURITY;
ALTER TABLE copilot.participant_profile_evidence FORCE ROW LEVEL SECURITY;
CREATE POLICY participant_profile_evidence_authorized_scope ON copilot.participant_profile_evidence
    FOR ALL
    USING (
        account_id = NULLIF(current_setting('copilot.account_id', true), '')::uuid
        AND conversation_id = NULLIF(current_setting('copilot.conversation_id', true), '')::uuid
    )
    WITH CHECK (
        account_id = NULLIF(current_setting('copilot.account_id', true), '')::uuid
        AND conversation_id = NULLIF(current_setting('copilot.conversation_id', true), '')::uuid
    );

ALTER TABLE copilot.memory_consolidation_event_receipts ENABLE ROW LEVEL SECURITY;
ALTER TABLE copilot.memory_consolidation_event_receipts FORCE ROW LEVEL SECURITY;
CREATE POLICY memory_consolidation_event_receipts_authorized_scope ON copilot.memory_consolidation_event_receipts
    FOR ALL
    USING (
        account_id = NULLIF(current_setting('copilot.account_id', true), '')::uuid
        AND conversation_id = NULLIF(current_setting('copilot.conversation_id', true), '')::uuid
    )
    WITH CHECK (
        account_id = NULLIF(current_setting('copilot.account_id', true), '')::uuid
        AND conversation_id = NULLIF(current_setting('copilot.conversation_id', true), '')::uuid
    );
