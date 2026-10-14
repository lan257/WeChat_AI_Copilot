import { store, scope } from './store.js';
let csrfToken = null;
const messages = {
  UNAUTHORIZED:'本机会话尚未建立，请从一键启动入口打开网页。', SESSION_INVALID:'登录会话已过期，请重新打开工作区。',
  DATABASE_UNAVAILABLE:'本机数据库暂时不可用，请在连接设置中检测并重试。',
  AGENT_RESULT_INVALID:'AI 返回结果未通过校验。', CONTEXT_MESSAGE_TEXT_REQUIRED:'文本消息缺少内容。',
  NETWORK_UNAVAILABLE:'无法连接本机应用，请确认本机应用已启动，或在连接设置中检测并重试。',
  SKILL_NOT_APPROVED:'这份 AI 草稿尚未确认启用。', CALIBRATION_VERSION_CONFLICT:'身份版本已变化，请重新核对。'
  ,SKILL_DEVICE_SCOPE_FORBIDDEN:'手机设备只能编辑已授权的当前联系人 Skill；账号通用规则请在电脑上修改。'
  ,CODEX_CLI_NETWORK_FAILED:'AI 连不上模型服务：本机代理或网络出口没有就绪。启动窗口里会提示代理是否连得上，配好之后再试一次即可。'
  ,CODEX_CLI_AUTH_REQUIRED:'Codex CLI 还没有登录模型服务，请先在这台电脑上完成 Codex 登录。'
  ,RUNTIME_TIMEOUT:'AI 超过本机设定的单次时限，通常是模型出口太慢或不通；稍后重试，或改用更快的分析深度。'
};
export class ApiError extends Error {
  constructor(code, detail, status) { super(messages[code] || detail || `请求失败（${code}）`); this.code=code; this.status=status; }
}
export async function request(path, { method='GET', body, scoped=true, target, signal } = {}) {
  const url = new URL(path, location.origin);
  const selected = target || scope();
  if (scoped && selected.accountId) url.searchParams.set('accountId', selected.accountId);
  if (scoped && selected.conversationId) url.searchParams.set('conversationId', selected.conversationId);
  const headers = { Accept:'application/json' };
  if (body !== undefined) headers['Content-Type']='application/json';
  if (method !== 'GET') {
    if (csrfToken) headers['X-Copilot-CSRF']=csrfToken;
    headers['Idempotency-Key']=crypto.randomUUID();
  }
  let response;
  try { response=await fetch(url, {method, headers, credentials:'same-origin', body:body === undefined ? undefined : JSON.stringify(body), signal}); }
  catch (error) { if (error.name==='AbortError') throw error; throw new ApiError('NETWORK_UNAVAILABLE'); }
  const data = await response.json().catch(() => null);
  if (!response.ok) {
    const error=data?.error || data || {};
    if(response.status===401){store.authenticated=false;store.connectionError=messages.UNAUTHORIZED;}
    const violations=error.violations || data?.violations || [];
    throw new ApiError(error.code || (response.status===401?'UNAUTHORIZED':`HTTP_${response.status}`), [error.message, ...violations.map(v=>v.code)].filter(Boolean).join(' · '), response.status);
  }
  return data;
}
export async function establishSession() {
  const fragment=new URLSearchParams(location.hash.slice(1));
  const grant=fragment.get('bootstrap');
  const pairing=fragment.get('pair');
  if (grant||pairing) history.replaceState(null,'',location.pathname+location.search);
  const result=grant||pairing
    ? await request(pairing?'/api/session/pair':'/api/session/bootstrap',{method:'POST',body:{grant:grant||pairing},scoped:false})
    : await request('/api/session',{scoped:false});
  csrfToken=result?.csrfToken;
  store.authenticated=result?.authenticated===true;
  if (!store.authenticated) throw new ApiError('UNAUTHORIZED');
  return result;
}
export function errorText(error) { return `${error.message}${error.code ? `（${error.code}）` : ''}`; }
