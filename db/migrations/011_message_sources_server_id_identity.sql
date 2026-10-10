DROP INDEX IF EXISTS copilot.ux_message_sources_identity;
CREATE UNIQUE INDEX ux_message_sources_identity
    ON copilot.message_sources(
        provider,
        account_id,
        source_kind,
        external_message_id,
        COALESCE(message_table, ''),
        COALESCE(shard_fingerprint, ''),
        COALESCE(server_id, ''),
        COALESCE(job_id, '')
    )
    WHERE external_message_id IS NOT NULL AND match_status IN ('Created', 'Matched');

DROP INDEX IF EXISTS copilot.ux_message_sources_review_identity;
CREATE UNIQUE INDEX ux_message_sources_review_identity
    ON copilot.message_sources(
        provider,
        account_id,
        source_kind,
        external_message_id,
        COALESCE(message_table, ''),
        COALESCE(shard_fingerprint, ''),
        COALESCE(server_id, ''),
        COALESCE(job_id, '')
    )
    WHERE external_message_id IS NOT NULL AND match_status = 'NeedsReview';
