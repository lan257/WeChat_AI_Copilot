import {store,current,scope,account,contact,changed,notify,staleAll} from './store.js';
import {request,errorText} from './api.js';
import {$,escape,placeholder} from './dom.js';
import {choosePersona} from './chat.js';

export const stages=[
  {id:'message',title:'新消息到达',desc:'只安排轻量任务，不会自动回复'},
  {id:'context',title:'构建上下文',desc:'决定可申请哪些已授权资料'},
  {id:'reply',title:'生成回复前',desc:'应用表达策略或话题指导'},
  {id:'review',title:'回复生成后',desc:'检查候选事实、语气与承诺'},
  {id:'consult',title:'AI 咨询前',desc:'调整咨询关注点和分析方式'},
  {id:'memory',title:'记忆沉淀前',desc:'形成待审记忆建议，不直接入库'}
];
const stageNames=ids=>(ids||[]).map(id=>stages.find(s=>s.id===id)?.title||id);
const available=s=>s.enabled&&s.approvalStatus==='Approved'&&(s.scopeType==='AllAuthorizedAccounts'||(s.accountId===store.selectedAccount&&(s.scopeType==='Account'||s.conversationId===store.selectedConversation)));
const workflows=stage=>store.skills.filter(s=>s.type==='Workflow'&&available(s)&&s.stages?.includes(stage));
const scopeText=s=>s.scopeType==='AllAuthorizedAccounts'?'所有账号':s.scopeType==='Conversation'?'仅指定联系人':'当前微信账号';

export function openEditor(kind='Persona', aiMode=false, source=null) {
  kind=String(kind).toLowerCase()==='persona'?'Persona':'Workflow';
  store.skillEditor={
    ...(source||{}),skillId:source?.builtIn?null:source?.skillId||null,
    name:source?(source.builtIn?source.name==='温柔'?'我理解的温柔':`我的${source.name}`:source.name):(kind==='Persona'?'我理解的温柔':'我的回复检查'),
    type:source?.type||kind,description:source?.description||'',instruction:source?.instruction||'',
    stages:source?[...source.stages]:kind==='Persona'?['reply','consult']:['review'],
    scopeType:source&&!source.builtIn?source.scopeType:'Account',
    accountId:source&&!source.builtIn?source.accountId:store.selectedAccount,
    conversationId:source&&!source.builtIn?source.conversationId:store.selectedConversation,
    origin:aiMode?'AiAssisted':source&&!source.builtIn?source.origin:'Manual',aiMode,idea:'',editingScope:scope(),contactName:contact()?.displayName||'当前联系人',versions:[]
  };
  store.skillPreview='';store.drawerTab='agent';store.drawerOpen=true;changed();
  if(source&&!source.builtIn)void loadVersions(source.skillId);
}
async function loadVersions(id) {
  try{const versions=await request(`/api/skills/${encodeURIComponent(id)}/versions`,{target:store.skillEditor.editingScope});if(store.skillEditor?.skillId===id){store.skillEditor.versions=Array.isArray(versions)?versions:versions?.versions||[];changed();}}catch(error){notify(errorText(error));}
}
export function captureEditor() {
  const draft=store.skillEditor;if(!draft||!$('skillName'))return;
  draft.type=$('skillKind').value;draft.name=$('skillName').value;draft.description=$('skillDesc').value;
  draft.instruction=$('skillInstruction').value;draft.scopeType=$('skillScope').value;
  draft.stages=[...document.querySelectorAll('[data-skill-stage]:checked')].map(n=>n.dataset.skillStage);
  draft.idea=$('skillIdea')?.value||draft.idea;
}
function payload(draft) {
  return {type:draft.type,name:draft.name.trim(),description:draft.description.trim(),instruction:draft.instruction.trim(),
    stages:draft.stages,scopeType:draft.scopeType,accountId:draft.scopeType==='AllAuthorizedAccounts'?null:draft.editingScope.accountId,
    conversationId:draft.scopeType==='Conversation'?draft.editingScope.conversationId:null,
    enabled:draft.approvalStatus!=='Draft'&&!(draft.origin==='AiAssisted'&&!draft.skillId),origin:draft.origin,expectedRevision:draft.revision};
}
function editorMarkup() {
  const d=store.skillEditor;
  return `<div class="skill-editor"><div class="skill-actions" style="margin-top:0"><button class="skill-action" data-action="skill-back">← 返回管理</button><span class="skill-tag ${d.origin==='AiAssisted'?'ai':''}">${d.origin==='AiAssisted'?'AI 辅助草稿':'个人编辑草稿'}</span></div>
    <h3>${d.skillId?'编辑':'创建'}${d.type==='Persona'?'人设 Skill':'流程 Skill'}</h3><p class="muted">只保存描述、规则和固定阶段；不会执行脚本、访问任意文件或自动发送微信。</p>
    ${d.aiMode?`<div class="skill-ai-box"><strong>描述你想要的效果，让 AI 帮你起草</strong><p>例如：“温柔但不讨好；先接住对方情绪，不撒娇、不用过多语气词。遇到邀约不要擅自答应。” AI 生成后仍由你编辑并确认。</p><textarea class="skill-textarea" id="skillIdea" rows="3" placeholder="用自己的话描述人设或希望自动执行的检查…">${escape(d.idea)}</textarea><button class="skill-action main" data-action="skill-ai-compose" ${store.skillBusy?'disabled':''}>${store.skillBusy?'正在起草…':'AI 起草'}</button></div>`:''}
    <div class="skill-field"><label for="skillKind">Skill 类型</label><select id="skillKind" class="skill-select"><option value="Persona" ${d.type==='Persona'?'selected':''}>人设 · 表达风格</option><option value="Workflow" ${d.type==='Workflow'?'selected':''}>流程 · 专项能力</option></select></div>
    <div class="skill-field"><label for="skillName">名称</label><input id="skillName" class="skill-input" maxlength="34" value="${escape(d.name)}" placeholder="例如：克制的温柔"></div>
    <div class="skill-field"><label for="skillDesc">一句话说明</label><input id="skillDesc" class="skill-input" maxlength="100" value="${escape(d.description)}" placeholder="这个 Skill 解决什么沟通问题？"></div>
    <div class="skill-field"><label for="skillInstruction">行为规则（自然语言，可自定义）</label><textarea id="skillInstruction" class="skill-textarea" rows="5" maxlength="2000" placeholder="写出你想要的具体表达方式、应该避免的词句、检查范围…">${escape(d.instruction)}</textarea><small>人设不是一个形容词：建议明确“应该做什么”“不要做什么”“如何判断是否符合”。</small></div>
    <div class="skill-field"><label for="skillScope">适用范围</label><select id="skillScope" class="skill-select"><option value="Account" ${d.scopeType==='Account'?'selected':''}>当前微信账号的所有联系人</option><option value="Conversation" ${d.scopeType==='Conversation'?'selected':''}>仅当前联系人 · ${escape(d.contactName)}</option><option value="AllAuthorizedAccounts" ${d.scopeType==='AllAuthorizedAccounts'?'selected':''}>所有已授权微信账号</option></select><small>作用域由服务器保存并校验，防止跨联系人混用。</small></div>
    <div class="skill-field"><label>在哪些固定阶段调用？</label><div class="skill-stagemap">${stages.map(s=>`<label class="skill-stage-option"><input type="checkbox" data-skill-stage="${s.id}" ${d.stages.includes(s.id)?'checked':''}><span><strong>${s.title}</strong><small>${s.desc}</small></span></label>`).join('')}</div><small>只会在列出的阶段触发；与 Quick/Max 预算和访问权限分开管理。可组合多个流程 Skill。</small></div>
    <div class="skill-review"><strong>保存前需要你确认</strong>：由 AI 创建的只是可编辑草稿；不会自动启用。文本不能授予读取其他联系人、Shell、网络访问或微信发送权限。若多个 Skill 规则冲突，以系统安全边界和你的当前人设选择为准。</div>
    <div class="skill-actions"><button class="skill-action" data-action="skill-preview" ${store.skillBusy?'disabled':''}>查看执行预览</button><button class="skill-action main" data-action="skill-save" ${store.skillBusy?'disabled':''}>${store.skillBusy?'正在提交…':'确认保存并启用'}</button><button class="skill-action" data-action="skill-back">取消</button></div>
    ${store.skillPreview?`<div class="skill-preview">${escape(store.skillPreview)}</div>`:''}
    ${d.versions?.length?`<div class="skill-field"><label>历史版本</label><select id="skillVersion" class="skill-select">${d.versions.map(v=>`<option value="${v.revision}">版本 ${v.revision} · ${escape(v.name)}</option>`).join('')}</select><button class="skill-action" data-action="skill-restore" ${store.skillBusy?'disabled':''}>恢复选定版本</button></div>`:''}</div>`;
}
export function renderSkills() {
  const heading='<div class="skill-lead"><h3>你来定义人设，Skill 决定何时生效</h3><p>“温柔”不必是系统理解的温柔。可以复制预设再写出自己的表达规则，也可以描述需求由 AI 辅助起草。人设本身就是一种特殊的 Skill；其他 Skill 可以在消息、上下文、生成、复核、咨询和记忆阶段运行。</p></div>';
  if(store.skillEditor)return heading+editorMarkup();
  const rows=type=>store.skills.filter(s=>s.type===type&&!s.deleted).map(s=>`<article class="skill-item ${available(s)?'':'is-off'} ${type==='Workflow'?'workflow':''}"><span class="skill-mark">${type==='Persona'?'语':'⚙'}</span><div class="skill-item-main"><div class="skill-item-title"><strong>${escape(s.name)}</strong><span class="skill-tag">${s.builtIn?'系统模板':'自定义'}</span><span class="skill-tag">${escape(scopeText(s))}</span>${s.origin==='AiAssisted'?'<span class="skill-tag ai">AI 辅助</span>':''}${s.approvalStatus==='Draft'?'<span class="skill-tag">待确认草稿</span>':''}</div><p>${escape(s.description||s.instruction||'尚未写描述')}</p><div class="skill-stages">${stageNames(s.stages).map(v=>`<span>${escape(v)}</span>`).join('')}</div></div><div class="skill-item-acts">${type==='Persona'&&available(s)?`<button class="skill-mini ${current().persona===s.skillId?'skill-on':''}" data-action="skill-use" data-id="${escape(s.skillId)}">${current().persona===s.skillId?'当前使用':'设为人设'}</button>`:''}<button class="skill-mini" data-action="skill-${s.builtIn?'clone':'edit'}" data-id="${escape(s.skillId)}">${s.builtIn?'复制定制':'编辑'}</button>${!s.builtIn?`<button class="skill-mini" data-action="skill-toggle" data-id="${escape(s.skillId)}">${s.enabled?'停用':'启用'}</button><button class="skill-mini" data-action="skill-delete" data-id="${escape(s.skillId)}">删除</button>`:''}</div></article>`).join('')||placeholder('当前范围没有此类 Skill。');
  return heading+`<div class="skill-actions"><button class="skill-action main" data-action="skill-new" data-kind="Persona">＋ 自己创建人设</button><button class="skill-action" data-action="skill-ai-new" data-kind="Persona">✦ AI 帮我创建</button><button class="skill-action" data-action="skill-new" data-kind="Workflow">＋ 创建流程 Skill</button><button class="skill-action" data-action="skill-ai-new" data-kind="Workflow">✦ AI 创建流程</button></div>
    <div class="notice">当前范围：账号「${escape(account()?.displayName||'当前微信账号')}」· 联系人「${escape(contact()?.displayName||'当前联系人')}」。回复时只使用你选中的<strong>一个人设</strong>，并叠加当前范围内已启用的流程 Skill；具体执行仍受服务端权限、时间和检索预算约束。</div>
    <div class="skill-section-head"><strong>① 人设 Skills</strong><small>可选择 / 复制修改 / 联系人专用</small></div><div class="skill-list">${rows('Persona')}</div>
    <div class="skill-section-head"><strong>② 生命周期中的流程 Skills</strong><small>按阶段运行，可启用多个</small></div><div class="skill-lifecycle">${stages.map(s=>`<div class="skill-life-step ${workflows(s.id).length?'is-active':''}"><b>${s.title}</b>${workflows(s.id).length} 个流程 Skill</div>`).join('')}</div><div class="skill-list">${rows('Workflow')}</div>
    <div class="skill-section-head"><strong>③ 模型与强度</strong><small>独立于人设 / Skill</small></div><div class="setting-card"><div class="setting-main"><strong>快速回复模型</strong><p>Quick 默认需要低延迟路径；Deep / Max 可检索、复核，但不会突破数据授权范围。</p></div><select class="pill-select" id="quickRouteSelect"><option value="fast" ${store.preferences.quickRoute==='fast'?'selected':''}>轻量模型优先</option><option value="codex" ${store.preferences.quickRoute==='codex'?'selected':''}>Codex CLI</option></select></div><div class="notice warn">AI 草稿须明确确认后启用；预览显示真实作用范围和固定阶段，不执行脚本。Quick 实际耗时以运行记录为准。</div>`;
}
export async function reloadSkills() {
  const result=await request('/api/skills');store.skills=Array.isArray(result)?result:result?.skills||[];changed();
}
export async function skillAction(el) {
  const action=el.dataset.action;
  if(action==='skill-new'||action==='skill-ai-new'){openEditor(el.dataset.kind,action==='skill-ai-new');return;}
  if(action==='skill-edit'||action==='skill-clone'){const source=store.skills.find(s=>s.skillId===el.dataset.id);if(source)openEditor(source.type,false,source);return;}
  if(action==='skill-back'){captureEditor();store.skillEditor=null;store.skillPreview='';changed();return;}
  if(store.skillBusy)return;
  captureEditor();store.skillBusy=true;
  try {
    const d=store.skillEditor;
    if(action==='skill-ai-compose'){
      if(d.idea.trim().length<6)throw new Error('请先写清楚希望的表达方式或检查任务（至少 6 字）。');
      changed();
      const result=await request('/api/skills/drafts',{method:'POST',target:d.editingScope,body:{type:d.type,idea:d.idea,scopeType:d.scopeType,accountId:d.scopeType==='AllAuthorizedAccounts'?null:d.editingScope.accountId,conversationId:d.scopeType==='Conversation'?d.editingScope.conversationId:null}});
      store.skillEditor={...d,...result,aiMode:true};store.skillPreview='AI 草稿已保存，尚未启用。请检查规则、范围和阶段，确认后才会参与运行。';
    }else if(action==='skill-preview'){
      const result=await request('/api/skills/preview',{method:'POST',target:d.editingScope,body:payload(d)});
      store.skillPreview=`作用域：${scopeText(d)}\n执行阶段：${stageNames(result.stages).join(' → ')}\n影响：${Array.isArray(result.impact)?result.impact.join('\n'):result.impact||''}\n边界：${Array.isArray(result.boundaries)?result.boundaries.join('\n'):result.boundaries||''}`;
    }else if(action==='skill-save'){
      const body=payload(d);if(!body.name||body.instruction.length<8||!body.stages.length)throw new Error('请填写名称、至少 8 字规则及生命周期阶段。');
      const result=await request(d.skillId?`/api/skills/${encodeURIComponent(d.skillId)}`:'/api/skills',{method:d.skillId?'PUT':'POST',target:d.editingScope,body});
      const saved=result.approvalStatus==='Draft'?await request(`/api/skills/${encodeURIComponent(result.skillId)}/approve`,{method:'POST',target:d.editingScope,body:{expectedRevision:result.revision,enabled:true}}):result;
      store.skillEditor=null;store.skillPreview='';staleAll();await reloadSkills();if(saved.type==='Persona'&&d.editingScope.accountId===store.selectedAccount&&d.editingScope.conversationId===store.selectedConversation)await choosePersona(saved.skillId);notify('已确认保存并启用 Skill。');
    }else if(action==='skill-restore'){
      const revision=Number($('skillVersion').value);
      const result=await request(`/api/skills/${encodeURIComponent(d.skillId)}/restore`,{method:'POST',target:d.editingScope,body:{revision,expectedRevision:d.revision}});
      store.skillEditor={...d,...result};staleAll();await reloadSkills();notify('已恢复历史版本，请核对启用状态。');
    }else {
      const skill=store.skills.find(s=>s.skillId===el.dataset.id);if(!skill)return;
      if(action==='skill-use')await choosePersona(skill.skillId);
      if(action==='skill-toggle'){
        if(skill.approvalStatus==='Draft'){openEditor(skill.type,false,skill);notify('请检查 AI 草稿并明确确认保存后启用。');return;}
        await request(`/api/skills/${encodeURIComponent(skill.skillId)}`,{method:'PUT',body:{...skill,enabled:!skill.enabled,expectedRevision:skill.revision}});staleAll();await reloadSkills();
      }
      if(action==='skill-delete'){
        if(!window.confirm(`删除自定义 Skill「${skill.name}」？这不会删除消息或记忆。`))return;
        await request(`/api/skills/${encodeURIComponent(skill.skillId)}?expectedRevision=${skill.revision}`,{method:'DELETE'});staleAll();await reloadSkills();notify('已停用并删除自定义 Skill。');
      }
    }
  }catch(error){notify(errorText(error));store.skillPreview=errorText(error);}finally{store.skillBusy=false;changed();}
}
