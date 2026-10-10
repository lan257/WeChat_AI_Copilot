CREATE TABLE copilot.memory_items (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    account_id uuid NOT NULL REFERENCES copilot.chat_accounts(id) ON DELETE CASCADE,
    subject_type text NOT NULL CHECK (subject_type IN ('WechatAccount', 'Contact')),
    subject_id uuid NOT NULL,
    conversation_id uuid NULL,
    text_content text NOT NULL
        CHECK (length(btrim(text_content)) > 0 AND char_length(text_content) <= 4000),
    category text NULL
        CHECK (category IS NULL OR (length(btrim(category)) > 0 AND char_length(category) <= 100)),
    origin text NOT NULL CHECK (origin IN ('UserEntered', 'MessageExtracted')),
    status text NOT NULL DEFAULT 'Active'
        CHECK (status IN ('Active', 'Superseded', 'Deleted', 'NeedsReview')),
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    observed_at timestamptz NULL,
    calibration_version bigint NULL CHECK (calibration_version IS NULL OR calibration_version > 0),
    source_message_cursor text NULL,
    version bigint NOT NULL DEFAULT 1 CHECK (version > 0),
    UNIQUE (id, account_id, conversation_id),
    FOREIGN KEY (conversation_id, account_id)
        REFERENCES copilot.conversations(id, account_id) ON DELETE CASCADE,
    CHECK (subject_type <> 'WechatAccount' OR subject_id = account_id),
    CHECK (
        conversation_id IS NOT NULL
        OR (subject_type = 'WechatAccount' AND origin = 'UserEntered')
    ),
    CHECK (
        (origin = 'UserEntered' AND calibration_version IS NULL AND source_message_cursor IS NULL)
        OR
        (origin = 'MessageExtracted'
            AND conversation_id IS NOT NULL
            AND calibration_version IS NOT NULL
            AND source_message_cursor IS NOT NULL
            AND length(btrim(source_message_cursor)) > 0)
    )
);

CREATE INDEX ix_memory_items_active_conversation_updated
    ON copilot.memory_items(account_id, conversation_id, updated_at DESC, id)
    WHERE status = 'Active';

CREATE INDEX ix_memory_items_active_account_notes
    ON copilot.memory_items(account_id, updated_at DESC, id)
    WHERE status = 'Active'
        AND conversation_id IS NULL
        AND subject_type = 'WechatAccount'
        AND origin = 'UserEntered';

CREATE INDEX ix_memory_items_account_id
    ON copilot.memory_items(account_id);

CREATE INDEX ix_memory_items_conversation_account
    ON copilot.memory_items(conversation_id, account_id)
    WHERE conversation_id IS NOT NULL;

-- M2's initial message key is globally unique by id but does not expose the
-- full tuple needed for an evidence FK that also proves account/conversation scope.
CREATE UNIQUE INDEX ux_chat_messages_id_account_conversation
    ON copilot.chat_messages(id, account_id, conversation_id);

CREATE TABLE copilot.memory_evidence (
    memory_id uuid NOT NULL,
    account_id uuid NOT NULL,
    conversation_id uuid NOT NULL,
    message_id uuid NOT NULL,
    ordinal integer NOT NULL CHECK (ordinal >= 0),
    created_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (memory_id, message_id),
    UNIQUE (memory_id, ordinal),
    FOREIGN KEY (memory_id, account_id, conversation_id)
        REFERENCES copilot.memory_items(id, account_id, conversation_id) ON DELETE CASCADE,
    FOREIGN KEY (message_id, account_id, conversation_id)
        REFERENCES copilot.chat_messages(id, account_id, conversation_id) ON DELETE CASCADE,
    FOREIGN KEY (conversation_id, account_id)
        REFERENCES copilot.conversations(id, account_id) ON DELETE CASCADE
);

CREATE INDEX ix_memory_evidence_message_id
    ON copilot.memory_evidence(message_id);

CREATE INDEX ix_memory_evidence_memory_scope
    ON copilot.memory_evidence(memory_id, account_id, conversation_id);

CREATE INDEX ix_memory_evidence_conversation_account
    ON copilot.memory_evidence(conversation_id, account_id);

ALTER TABLE copilot.memory_items ENABLE ROW LEVEL SECURITY;
ALTER TABLE copilot.memory_items FORCE ROW LEVEL SECURITY;
CREATE POLICY memory_items_authorized_scope ON copilot.memory_items
    FOR ALL
    USING (
        account_id = NULLIF(current_setting('copilot.account_id', true), '')::uuid
        AND (
            conversation_id = NULLIF(current_setting('copilot.conversation_id', true), '')::uuid
            OR (
                conversation_id IS NULL
                AND subject_type = 'WechatAccount'
                AND origin = 'UserEntered'
            )
        )
    )
    WITH CHECK (
        account_id = NULLIF(current_setting('copilot.account_id', true), '')::uuid
        AND (
            conversation_id = NULLIF(current_setting('copilot.conversation_id', true), '')::uuid
            OR (
                conversation_id IS NULL
                AND subject_type = 'WechatAccount'
                AND origin = 'UserEntered'
            )
        )
    );

ALTER TABLE copilot.memory_evidence ENABLE ROW LEVEL SECURITY;
ALTER TABLE copilot.memory_evidence FORCE ROW LEVEL SECURITY;
CREATE POLICY memory_evidence_authorized_scope ON copilot.memory_evidence
    FOR ALL
    USING (
        account_id = NULLIF(current_setting('copilot.account_id', true), '')::uuid
        AND conversation_id = NULLIF(current_setting('copilot.conversation_id', true), '')::uuid
    )
    WITH CHECK (
        account_id = NULLIF(current_setting('copilot.account_id', true), '')::uuid
        AND conversation_id = NULLIF(current_setting('copilot.conversation_id', true), '')::uuid
    );
