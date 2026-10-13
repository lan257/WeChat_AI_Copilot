// Domain snapshots are isolated by account and conversation. UI selection never changes monitoring.
export const store = {
  accounts: [], conversations: new Map(), selectedAccount: null, selectedConversation: null,
  sessions: new Map(), skills: [], preferences: {}, operations: null, authenticated: false,
  connectionError: '', filter: 'all', drawerTab: 'contacts', drawerOpen: false, setupStep: 0,
  diagnostics: new Map(), skillEditor: null, skillPreview: '', skillBusy: false, epoch: 0,
  // V8: one shared persona + analysis depth per conversation drives suggestions and consultation.
  focusOptionsExpanded: true
};
export const scopeKey = (accountId, conversationId) => `${accountId}:${conversationId}`;
export function scope() { return { accountId: store.selectedAccount, conversationId: store.selectedConversation }; }
export function stateFor(accountId, conversationId) {
  const key = scopeKey(accountId, conversationId);
  if (!store.sessions.has(key)) store.sessions.set(key, {
    accountId, conversationId, snapshot: null, messages: [], hasMore: true, perspective: null,
    memories: [], portrait: null, expression: null, persona: null, level: 'Quick',
    suggestions: null, suggestionTask: null, consultationTask: null, consultationHistory: [],
    threadId: null, historyJob: null, error: '', suggestionError: '', consultationError: '',
    stale: false, scrollTop: null, input: '', revisionDigest: null, loading: false
  });
  return store.sessions.get(key);
}
export function current() { return stateFor(store.selectedAccount, store.selectedConversation); }
export function account() { return store.accounts.find(a => a.accountId === store.selectedAccount); }
export function directory() { return store.conversations.get(store.selectedAccount) || []; }
export function contact() { return directory().find(c => c.conversationId === store.selectedConversation); }
export function changed() { document.dispatchEvent(new CustomEvent('copilot:state')); }
export function notify(message) { document.dispatchEvent(new CustomEvent('copilot:notify', { detail: message })); }
export function staleAll() { for (const s of store.sessions.values()) if (s.suggestions) s.stale = true; changed(); }
