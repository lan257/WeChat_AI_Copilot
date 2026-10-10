CREATE INDEX ix_chat_messages_conversation_source_time
    ON copilot.chat_messages(
        account_id,
        conversation_id,
        source_occurred_at DESC NULLS FIRST,
        ingested_at DESC,
        id DESC
    );
