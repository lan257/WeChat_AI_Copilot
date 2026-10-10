CREATE INDEX IF NOT EXISTS ix_chat_messages_conversation_effective_time
    ON copilot.chat_messages(
        account_id,
        conversation_id,
        COALESCE(source_occurred_at, ingested_at) DESC,
        ingested_at DESC,
        id DESC
    );
