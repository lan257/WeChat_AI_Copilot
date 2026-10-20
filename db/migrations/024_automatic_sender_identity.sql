ALTER TABLE copilot.chat_accounts
    ADD COLUMN account_wxid text NULL;

ALTER TABLE copilot.message_sources
    ADD COLUMN shard text NULL,
    ADD COLUMN sender_wxid text NULL;

ALTER TABLE copilot.sender_calibrations
    ADD COLUMN compatibility_only boolean NOT NULL DEFAULT true;

COMMENT ON COLUMN copilot.chat_accounts.account_wxid IS
    'Stable wxid reported by the selected local WeChat account directory.';
COMMENT ON COLUMN copilot.message_sources.shard IS
    'Original message database shard filename, retained alongside its account-scoped fingerprint.';
COMMENT ON COLUMN copilot.message_sources.sender_wxid IS
    'Sender username resolved through Name2Id in the message row shard.';
COMMENT ON COLUMN copilot.sender_calibrations.compatibility_only IS
    'Legacy short-ID calibration data retained for transition diagnostics; never used to project direction.';

CREATE INDEX ix_message_sources_shard_local_identity
    ON copilot.message_sources(
        provider,
        account_id,
        conversation_id,
        shard_fingerprint,
        message_table,
        local_id
    )
    WHERE local_id IS NOT NULL
      AND shard_fingerprint IS NOT NULL
      AND message_table IS NOT NULL
      AND match_status IN ('Created', 'Matched');

UPDATE copilot.chat_messages
SET direction = 'Unknown',
    direction_evidence = 'Unresolved'
WHERE direction_evidence IN ('DefaultSmallSenderId', 'UserConfirmed');
