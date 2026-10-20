import {store,current,account,contact,directory,changed,notify,staleAll} from './store.js';
import {request,errorText} from './api.js';
import {$,escape,intro,card,button,placeholder,time,isBusy,statusLabel} from './dom.js';
import {renderSkills,captureEditor} from './skills.js';
import {loadConversation,refreshDirectory} from './chat.js';
import {hydrateIcons} from './icons.js';

const name=()=>contact()?.displayName||'当前联系人';
const tabs=['overview','listening','conversation','portrait','self','memories','history','agent','setup','preferences','health'];
const steps=[
  {title:'本地服务与存储',summary:'应用检查和管理 PostgreSQL、迁移与必要运行时',detail:'本机应用负责数据目录、数据库准备与运行状态。缺组件时显示原因和恢复建议。',action:'本地自检',guide:'本机应用管理私有 PostgreSQL。高级诊断只展示运行元数据，不含凭据或聊天正文。'},
  {title:'微信连接与读取器',summary:'由用户登录微信；应用选择当前账号和授权监听的会话',detail:'只读 Reader 检测微信会话和自己的运行状态。当前活动账号能监听多个已授权联系人；其他账号可保留资料但标记离线。',action:'微信连接检测',guide:'若微信未登录，打开官方微信客户端完成登录；若 Reader 未就绪，查看原因与恢复建议。'},
  {title:'Codex / AI 连接',summary:'应用检测 CLI/模型服务，并引导用户完成服务商认证',detail:'运行不含私聊内容的连接测试；模型、Quick 路由和思考档位可在 AI 设置中调整。',action:'AI 连接测试',guide:'认证凭据由本机安全存储或提供方管理，不能写入前端源码。'},
  {title:'手机与其他设备',summary:'可选：电脑批准设备配对，手机通过安全 HTTPS 访问',detail:'手机使用独立可撤销会话。访问未启用时不会显示电脑本机地址作为手机链接。',action:'设备配对检查',guide:'需安全 HTTPS、短期配对和设备撤销。连接未就绪时可跳过，不阻塞电脑使用。'}
];
let drawerRequest=0;
const fields=new Map();
const key=()=>`${store.selectedAccount}:${store.selectedConversation}:${store.drawerTab}`;
function preserveFields() {
  captureEditor();
  const values={};$('drawerContent').querySelectorAll('input[id],textarea[id],select[id]').forEach(n=>{values[n.id]=n.value;});fields.set(key(),values);
}
export function openDrawer(tab='overview') {
  if(store.drawerOpen)preserveFields();store.drawerTab=tabs.includes(tab)?tab:'overview';store.drawerOpen=true;renderDrawer();void loadDrawer();
}
export function closeDrawer() {preserveFields();store.drawerOpen=false;renderDrawer();}
export async function loadDrawer() {
  const s=current(),tab=store.drawerTab,version=++drawerRequest;
  try {
    if(tab==='portrait'){s.portrait=await request('/api/portraits/current',{target:s});}
    if(tab==='self'){s.expression=await request('/api/expression/current',{target:s});}
    if(['overview','health','setup','preferences','agent'].includes(tab)){
      const results=await Promise.allSettled([request('/api/operations/status',{scoped:false}),request('/api/preferences',{scoped:false})]);
      if(results[0].status==='fulfilled')store.operations=results[0].value;
      if(results[1].status==='fulfilled')store.preferences=results[1].value;
    }
    if(tab==='setup'&&store.setupStep===3)await loadDevices();
    if(version===drawerRequest&&store.drawerOpen&&s===current())renderDrawer();
  }catch(error){if(version===drawerRequest){notify(errorText(error));if(tab==='portrait')s.portraitError=errorText(error);if(tab==='self')s.expressionError=errorText(error);renderDrawer();}}
}
function overview() {
  const s=current(),rows=directory(),n=rows.filter(c=>c.captureState==='Listening').length;
  return intro('一切都围绕当前联系人','这是产品功能入口。可以直接打开应用、选择联系人、查看聊天和 AI 建议；复杂设置只在需要时出现。')+
  `<div class="stat-grid"><div class="stat-item"><strong>${rows.length}</strong><span>已发现会话</span></div><div class="stat-item"><strong>${n}</strong><span>持续监听</span></div><div class="stat-item"><strong>${s.memories.length}</strong><span>当前人物记忆</span></div></div><div class="group-heading">日常使用</div>`+
  card('同时监听多个联系人','切换当前查看的人，不影响其余监听目标。进入“监听管理”可以启停。',button('tab','去管理','data-tab="listening"'))+
  card('新消息让旧建议过期','刷新真实消息，查看建议区与极简模式的版本状态。',button('refresh','刷新消息'))+
  card('快速 / 平衡 / 深度 / Max','回复人设与分析强度相互独立。任务运行不阻塞聊天阅读。',button('close-drawer','回工作区'))+
  '<div class="notice warn">微信始终只读。建议只复制到剪贴板，由你自行粘贴与发送。</div>';
}
function listening() {
  const rows=directory(),n=rows.filter(c=>c.isMonitored).length;
  return intro('后台监听管理','“当前查看谁”与“正在监听谁”是独立状态。会话切换不会重启监听器；新增监听目标后，其他联系人仍然持续采集。')+
  `<div class="notice">当前账号：${escape(account()?.displayName||'当前微信账号')}　·　已选择 ${n}/${rows.length} 个会话（最多 20 个）</div><div class="group-heading">我的会话 <span style="color:#8a9a94;font-size:11px">开关以服务端确认和 Reader 状态为准</span></div>`+
  card('发现本机联系人目录','只读取当前已登录微信账号的目录，不会自动授权全部联系人。',button('discover','读取目录'))+
  rows.map(c=>`<div class="setting-card"><span class="avatar">${escape((c.displayName||'微').slice(0,1))}</span><div class="setting-main"><strong>${escape(c.displayName||'未命名联系人')}</strong><p>${escape(({Listening:'消息持续采集中 · 可在任意会话查看',Starting:'目标已提交 · 正在等待 Reader 生效',Offline:'目标已保存 · Reader 当前离线',NotMonitored:'当前未监听 · 历史记录仍可查看',Error:'监听异常 · 在运行状态查看恢复建议'}[c.captureState]||'等待真实 Reader 状态'))}</p></div><button class="toggle ${c.isMonitored?'on':''}" data-action="toggle-listen" data-id="${escape(c.conversationId)}" aria-label="${c.isMonitored?'停止':'开始'}监听 ${escape(c.displayName)}" aria-pressed="${c.isMonitored}"></button></div>`).join('');
}
function conversation() {
  const s=current(),c=contact();
  return intro(`${name()} · 会话详情`,'相关操作集中在当前联系人内，不占据聊天窗口的主要位置。')+
  card(name(),`微信 · ${account()?.displayName||'当前微信账号'} · ${c?.isMonitored?'已选择监听':'未监听'}`,`<button class="toggle ${c?.isMonitored?'on':''}" data-action="toggle-listen" data-id="${escape(c?.conversationId||'')}" aria-label="切换监听"></button>`)+
  '<div class="group-heading">会话操作</div>'+
  card('历史聊天记录补录','按时间段读取本地历史、去重合并，展示扫描范围与补录结果。',button('tab','开始','data-tab="history"'))+
  card('联系人画像概览','共同话题、近期安排与沟通偏好；事实与推测分别标注。',button('tab','查看画像','data-tab="portrait"'))+
  card('记忆条目与证据',`${s.memories.length} 条可维护记录，支持按证据回溯与纠错。`,button('tab','查看证据','data-tab="memories"'));
}
function portrait() {
  const s=current(),p=s.portrait,verified=s.snapshot?.perspectiveState==='Confirmed';
  const claimText=item=>typeof item==='string'?item:item.text||item.summary||'';
  const section=(title,items,empty)=>`<div class="portrait-section"><h4>${title}</h4>${items?.length?items.map(item=>`<div class="portrait-item"><span class="portrait-bullet"></span><span>${typeof item==='object'?`<span class="portrait-mark ${item.type==='Inference'?'is-warn':''}">${item.type==='Inference'?'AI 推测':'聊天事实'}</span> `:''}${escape(claimText(item))}</span></div>`).join(''):`<p class="portrait-empty">${empty}</p>`}</div>`;
  const sections=p?.sections||{};
  return intro(`${name()} · 人物画像`,'一眼了解当前联系人。画像是一份有证据边界的动态摘要，不是记忆原始列表；聊天事实、用户备注与 AI 推测必须分开。')+
  `<div class="portrait-hero"><div class="avatar">${escape(name().slice(0,1))}</div><div><strong>${escape(name())}</strong><p>当前账号 · ${escape(account()?.displayName||'微信账号')}</p><span class="portrait-mark ${verified?'':'is-warn'}">${verified?'账号身份已识别':'账号身份未识别 · 暂停方向依赖功能'}</span></div></div>
  <div class="portrait-summary"><div class="portrait-kicker">当前关系背景${p?.status==='Stale'?' · 已过期，需更新':''}</div><p>${escape(s.portraitError||(Array.isArray(sections.overview)?sections.overview.map(item=>`${item.type==='Inference'?'[AI 推测]':'[聊天事实]'} ${claimText(item)}`).join('\n'):sections.overview)||'暂无可靠资料。可补充核实后的备注，或根据授权聊天更新画像。')}</p><div class="portrait-source">来源范围：${p?.evidence?.length||0} 条授权证据 · 方向版本 ${p?.calibrationVersion||s.snapshot?.calibrationVersion||0} · 画像版本 ${p?.revision||0}</div></div>
  <div class="portrait-grid">${section('共同话题',sections.topics,'目前尚无可确定的共同话题')}${section('近期计划',sections.plans,'暂无已记录的近期计划')}${section('表达与互动偏好',sections.preferences,'没有可靠偏好证据，不进行性格推断')}</div>
  <div class="notice warn"><strong>推断边界</strong><div style="margin-top:5px">${escape((Array.isArray(sections.boundaries)?sections.boundaries.map(claimText).join('；'):sections.boundaries)||'证据不足，不应推断对方性格或真实意图。')} 未确认的内容不能进入强事实记忆。</div></div>
  <div class="group-heading">持续维护与纠错</div>`+
  card('根据新消息更新画像','异步检查授权聊天、对比旧画像，摘要与证据分别保存。',button('portrait-refresh',isBusy(s.portraitTask)?'正在更新…':'更新画像',!verified||isBusy(s.portraitTask)?'disabled':''))+
  card('审阅画像证据','查看独立记忆条目、用户备注及聊天证据。',button('tab','查看证据','data-tab="memories"'))+
  `<div class="group-heading">纠正或补充信息</div><textarea class="form-field" id="portraitCorrection" rows="3" placeholder="例如：周六还没有确认；请勿把邀约当作已约好。"></textarea><button class="primary" data-action="portrait-correct" style="margin-top:9px">保存为人工备注</button><div class="notice" style="margin-top:16px">画像摘要与消息证据分开保存。人工备注不改写微信历史，不建立未经证实的敏感心理标签。</div>`;
}
function expression() {
  const p=current().expression||{};
  return intro('我的表达与沟通画像','这里属于当前微信账号的“我”，与联系人的画像不同。为 AI 提供你的表达习惯和边界，而不是替你定义人格。')+
  `<div class="portrait-hero"><div class="account-logo">我</div><div><strong>${escape(account()?.displayName||'当前微信账号')}</strong><p>${account()?.availability==='ReaderOnline'?'当前已连接':'历史账号 · 当前未连接'}</p></div></div>${current().expressionError?placeholder(current().expressionError):''}
  <div class="group-heading">默认表达风格</div><textarea id="selfVoice" class="form-field" rows="3">${escape(p.voice)}</textarea>
  <div class="group-heading">重要沟通边界</div><textarea id="selfBoundary" class="form-field" rows="3">${escape(p.boundary)}</textarea>
  <div class="group-heading">其他个性化说明</div><textarea id="selfNotes" class="form-field" rows="3">${escape(p.notes)}</textarea>
  <div class="notice" style="margin:14px 0">“我的表达”决定默认语气与边界；联系人画像决定对方背景；单次人设和思考强度仍可单独覆盖。微信不会自动发送任何内容。</div><button class="primary" data-action="save-self" style="width:100%">保存表达设置</button>`;
}
function memories() {
  const s=current();
  return intro(`${name()} · 记忆证据`,'这里管理支持画像的原始线索、人工备注和可追溯证据。点击“人物画像”查看上层摘要。')+
  `<div class="notice">记忆只属于当前账号与「${escape(name())}」。换联系人后不会带入其他人的背景。</div><div class="group-heading">当前记忆 · ${s.memories.length} 条</div><div class="memory-cards">`+
  (s.memories.length?s.memories.map(m=>`<div class="memory-card"><div class="meta"><span>${m.origin==='UserEntered'?'用户备注':'聊天证据提取'} · ${escape(m.status)} · v${m.version}</span>${m.origin==='UserEntered'?`<button data-action="delete-memory" data-id="${escape(m.memoryId)}">删除</button>`:''}</div><p>${escape(m.text)}</p>${m.sourceMessageIds?.length?`<button class="evidence" style="width:100%;text-align:left" data-action="memory-evidence" data-id="${escape(m.memoryId)}">查看 ${m.sourceMessageIds.length} 条授权聊天证据 · 方向版本 v${m.calibrationVersion||0}</button>`:`<div class="evidence">用户手动录入 · 不作为聊天事实 · ${escape(time(m.updatedAt))}</div>`}</div>`).join(''):placeholder('暂无人物记忆。解析出真实发送者后会按用户授权沉淀聊天背景。'))+
  `</div>${(s.selectedEvidence||s.portrait?.evidence)?.length?`<div class="group-heading">当前引用的聊天证据</div>${(s.selectedEvidence||s.portrait.evidence).map(e=>`<div class="memory-card"><p>${escape(e.excerpt||`[${e.messageType||'非文本消息'}]`)}</p><div class="evidence">授权聊天 · ${escape(time(e.occurredAt||e.occurredAtUtc))}</div></div>`).join('')}:''}<div class="group-heading">补充一条个人备注</div><textarea class="form-field" id="newMemory" rows="3" placeholder="例如：对方不喜欢临时改变约好的时间……" style="resize:vertical"></textarea><div style="display:flex;justify-content:flex-end;margin-top:9px"><button class="primary" data-action="add-memory">添加备注</button></div>`;
}
function history() {
  const j=current().historyJob,busy=j&&['Pending','Queued','Running','CancelRequested'].includes(j.status);
  return intro(`${name()} · 历史消息补录`,'主动补录当前联系人在本机已有的旧消息。实时监听和历史补录共享消息库，按来源身份幂等合并。')+
  `<div class="notice warn">当前账号 · 当前一对一会话 · 文本记录。本次完成只代表所选范围扫描完成，历史覆盖状态仍为 Unknown。</div><div class="group-heading">补录范围</div>`+
  card('最近 90 天','最大 5,000 条 · 按时间分批 · 保留来源冲突证据')+
  `<div style="display:flex;gap:9px">${button('backfill',busy?'正在同步…':'同步历史消息',busy?'disabled':'','primary')}${button('cancel-backfill','取消任务',busy?'':'disabled')}</div><div style="margin-top:21px" id="backfillStats">`+
  (j?`<div class="group-heading">本次任务 · ${escape(statusLabel(j.status))}</div><div class="stat-grid"><div class="stat-item"><strong>${j.read||0}</strong><span>已扫描</span></div><div class="stat-item"><strong>${j.matched||0}</strong><span>已匹配</span></div><div class="stat-item"><strong>${j.created||0}</strong><span>新补录</span></div></div><p class="intro" style="margin-top:13px">${j.needsReview||0} 条需核对 · ${j.failed||0} 条失败 · 历史覆盖：${escape(statusLabel(j.coverage||'Unknown'))}${j.errorCode?` · ${escape(j.errorCode)}`:''}</p>`:placeholder('尚未开始本次历史补录。点击“同步历史消息”查看真实任务状态和对账结果。'))+'</div>';
}
function setup() {
  const n=store.setupStep,item=steps[n],check=store.diagnostics.get(n);
  return intro('连接中心 · 首次配置向导','按步骤检测本机应用、微信、AI 与手机安全访问。日常使用不应重复经过这些步骤。')+
  `<div class="setup-progress">${steps.map((v,i)=>`<button class="setup-step ${i===n?'active':''} ${store.diagnostics.get(i)?.state==='Ready'?'done':''}" data-action="setup-step" data-index="${i}"><span>${store.diagnostics.get(i)?.state==='Ready'?'✓':i+1}</span><small>${escape(v.title)}</small></button>`).join('')}</div><div class="setup-main-card"><div class="setup-title">第 ${n+1} / ${steps.length} 步 <span class="portrait-mark">真实连接检测</span></div><h3>${escape(item.title)}</h3><p class="setup-summary">${escape(item.summary)}</p><p>${escape(item.detail)}</p><div class="setup-actions">${button('setup-check',item.action,'','primary')}${n>0?button('setup-prev','上一步'):''}${n<3?button('setup-next','下一步'):button('close-drawer','返回工作区')}</div><details class="setup-guide"><summary>需要自己完成哪些操作？ / 高级说明</summary><p>${escape(item.guide)}</p></details></div>`+
  (check?`<div class="group-heading">检测结果 · ${escape(statusLabel(check.state))}</div>${(check.checks||[]).map(c=>card(c.name,`${c.message||''}${c.recovery?' · '+c.recovery:''}`,`<span class="conn-pill ${c.state==='Ready'?'ok':'idle'}">${escape(statusLabel(c.state))}</span>`)).join('')}`:'')+
  (n===3?deviceMarkup():'')+
  `<div class="group-heading">所有连接的状态与归属</div>${steps.map((v,i)=>card(v.title,v.summary,`<span class="conn-pill ${store.diagnostics.get(i)?.state==='Ready'?'ok':'idle'}">${escape(store.diagnostics.get(i)?.state||'待检测')}</span>`)).join('')}<div class="notice warn"><strong>安全会话：</strong>本机网页由启动器建立安全会话，手机使用独立可撤销设备会话。无需每次复制粘贴 Web Token。</div><details class="setup-guide" style="margin-top:12px"><summary>专家选项：外部 PostgreSQL、模型地址与诊断信息</summary><p>敏感配置由本机应用受控保存；本页面不显示数据库管理员密码、Reader Token 或原始聊天日志。</p></details>`;
}
function health() {
  const o=store.operations||{};
  const status=statusLabel;
  return intro('运行状态','查看哪些能力正在工作、失败原因与恢复方法。这里只显示运行元数据。')+
  '<div class="group-heading">当前应用状态</div>'+
  [['本机微信 Reader',o.readerState,'只读采集，监听目标独立于当前查看会话'],['PostgreSQL 消息库',o.databaseState,'消息和来源去重归档'],['AI 任务执行',o.configuredRuntime,'Quick / Balanced / Deep / Max，实际速度由模型路由决定'],['长期记忆沉淀',o.memoryState||'等待账号身份识别','基于授权证据与可追溯修订'],['手机安全访问',o.mobileAccess?.state||'尚未启用','需要 HTTPS 与安全配对']].map(([title,state,description])=>card(title,description,`<span style="font-size:11px;white-space:nowrap">${escape(status(state))}</span>`)).join('')+
  card('诊断与恢复','前往连接中心检测失败的步骤并查看恢复建议。',button('tab','连接中心','data-tab="setup"'))+
  (current().suggestionTask?.timings?`<div class="notice">最近建议耗时：排队 ${current().suggestionTask.timings.queueMilliseconds??'未知'} ms · 上下文 ${current().suggestionTask.timings.contextMilliseconds??'未知'} ms · CLI/模型 ${current().suggestionTask.timings.cliMilliseconds??'未知'} ms · 复核 ${current().suggestionTask.timings.reviewMilliseconds??'未知'} ms · 总计 ${current().suggestionTask.timings.totalMilliseconds??'未知'} ms</div>`:'');
}
function preferences() {
  const p=store.preferences;
  return intro('应用偏好与体验设置','保存日常使用偏好，连接参数和内部凭据由本机应用管理。')+
  '<div class="group-heading">日常使用</div>'+
  card('新消息到达时提前准备建议','后台准备当前已授权会话的 Quick 建议；以服务端配置生效为准。',`<button class="toggle ${p.autoPrep?'on':''}" data-action="toggle-pref" data-key="autoPrep" aria-label="提前准备建议" aria-pressed="${Boolean(p.autoPrep)}"></button>`)+
  card('桌面新消息提示','只提示新消息到达，不公开聊天正文。',`<button class="toggle ${p.notifications?'on':''}" data-action="toggle-pref" data-key="notifications" aria-label="桌面新消息提示" aria-pressed="${Boolean(p.notifications)}"></button>`)+
  card('在工作区优先展示三条建议','让建议始终处于聊天记录底部的可达区域。','<button class="toggle on" aria-label="始终开启" disabled></button>')+
  '<div class="group-heading">系统与连接</div>'+card('本机数据与账号绑定','聊天历史、联系人画像和 AI 会话分账号隔离。','<span style="color:#278c70;font-weight:700;font-size:12px">服务端隔离</span>')+
  card('手机局域网安全配对','HTTPS、安全配对及有限访问权限，不要求手动复制 Bearer Token。',button('tab','查看','data-tab="setup"'))+
  '<div class="notice">始终只读微信，建议由你复制后手动粘贴与发送。任何模式均不提供微信自动代发。</div>';
}
export function renderDrawer() {
  $('drawer').classList.toggle('hidden',!store.drawerOpen);$('scrim').classList.toggle('hidden',!store.drawerOpen);
  if(!store.drawerOpen)return;
  const active=document.activeElement;
  // Preserve in-progress field editing while background message/task events arrive.
  if($('drawerContent').contains(active)&&['INPUT','TEXTAREA','SELECT'].includes(active.tagName))return;
  const mapping={overview,listening,conversation,portrait,self:expression,memories,history,agent:renderSkills,setup,preferences,health};
  $('drawerContent').innerHTML=(mapping[store.drawerTab]||overview)();hydrateIcons($('drawerContent'));
  if(store.skillBusy)$('drawerContent').querySelectorAll('.skill-editor input,.skill-editor textarea,.skill-editor select').forEach(field=>field.disabled=true);
  document.querySelectorAll('.drawer-tab').forEach(b=>b.classList.toggle('active',b.dataset.tab===store.drawerTab));
  const saved=fields.get(key());if(saved)for(const [id,value]of Object.entries(saved)){const field=$(id);if(field&&field.closest('#drawerContent'))field.value=value;}
}
async function toggleListen(id) {
  const rows=directory(),row=rows.find(c=>c.conversationId===id);if(!row)return;
  const ids=rows.filter(c=>c.isMonitored&&c!==row).map(c=>c.conversationId);if(!row.isMonitored)ids.push(id);
  await request(`/api/accounts/${encodeURIComponent(store.selectedAccount)}/reader-targets`,{method:'PUT',scoped:false,body:{expectedVersion:row.targetVersion||0,conversationIds:ids}});
  await refreshDirectory();await loadConversation({incremental:true});notify('监听目标已保存；页面以 Reader 实际状态显示是否生效。');
}
async function pollHistory(s) {
  while(s.historyJob&&['Pending','Queued','Running','CancelRequested'].includes(s.historyJob.status)){
    await new Promise(r=>setTimeout(r,1200));s.historyJob=await request(`/api/history/jobs/${encodeURIComponent(s.historyJob.jobId)}`,{target:s});changed();
  }
  if(s===current())await loadConversation({incremental:true});
}
export async function drawerAction(el) {
  const action=el.dataset.action,s=current();
  if(action==='setup-step'||action==='setup-next'||action==='setup-prev'){
    preserveFields();store.setupStep=action==='setup-step'?Number(el.dataset.index):Math.max(0,Math.min(3,store.setupStep+(action==='setup-next'?1:-1)));renderDrawer();if(store.setupStep===3)await loadDevices();return;
  }
  try {
    if(action==='toggle-listen')await toggleListen(el.dataset.id);
    if(action==='add-memory'||action==='portrait-correct'){
      const text=$(action==='add-memory'?'newMemory':'portraitCorrection')?.value.trim();if(!text)throw new Error('请先填写要保存的人工备注。');
      await request('/api/conversations/current/memory',{method:'POST',target:s,body:{subjectType:'Contact',text,category:action==='portrait-correct'?'Correction':'Note',sourceMessageIds:[]}});
      fields.delete(key());await loadConversation({incremental:true});notify('人工备注已保存，不改写聊天事实。');
    }
    if(action==='delete-memory'){
      const m=s.memories.find(m=>m.memoryId===el.dataset.id);if(m?.origin!=='UserEntered')return;
      if(!window.confirm('删除这条人工备注？微信聊天记录不会被删除。'))return;
      await request(`/api/conversations/current/memory/${encodeURIComponent(m.memoryId)}?expectedVersion=${m.version}`,{method:'DELETE',target:s});await loadConversation({incremental:true});
    }
    if(action==='memory-evidence'){
      const m=s.memories.find(m=>m.memoryId===el.dataset.id);if(!m?.sourceMessageIds?.length)return;
      const evidence=await request('/api/evidence/current',{method:'POST',target:s,body:{messageIds:m.sourceMessageIds}});
      s.selectedEvidence=Array.isArray(evidence)?evidence:evidence?.items||evidence?.evidence||[];
    }
    if(action==='consult-evidence'){
      const turn=s.consultationHistory.find(t=>t.runId===el.dataset.id);if(!turn?.evidenceMessageIds?.length)return;
      const evidence=await request('/api/evidence/current',{method:'POST',target:s,body:{messageIds:turn.evidenceMessageIds}});
      s.selectedEvidence=Array.isArray(evidence)?evidence:evidence?.items||evidence?.evidence||[];
      if(s===current())openDrawer('memories');
    }
    if(action==='save-self'){
      s.expression=await request('/api/expression/current',{method:'PUT',target:s,body:{voice:$('selfVoice').value,boundary:$('selfBoundary').value,notes:$('selfNotes').value,revision:s.expression?.revision||0}});fields.delete(key());staleAll();notify('当前账号的表达设置已保存。');
    }
    if(action==='portrait-refresh'){
      s.portraitTask=await request('/api/portraits/current/refresh',{method:'POST',target:s,body:{}});changed();
      while(isBusy(s.portraitTask)){await new Promise(r=>setTimeout(r,1200));s.portraitTask=await request(`/api/ai/tasks/${encodeURIComponent(s.portraitTask.taskId)}`,{target:s});changed();}
      if(s.portraitTask.status==='Failed')throw new Error(`画像更新失败（${s.portraitTask.errorCode||'UNKNOWN'}）。`);
      s.portrait=await request('/api/portraits/current',{target:s});notify('画像任务已结束，请核对摘要与证据。');
    }
    if(action==='setup-check'){store.diagnostics.set(store.setupStep,{state:'Checking',checks:[]});renderDrawer();store.diagnostics.set(store.setupStep,await request('/api/operations/diagnostics',{method:'POST',scoped:false,body:{step:store.setupStep}}));}
    if(action==='toggle-pref'){const value={...store.preferences,[el.dataset.key]:!store.preferences[el.dataset.key]};store.preferences=await request('/api/preferences',{method:'PUT',scoped:false,body:value});notify('偏好已保存。');}
    if(action==='backfill'){s.historyJob=await request('/api/history/jobs',{method:'POST',target:s,body:{accountId:s.accountId,conversationId:s.conversationId,maximumRows:5000}});changed();void pollHistory(s).catch(error=>notify(errorText(error)));}
    if(action==='cancel-backfill'&&s.historyJob)s.historyJob=await request(`/api/history/jobs/${encodeURIComponent(s.historyJob.jobId)}`,{method:'DELETE',target:s});
    if(action==='discover'){
      const id=store.selectedAccount;let job=await request(`/api/accounts/${encodeURIComponent(id)}/discovery-jobs`,{method:'POST',body:{},scoped:false});notify('目录发现任务已受理，等待只读 Reader。');
      while(['Pending','PendingOrRunning','Queued','Running'].includes(job.status)){await new Promise(r=>setTimeout(r,1500));job=await request(`/api/accounts/${encodeURIComponent(id)}/discovery-jobs/${encodeURIComponent(job.jobId)}`,{scoped:false});}
      await refreshDirectory();notify(job.status==='Completed'?'联系人目录已更新。':`目录读取未完成（${job.errorCode||job.status}）。`);
    }
    if(action.startsWith('device-'))await deviceAction(el);
    changed();
  }catch(error){notify(errorText(error));}
}
export async function saveQuickRoute(value){try{store.preferences=await request('/api/preferences',{method:'PUT',scoped:false,body:{...store.preferences,quickRoute:value}});notify('快速模型路由偏好已保存；实际可用性以连接检测为准。');}catch(error){notify(errorText(error));}}

// Device UI stays inside V6 connection step 4. Server readiness governs availability.
async function loadDevices(){try{store.devices=await request('/api/devices',{scoped:false});changed();}catch(error){store.devicesError=errorText(error);changed();}}
function deviceMarkup(){return `<div class="group-heading">安全配对与设备</div>${store.devicesError?placeholder(store.devicesError):card('手机安全访问',store.operations?.mobileAccess?.message||'尚未启用',button('device-pair','开始配对',store.operations?.mobileAccess?.state==='Configured'?'':'disabled'))}${store.pairing?`<div class="notice"><a href="${escape(store.pairing.pairingUrl)}" rel="noreferrer">手机短期配对链接</a><br>有效期：${escape(time(store.pairing.expiresAtUtc))}<br>仅授予当前账号与当前联系人访问权限。</div>`:''}${(store.devices?.items||[]).map(d=>card(d.deviceName||'已配对设备',`有效期 ${time(d.expiresAtUtc)}${d.revokedAtUtc?' · 已撤销':''}`,button('device-revoke','撤销',`data-id="${escape(d.sessionId)}" ${d.revokedAtUtc?'disabled':''}`))).join('')}`;}
async function deviceAction(el){if(el.dataset.action==='device-pair'){store.pairing=await request('/api/devices/pairings',{method:'POST',body:{deviceName:'手机设备',accountIds:[store.selectedAccount],conversationIds:[store.selectedConversation]},scoped:false});}if(el.dataset.action==='device-revoke'){await request(`/api/devices/${encodeURIComponent(el.dataset.id)}`,{method:'DELETE',scoped:false});await loadDevices();}changed();}
