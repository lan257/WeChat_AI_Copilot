ALTER TABLE copilot.sender_calibrations
    DROP CONSTRAINT IF EXISTS sender_calibrations_check1;

ALTER TABLE copilot.sender_calibrations
    ADD CONSTRAINT sender_calibrations_sender_ids_distinct_when_present
    CHECK (
        account_self_raw_sender_id IS NULL
        OR contact_raw_sender_id IS NULL
        OR account_self_raw_sender_id IS DISTINCT FROM contact_raw_sender_id
    );
