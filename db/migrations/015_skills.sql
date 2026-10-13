CREATE TABLE copilot.skills (
    skill_id text PRIMARY KEY,
    type text NOT NULL CHECK (type IN ('Persona', 'Workflow')),
    name text NOT NULL CHECK (length(btrim(name)) BETWEEN 1 AND 34),
    description text NOT NULL CHECK (length(description) <= 100),
    instruction text NOT NULL CHECK (length(btrim(instruction)) BETWEEN 8 AND 2000),
    stages text[] NOT NULL CHECK (cardinality(stages) BETWEEN 1 AND 6 AND stages <@ ARRAY['message','context','reply','review','consult','memory']::text[]),
    scope_type text NOT NULL CHECK (scope_type IN ('AllAuthorizedAccounts','Account','Conversation')),
    account_id uuid NULL REFERENCES copilot.chat_accounts(id),
    conversation_id uuid NULL,
    enabled boolean NOT NULL DEFAULT false,
    built_in boolean NOT NULL DEFAULT false,
    origin text NOT NULL CHECK (origin IN ('BuiltIn','Manual','AiAssisted')),
    revision bigint NOT NULL DEFAULT 1 CHECK (revision > 0),
    approval_status text NOT NULL CHECK (approval_status IN ('Draft','Approved')),
    approved_at timestamptz NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    deleted boolean NOT NULL DEFAULT false,
    FOREIGN KEY (conversation_id, account_id) REFERENCES copilot.conversations(id, account_id),
    CHECK ((scope_type = 'AllAuthorizedAccounts' AND account_id IS NULL AND conversation_id IS NULL)
        OR (scope_type = 'Account' AND account_id IS NOT NULL AND conversation_id IS NULL)
        OR (scope_type = 'Conversation' AND account_id IS NOT NULL AND conversation_id IS NOT NULL)),
    CHECK (type <> 'Persona' OR 'reply' = ANY(stages)),
    CHECK (NOT enabled OR (approval_status = 'Approved' AND approved_at IS NOT NULL))
);
CREATE INDEX ix_skills_scope ON copilot.skills(account_id, conversation_id) WHERE NOT deleted;
CREATE TABLE copilot.skill_versions (
    skill_id text NOT NULL REFERENCES copilot.skills(skill_id),
    revision bigint NOT NULL,
    snapshot jsonb NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY(skill_id, revision)
);
CREATE TABLE copilot.conversation_skill_selection (
    account_id uuid NOT NULL,
    conversation_id uuid NOT NULL,
    persona_skill_id text NOT NULL REFERENCES copilot.skills(skill_id),
    updated_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY(account_id, conversation_id),
    FOREIGN KEY(conversation_id, account_id) REFERENCES copilot.conversations(id, account_id)
);
INSERT INTO copilot.skills(skill_id,type,name,description,instruction,stages,scope_type,enabled,built_in,origin,approval_status,approved_at)
VALUES
('natural','Persona','自然','不刻意、不套路；尊重本人原有语言习惯','真实、自然、简洁；避免虚构承诺。',ARRAY['reply','consult'],'AllAuthorizedAccounts',true,true,'BuiltIn','Approved',now()),
('warm','Persona','温柔','关照对方感受，适当柔和表达','照顾对方感受，语气柔和，避免机械化套话。',ARRAY['reply','consult'],'AllAuthorizedAccounts',true,true,'BuiltIn','Approved',now()),
('playful','Persona','俏皮','轻松活泼，不过度调侃','用自然的轻松语气回应，避免冒犯和自说自话。',ARRAY['reply','consult'],'AllAuthorizedAccounts',true,true,'BuiltIn','Approved',now()),
('concise','Persona','简洁','直接清楚地表达，不绕圈','文字简短但有礼，优先解决沟通需求。',ARRAY['reply','consult'],'AllAuthorizedAccounts',true,true,'BuiltIn','Approved',now()),
('evidence','Workflow','聊天事实核查','核对约定和事实，不把猜测说成事实','仅在拥有对应会话的授权证据时确认事实；信息不足应表明不确定。',ARRAY['review'],'AllAuthorizedAccounts',true,true,'BuiltIn','Approved',now()),
('topic','Workflow','话题延展','在咨询前寻找当前话题自然延续点','优先延续已出现的话题，不编造共同经历。',ARRAY['consult'],'AllAuthorizedAccounts',true,true,'BuiltIn','Approved',now()),
('boundaries','Workflow','边界复核','核对是否替用户答应约定、作出过度承诺','检查回复是否越权承诺、猜测亲密关系或误认发言人。',ARRAY['review'],'AllAuthorizedAccounts',true,true,'BuiltIn','Approved',now());
