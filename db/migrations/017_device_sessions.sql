CREATE TABLE copilot.web_sessions (
 session_id uuid PRIMARY KEY DEFAULT gen_random_uuid(), token_hash text NOT NULL UNIQUE,
 device_name text NOT NULL, all_imported boolean NOT NULL DEFAULT false,
 authorized_account_ids uuid[] NOT NULL DEFAULT '{}', authorized_conversation_ids uuid[] NOT NULL DEFAULT '{}',
 created_at timestamptz NOT NULL DEFAULT now(), expires_at timestamptz NOT NULL,
 revoked_at timestamptz, is_local boolean NOT NULL DEFAULT false
);
CREATE TABLE copilot.device_pairing_grants (
 grant_hash text PRIMARY KEY, device_name text NOT NULL,
 authorized_account_ids uuid[] NOT NULL, authorized_conversation_ids uuid[] NOT NULL,
 created_at timestamptz NOT NULL DEFAULT now(), expires_at timestamptz NOT NULL, consumed_at timestamptz
);
