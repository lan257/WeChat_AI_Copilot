import { store, scope, current, stateFor, directory, account, contact, changed, notify, scopeKey } from './store.js';
import { request, errorText, establishSession } from './api.js';
import { $, escape, placeholder, time, isBusy, taskLabel, phaseLabel } from './dom.js';
import { svg } from './icons.js';

let loadEpoch=0;
let personaMarkup='',accountMarkup='';
const renderedMessages=new WeakMap();
const renderedConsultations=new WeakMap();
const taskPolls=new Map();
const levels={Quick:'近期聊天优先 · 有界上下文',Balanced:'近期聊天与授权记忆',Deep:'关联授权历史与记忆',Max:'有界检索与回复复核'};
const scopedMatch=(data,s)=>data && data.accountId===s.accountId && data.conversationId===s.conversationId;
const name=()=>contact()?.displayName || current().snapshot?.displayName || '请选择联系人';

export async function bootstrap() {
  try {
    const session=await establishSession();
    const metadata=await Promise.allSettled([request('/api/preferences',{scoped:false}),request('/api/operations/status',{scoped:false})]);
    if(metadata[0].status==='fulfilled')store.preferences=metadata[0].value;
    if(metadata[1].status==='fulfilled')store.operations=metadata[1].value;
    await refreshDirectory();
    let initial=null;
    // A paired device starts from its server-filtered directory; it never probes an unscoped default.
    if(session?.allImported){try { initial=await request('/api/conversations/current',{scoped:false}); } catch(error) { if(![403,404].includes(error.status))throw error; }}
    if (initial) { store.selectedAccount=initial.accountId; store.selectedConversation=initial.conversationId; }
    else { store.selectedAccount=store.accounts[0]?.accountId; store.selectedConversation=directory()[0]?.conversationId; }
    await loadConversation();
    store.connectionError='';
  } catch(error) { store.connectionError=errorText(error); notify(store.connectionError); }
  changed();
}
export async function refreshDirectory() {
  const data=await request('/api/accounts',{scoped:false});
  store.accounts=data?.accounts || [];
  await Promise.all(store.accounts.map(async a=>{
    const result=await request(`/api/accounts/${encodeURIComponent(a.accountId)}/conversations?limit=2000`,{scoped:false});
    const previous=store.conversations.get(a.accountId) || [];
    const records=(result?.conversations || []).map(row=>{
      const old=previous.find(c=>c.conversationId===row.conversationId);
      const newMessages=old&&old.messageSetRevision!==row.messageSetRevision;
      if(newMessages&&store.preferences.notifications)notify(`${row.displayName||'联系人'}有消息变化，点击会话查看。`);
      return {...row,unread:row.unreadCount||0};
    });
    store.conversations.set(a.accountId,records);
  }));
}
function mergeMessages(s,items) {
  const records=new Map(s.messages.map(m=>[m.messageId,m]));
  for(const item of items) records.set(item.messageId,item);
  s.messages=[...records.values()].sort((a,b)=>new Date(a.occurredAt||0)-new Date(b.occurredAt||0)||a.messageId.localeCompare(b.messageId));
}
function rememberThread(s,id) {
  s.threadId=id;
  try { localStorage.setItem('copilot:v6:thread:'+scopeKey(s.accountId,s.conversationId),id); } catch { /* Browser storage may be disabled. */ }
}
async function restoreThread(s) {
  try { s.threadId ||= localStorage.getItem('copilot:v6:thread:'+scopeKey(s.accountId,s.conversationId)); } catch { }
  if(!s.threadId)return;
  try {
    const history=await request(`/api/ai/consultations/${encodeURIComponent(s.threadId)}`,{target:s});
    if(scopedMatch(history?.thread,s)) s.consultationHistory=history.turns || [];
  } catch(error) {
    if(error.status===404){s.threadId=null;try{localStorage.removeItem('copilot:v6:thread:'+scopeKey(s.accountId,s.conversationId));}catch{}}
    else s.consultationError=errorText(error);
  }
}
export async function loadConversation({ incremental=false }={}) {
  if(!store.selectedAccount||!store.selectedConversation)return;
  const s=current(),epoch=++loadEpoch,selectionEpoch=s.selectionEpoch||0;
  if(!incremental)s.loading=true; s.error='';changed();
  try {
    const results=await Promise.allSettled([
      request('/api/conversations/current',{target:s}),
      request('/api/conversations/current/messages?limit=50',{target:s}),
      request('/api/conversations/current/sender-perspective',{target:s}),
      request('/api/conversations/current/memory?limit=100',{target:s}),
      request('/api/skills',{target:s}),
      request('/api/skills/selection',{target:s}),
      request('/api/ai/tasks',{target:s}),
      request('/api/history/jobs',{target:s})
    ]);
    const [snapshot,messages,perspective,memory,skills,selection,tasks,historyJobs]=results;
    if(snapshot.status==='fulfilled'&&scopedMatch(snapshot.value,s)){
      const next=snapshot.value;
      if(s.snapshot&&(next.messageSetRevision!==s.snapshot.messageSetRevision || next.calibrationVersion!==s.snapshot.calibrationVersion))s.stale=Boolean(s.suggestions);
      s.snapshot=next;
    } else if(snapshot.status==='rejected')s.error=errorText(snapshot.reason);
    if(messages.status==='fulfilled'&&scopedMatch(messages.value,s)){
      if(s.snapshot?.calibrationVersion!==messages.value.calibrationVersion){s.error='身份校准版本变化，请刷新消息。';}
      else {if(!incremental)s.messages=[];mergeMessages(s,messages.value.messages||[]);if(!incremental)s.hasMore=messages.value.hasMoreMessages;s.coverage=messages.value.historyCoverage;}
    }else if(messages.status==='rejected')s.error=errorText(messages.reason);
    if(perspective.status==='fulfilled'&&scopedMatch(perspective.value,s))s.perspective=perspective.value;
    if(memory.status==='fulfilled')s.memories=Array.isArray(memory.value)?memory.value:memory.value?.items||memory.value?.memoryItems||[];
    if(selection.status==='fulfilled'&&(s.selectionEpoch||0)===selectionEpoch){
      if(s.revisionDigest && s.revisionDigest!==selection.value?.revisionDigest)s.stale=Boolean(s.suggestions);
      s.revisionDigest=selection.value?.revisionDigest;s.persona=selection.value?.personaSkillId||s.persona;
    }
    if(epoch===loadEpoch&&skills.status==='fulfilled')store.skills=Array.isArray(skills.value)?skills.value:skills.value?.skills||[];
    if(tasks.status==='fulfilled'){
      const list=Array.isArray(tasks.value)?tasks.value:tasks.value?.tasks||[];
      const latest=list.find(t=>t.taskType==='ReplyCandidates');
      if(latest){s.suggestionTask=latest;if(latest.result)acceptTaskResult(s,latest);if(isBusy(latest))pollTask(s,latest.taskId,'suggestionTask');}
      const consultation=list.find(t=>t.taskType==='FreeConsultation');
      if(consultation){s.consultationTask=consultation;if(consultation.result?.threadId)rememberThread(s,consultation.result.threadId);if(isBusy(consultation))pollTask(s,consultation.taskId,'consultationTask');}
      const portrait=list.find(t=>t.taskType==='Portrait');
      if(portrait){s.portraitTask=portrait;if(isBusy(portrait))pollTask(s,portrait.taskId,'portraitTask');}
    }
    if(historyJobs.status==='fulfilled'){
      const latest=historyJobs.value?.items?.[0];
      if(latest&&(!s.historyJob||new Date(latest.updatedAtUtc)>=new Date(s.historyJob.updatedAtUtc)))s.historyJob=latest;
    }
    await restoreThread(s);
    if(epoch===loadEpoch&&s===current()&&s.snapshot&&!document.hidden&&!s.error){
      try{
        await request('/api/conversations/current/read',{method:'POST',target:s,body:{messageSetRevision:s.snapshot.messageSetRevision}});
        const row=directory().find(row=>row.conversationId===s.conversationId);if(row){row.unreadCount=0;row.unread=0;}
      }catch(error){if(error.status!==409)s.error=errorText(error);}
    }
  } finally {s.loading=false;changed();}
}
export async function selectConversation(id, accountId=store.selectedAccount) {
  const old=current();old.scrollTop=$('chatTimeline').scrollTop;old.input=$('aiInput').value;
  store.selectedAccount=accountId;store.selectedConversation=id;
  changed();await loadConversation();
  $('aiInput').value=current().input;
  renderMessages({ restore:true });
}
export async function refresh() {
  if(!store.authenticated){await bootstrap();return;}
  try {await refreshDirectory();await loadConversation({incremental:true});store.connectionError='';}catch(error){store.connectionError=errorText(error);}changed();
}
export async function older() {
  const s=current();if(!s.hasMore||s.loading)return;
  const box=$('chatTimeline'),oldHeight=box.scrollHeight,oldTop=box.scrollTop;
  const anchor=s.messages[0]?.messageId;if(!anchor)return;
  s.loading=true;
  try {
    const result=await request(`/api/conversations/current/messages?limit=50&anchorMessageId=${encodeURIComponent(anchor)}`,{target:s});
    if(!scopedMatch(result,s))throw new Error('消息范围不匹配，已停止展示。');
    mergeMessages(s,result.messages||[]);s.hasMore=result.hasMoreMessages;
    if(s===current()){renderMessages({preserve:true});box.scrollTop=box.scrollHeight-oldHeight+oldTop;}
  } catch(error){s.error=errorText(error);notify(s.error);}finally{s.loading=false;changed();}
}
export function renderDirectory() {
  const a=account();$('accountName').textContent=a?.displayName||'当前微信账号';
  $('accountSub').textContent=store.connectionError || (a?.availability==='ReaderOnline'?'已连接本机微信':a?'当前未连接 · 可查看历史':'等待本机应用连接');
  const accountOptions=store.accounts.map(a=>`<option value="${escape(a.accountId)}">${escape(a.displayName||'微信账号')} · ${a.availability==='ReaderOnline'?'已连接':'离线'}</option>`).join('');
  if(accountMarkup!==accountOptions){$('accountSelect').innerHTML=accountOptions;accountMarkup=accountOptions;}
  $('accountSelect').value=store.selectedAccount||'';
  const term=$('contactSearch').value.trim().toLowerCase();
  const list=directory().filter(c=>(!term||(c.displayName||'').toLowerCase().includes(term))&&(store.filter==='all'||c.isMonitored));
  $('conversationList').innerHTML=list.length?list.map(c=>`<button class="conv-row ${c.conversationId===store.selectedConversation?'selected':''}" data-action="switch" data-id="${escape(c.conversationId)}" aria-current="${c.conversationId===store.selectedConversation}"><span class="avatar">${escape((c.displayName||'微').slice(0,1))}<span class="live-mini ${c.captureState==='Listening'?'':'offline-mini'}"></span></span><span class="conv-content"><span class="conv-first"><strong>${escape(c.displayName||'未命名联系人')}</strong><span class="conv-time">${escape(time(c.lastMessageAtUtc))}</span></span><span class="conv-second"><span class="conv-preview">${escape(c.preview||'暂无可读取消息')}</span>${c.unreadCount?`<span class="unread">${c.unreadCount}</span>`:''}</span></span></button>`).join(''):placeholder(store.connectionError||'没有符合条件的会话。可切换账号或清除搜索条件。');
  const n=directory().filter(c=>c.captureState==='Listening').length;
  $('listenCount').textContent=`${n} 个会话正在监听`;
  document.querySelector('.demo-pill').innerHTML=`<span class="demo-dot"></span>${escape(store.connectionError?'本机连接不可用':store.authenticated?'本机安全会话 · 微信只读':'正在连接本机应用…')}`;
}
export function renderHeader() {
  const s=current(),c=contact();$('chatTitle').textContent=name();$('chatAvatar').textContent=name().slice(0,1);
  $('chatStatus').textContent=c?.captureState==='Listening'?'监听中':c?.isMonitored?'监听待就绪':'未监听';$('chatStatus').classList.toggle('off',c?.captureState!=='Listening');
  const confirmed=s.snapshot?.perspectiveState==='Confirmed';
  $('chatSubtitle').textContent=`${s.messages.length} 条已载入 · ${confirmed?'身份已校准':'发送方向待确认'} · 只读微信`;
  $('identityBanner').classList.toggle('hidden',confirmed||!s.snapshot);$('aiContact').textContent=name();
}
export function renderMessages({preserve=true,restore=false}={}) {
  const s=current(),box=$('chatTimeline'),previous=box.scrollTop,nearBottom=box.scrollHeight-box.clientHeight-previous<90;
  const first=s.messages[0];
  let html=`<div class="history-load"><button data-action="older" ${!s.hasMore||s.loading?'disabled':''}>${s.loading?'正在读取…':s.hasMore?'↑ 加载更早消息':'已到本次可读记录起点'}</button></div>`;
  if(first)html+=`<div class="date-line">${escape(new Date(first.occurredAt).toLocaleDateString('zh-CN'))}</div>`;
  html+=s.messages.map(m=>{
    const mine=m.direction==='Outgoing';
    const text=m.text || ({Image:'[图片]',Voice:'[语音]',Video:'[视频]',File:'[文件]',Text:'[文本内容不可用]'}[m.messageType] || `[${m.messageType||'非文本消息'}]`);
    const avatar=`<span class="avatar">${mine?'我':escape(name().slice(0,1))}</span>`;
    const bubble=`<div class="bubble-wrap"><div class="bubble">${escape(text)}</div><div class="bubble-time">${escape(time(m.occurredAt))}${m.direction==='Unknown'?' · 方向待确认':''}</div></div>`;
    return `<div class="bubble-row ${mine?'mine':''}" data-message-id="${escape(m.messageId)}">${mine?bubble+avatar:avatar+bubble}</div>`;
  }).join('');
  if(!s.messages.length)html+=placeholder(s.error||store.connectionError||'当前会话暂无可读取消息。');
  if(renderedMessages.get(box)!==html){box.innerHTML=html;renderedMessages.set(box,html);}
  if(restore&&s.scrollTop!==null)box.scrollTop=s.scrollTop;
  else if(!preserve||nearBottom)box.scrollTop=box.scrollHeight;
  else box.scrollTop=previous;
}
export function renderSuggestions() {
  const s=current(),busy=isBusy(s.suggestionTask),batch=s.suggestions;
  const available=store.skills.filter(p=>p.type==='Persona'&&p.enabled&&p.approvalStatus==='Approved'&&(p.scopeType==='AllAuthorizedAccounts'||(p.accountId===s.accountId&&(p.scopeType==='Account'||p.conversationId===s.conversationId))));
  const options=available.map(p=>`<option value="${escape(p.skillId)}">${escape(p.name)}</option>`).join('');
  if(personaMarkup!==options){$('personaSelect').innerHTML=options;personaMarkup=options;}
  if(s.persona)$('personaSelect').value=s.persona;
  $('thinkingSelect').value=s.level;
  $('generateLabel').textContent=busy?'生成中…':batch?'换一批':'生成三条';$('generateButton').disabled=busy||!s.snapshot||!available.length;
  $('cancelGen').classList.toggle('hidden',!busy);$('suggestProgress').classList.toggle('hidden',!busy);
  $('progressText').textContent=phaseLabel(s.suggestionTask);
  $('suggestStatus').textContent=busy?'生成中':s.suggestionError?'请求失败':s.stale?'建议待更新':batch?'已生成':'等待生成';$('suggestStatus').classList.toggle('stale',s.stale);
  const persona=available.find(p=>p.skillId===s.persona);
  $('suggestInfo').innerHTML=`${escape(levels[s.level])} · ${escape(persona?.name||'请选择')}人设${s.stale?' · <em>聊天、人设或 Skill 已变化，这批建议已过期</em>':' · 点击任一建议即可复制'}`;
  const suggestions=batch?.suggestions;
  $('suggestions').innerHTML=suggestions?.length===3?suggestions.map((q,i)=>`<button class="suggest-card" data-action="copy" data-idx="${i}"><span class="suggest-label">${i+1}</span><span class="suggest-text">${escape(q.text)}</span><span class="copy-mark">复制</span></button>`).join(''):placeholder(s.suggestionError||'先选择一种人设，然后生成三条可复制的回复建议。');
  renderFocus();
}
export function renderFocus() {
  const s=current(),persona=store.skills.find(p=>p.skillId===s.persona);
  $('focusName').textContent=`${name()} · ${s.stale?'建议需更新':'只读助手'}`;$('focusPersonaName').textContent=`人设 · ${persona?.name||'请选择'}`;
  $('focusChoices').innerHTML=s.suggestions?.suggestions?.length===3?s.suggestions.suggestions.map((q,i)=>`<button class="focus-choice" data-action="copy" data-idx="${i}"><span class="num">${i+1}</span><span class="words">${escape(q.text)}</span><span class="tick">复制</span></button>`).join(''):'<div class="focus-empty">暂无回复建议。点击下方“换一批”即可生成三条候选。</div>';
  const message=isBusy(s.suggestionTask)?phaseLabel(s.suggestionTask):s.suggestionError|| (s.stale?'新消息或 Skill 已变化，建议已过期':'');
  $('focusStatus').textContent=message;$('focusStatus').classList.toggle('hidden',!message);
  $('focusPersona').disabled=isBusy(s.suggestionTask)||!s.snapshot;
  document.querySelectorAll('.focus-controls [data-action="generate"]').forEach(b=>b.disabled=isBusy(s.suggestionTask)||!s.snapshot);
}
export function renderConsult() {
  const s=current(),box=$('aiScroll'),near=box.scrollHeight-box.clientHeight-box.scrollTop<80,previous=box.scrollTop;
  let html=s.consultationHistory.length?s.consultationHistory.map(turn=>`<div class="ai-turn user"><div>${escape(turn.userInput)}</div></div><div class="ai-turn assistant"><div><span class="ai-speaker">${svg('sparkles')} 知间 AI</span>${escape(turn.answer)}${turn.evidenceMessageIds?.length?`<button class="ai-citation" data-action="consult-evidence" data-id="${escape(turn.runId)}">已参考 ${turn.evidenceMessageIds.length} 条当前会话证据</button>`:''}</div></div>`).join(''):`<div class="ai-welcome"><strong>嗨，来聊聊你和 ${escape(name())} 的对话。</strong><p>想主动找话题、分析邀约，或者拿不准怎么回应，都可以在这里继续讨论。</p></div><div class="suggested-questions"><button class="question-btn" data-action="ask-example" data-q="怎样回复对方最近的消息比较自然？">如何自然地回应最近的消息？</button><button class="question-btn" data-action="ask-example" data-q="结合我们的聊天，帮我想几个可以继续聊的话题。">帮我找几个继续聊天的话题</button><button class="question-btn" data-action="ask-example" data-q="从目前的聊天信息看，有什么需要避免的误解吗？">这段聊天有没有容易误解的地方？</button></div>`;
  if(isBusy(s.consultationTask))html+=`<div class="ai-turn user"><div>${escape(s.pendingQuestion||'')}</div></div><div class="ai-turn assistant"><div><span class="ai-speaker"><span class="spinner"></span>${escape(phaseLabel(s.consultationTask))}</span><button class="textbtn" data-action="cancel-consultation">取消咨询</button></div></div>`;
  if(s.consultationError)html+=placeholder(s.consultationError);
  if(renderedConsultations.get(box)!==html){box.innerHTML=html;renderedConsultations.set(box,html);}
  box.scrollTop=near?box.scrollHeight:previous;$('aiSend').disabled=isBusy(s.consultationTask)||!s.snapshot;$('consultThinking').value=s.consultLevel;
}
function acceptTaskResult(s,task) {
  if(!task.result||!scopedMatch(task.result,s))return;
  if(task.taskType==='ReplyCandidates'){
    const items=task.result.suggestions;
    if(!Array.isArray(items)||items.length!==3||items.some(x=>!x.text?.trim())){s.suggestionError='建议格式无效，未显示未通过校验的结果。';return;}
    s.suggestions=task.result;s.stale=Boolean(task.isStale)||task.result.personaPresetId!==s.persona||task.result.executionProfileId!==s.level;s.suggestionError='';
  }else if(task.taskType==='FreeConsultation'){
    rememberThread(s,task.result.threadId);
    if(!s.consultationHistory.some(t=>t.runId===task.result.runId))s.consultationHistory.push(task.result);
    s.pendingQuestion='';s.consultationError='';
  }
}
async function pollTask(s,id,key) {
  if(taskPolls.has(id))return;
  taskPolls.set(id,true);
  try {
    while(true){
      await new Promise(resolve=>setTimeout(resolve,1000));
      const task=await request(`/api/ai/tasks/${encodeURIComponent(id)}`,{target:s});
      if(s[key]?.taskId!==id)break;
      s[key]=task;
      if(!isBusy(task)){
        if(task.status==='Completed'){if(key==='portraitTask')s.portrait=await request('/api/portraits/current',{target:s});else acceptTaskResult(s,task);}
        if(task.status==='Failed'){const message=`任务失败（${task.errorCode||'AGENT_FAILED'}），可重试。`;if(key==='suggestionTask')s.suggestionError=message;else if(key==='portraitTask')s.portraitError=message;else s.consultationError=message;}
        changed();break;
      }
      changed();
    }
  }catch(error){if(key==='suggestionTask')s.suggestionError=errorText(error);else if(key==='portraitTask')s.portraitError=errorText(error);else s.consultationError=errorText(error);changed();}
  finally{taskPolls.delete(id);}
}
export async function generate() {
  const s=current();if(!s.snapshot||isBusy(s.suggestionTask))return;
  const level=document.body.classList.contains('focus-mode')?'Quick':s.level;s.level=level;
  s.suggestionError='';s.suggestionTask={status:'Queued',phase:'正在提交任务…'};changed();
  try {
    const task=await request('/api/ai/tasks',{method:'POST',target:s,body:{taskType:'ReplyCandidates',personaPresetId:s.persona,executionProfileId:level,...(s.suggestions?.batchId?{previousBatchId:s.suggestions.batchId}:{})}});
    s.suggestionTask=task;changed();if(isBusy(task))pollTask(s,task.taskId,'suggestionTask');else acceptTaskResult(s,task);
  }catch(error){s.suggestionTask=null;s.suggestionError=errorText(error);changed();}
}
export async function ask(question) {
  const s=current(),input=(question??$('aiInput').value).trim();if(!input||!s.snapshot||isBusy(s.consultationTask))return;
  s.consultationError='';s.consultLevel=$('consultThinking').value;s.pendingQuestion=input;s.consultationTask={status:'Queued',phase:'正在提交咨询…'};$('aiInput').value='';s.input='';changed();
  try {
    if(!s.threadId){const thread=await request('/api/ai/consultations',{method:'POST',target:s});rememberThread(s,thread.threadId);}
    const task=await request('/api/ai/tasks',{method:'POST',target:s,body:{taskType:'FreeConsultation',executionProfileId:s.consultLevel,threadId:s.threadId,userInput:input,personaPresetId:s.persona}});
    s.consultationTask=task;changed();if(isBusy(task))pollTask(s,task.taskId,'consultationTask');else acceptTaskResult(s,task);
  }catch(error){s.consultationTask=null;s.consultationError=errorText(error);s.input=input;if(s===current())$('aiInput').value=input;changed();}
}
export async function cancelTask(kind) {
  const s=current(),key=kind==='consultation'?'consultationTask':'suggestionTask',task=s[key];if(!task?.taskId||!isBusy(task))return;
  try{s[key]=await request(`/api/ai/tasks/${encodeURIComponent(task.taskId)}`,{method:'DELETE',target:s});changed();}catch(error){notify(errorText(error));}
}
export async function copy(index) {
  const text=current().suggestions?.suggestions?.[index]?.text;if(!text)return;
  try{await navigator.clipboard.writeText(text);notify('已复制，粘贴后由你手动发送。');}catch{notify('复制失败，请允许浏览器访问剪贴板后重试。');}
}
export async function choosePersona(id) {
  const s=current(),epoch=(s.selectionEpoch||0)+1;s.selectionEpoch=epoch;
  const result=await request('/api/skills/selection',{method:'PUT',target:s,body:{personaSkillId:id}});
  if(s.selectionEpoch!==epoch)return;
  s.persona=result.personaSkillId||id;s.revisionDigest=result.revisionDigest;s.stale=Boolean(s.suggestions);changed();
}
