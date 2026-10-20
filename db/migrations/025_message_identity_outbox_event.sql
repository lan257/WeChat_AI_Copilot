ALTER TABLE copilot.conversation_change_outbox
    DROP CONSTRAINT IF EXISTS conversation_change_outbox_event_type_check;

ALTER TABLE copilot.conversation_change_outbox
    ADD CONSTRAINT ck_conversation_change_outbox_event_type
    CHECK (event_type IN (
        'message.created',
        'message.identity-resolved',
        'conversation.perspective-changed',
        'conversation.perspective-confirmed'
    ));
