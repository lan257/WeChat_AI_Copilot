import {store,current,directory,changed,notify} from './store.js';
import {$} from './dom.js';
import {hydrateIcons} from './icons.js';
import {errorText} from './api.js';
import {bootstrap,refresh,selectConversation,renderDirectory,renderHeader,renderMessages,renderSuggestions,renderConsult,generate,ask,older,copy,cancelTask,choosePersona} from './chat.js';
import {skillAction,captureEditor} from './skills.js';
import {openDrawer,closeDrawer,renderDrawer,drawerAction,saveQuickRoute} from './drawers.js';

let toastTimer;
let renderScheduled=false;
let focusBeforeDrawer=null;
function render(){renderDirectory();renderHeader();renderMessages();renderSuggestions();renderConsult();renderDrawer();}
document.addEventListener('copilot:state',()=>{
  if(renderScheduled)return;renderScheduled=true;
  requestAnimationFrame(()=>{renderScheduled=false;render();});
});
document.addEventListener('copilot:notify',event=>{
  $('toast').textContent=event.detail;$('toast').classList.add('show');clearTimeout(toastTimer);toastTimer=setTimeout(()=>$('toast').classList.remove('show'),3500);
});
function mobileClose(){
  $('sidebar').classList.remove('open');$('sideOverlay').classList.add('hidden');
  $('aiPanel').classList.remove('open');$('aiOverlay').classList.add('hidden');
}
function focus(active){document.body.classList.toggle('focus-mode',active);$('focusDetails').classList.add('hidden');mobileClose();renderSuggestions();}
async function handleAction(event){
  const el=event.target.closest('[data-action]');if(!el||el.disabled)return;
  const action=el.dataset.action;
  try{
    if(action.startsWith('skill-')){await skillAction(el);return;}
    if(action==='switch'){mobileClose();await selectConversation(el.dataset.id);return;}
    if(action==='filter'){store.filter=el.dataset.filter;document.querySelectorAll('[data-filter]').forEach(b=>b.classList.toggle('active',b.dataset.filter===store.filter));renderDirectory();return;}
    if(action==='refresh'){await refresh();return;}
    if(action==='focus'){focus(true);return;}
    if(action==='workspace'){focus(false);closeDrawer();return;}
    if(action==='focus-expand'){$('focusDetails').classList.toggle('hidden');el.setAttribute('aria-expanded',String(!$('focusDetails').classList.contains('hidden')));return;}
    if(action==='focus-choose'){focus(false);$('sidebar').classList.add('open');$('sideOverlay').classList.remove('hidden');return;}
    if(action==='focus-persona'){
      const options=[...$('personaSelect').options];if(!options.length)return;
      const selected=current(),idx=options.findIndex(o=>o.value===selected.persona);await choosePersona(options[(idx+1)%options.length].value);if(selected===current())await generate();return;
    }
    if(action==='generate'){await generate();return;}
    if(action==='cancel-generate'){await cancelTask('suggestion');return;}
    if(action==='cancel-consultation'){await cancelTask('consultation');return;}
    if(action==='copy'){await copy(Number(el.dataset.idx));return;}
    if(action==='ask'||action==='ask-example'){await ask(action==='ask-example'?el.dataset.q:undefined);return;}
    if(action==='older'){await older();return;}
    if(action==='drawer'||action==='tab'){
      focusBeforeDrawer=store.drawerOpen?focusBeforeDrawer:el;openDrawer(el.dataset.tab||'overview');$('drawer').querySelector('[data-action="close-drawer"]').focus();return;
    }
    if(action==='close-drawer'){closeDrawer();focusBeforeDrawer?.focus();return;}
    if(action==='mobile-contacts'){$('sidebar').classList.toggle('open');$('sideOverlay').classList.toggle('hidden');$('aiPanel').classList.remove('open');$('aiOverlay').classList.add('hidden');return;}
    if(action==='ai-open'){$('aiPanel').classList.add('open');$('aiOverlay').classList.remove('hidden');$('aiInput').focus();return;}
    if(action==='ai-close'){$('aiPanel').classList.remove('open');$('aiOverlay').classList.add('hidden');return;}
    await drawerAction(el);
  }catch(error){notify(errorText(error));}
}
document.addEventListener('click',event=>{void handleAction(event);});
$('contactSearch').addEventListener('input',renderDirectory);
$('accountSelect').addEventListener('change',async event=>{
  store.selectedAccount=event.target.value;
  const first=directory()[0];if(first)await selectConversation(first.conversationId,store.selectedAccount);else{store.selectedConversation=null;changed();}
});
$('personaSelect').addEventListener('change',async event=>{try{await choosePersona(event.target.value);}catch(error){notify(errorText(error));renderSuggestions();}});
$('thinkingSelect').addEventListener('change',event=>{current().level=event.target.value;current().stale=Boolean(current().suggestions);renderSuggestions();});
$('consultThinking').addEventListener('change',event=>{current().consultLevel=event.target.value;});
$('aiInput').addEventListener('input',event=>{current().input=event.target.value;});
$('aiInput').addEventListener('keydown',event=>{if(event.key==='Enter'&&!event.shiftKey){event.preventDefault();void ask();}});
$('drawerContent').addEventListener('input',captureEditor);
$('drawerContent').addEventListener('change',event=>{captureEditor();if(event.target.id==='quickRouteSelect')void saveQuickRoute(event.target.value);});
document.addEventListener('keydown',event=>{
  if(event.key==='Escape'){if(store.drawerOpen)closeDrawer();else mobileClose();}
  if(event.key==='Tab'&&store.drawerOpen){
    const nodes=[...$('drawer').querySelectorAll('button:not(:disabled),input,textarea,select,summary')].filter(n=>n.offsetParent!==null);
    if(!nodes.length)return;const first=nodes[0],last=nodes.at(-1);
    if(event.shiftKey&&document.activeElement===first){event.preventDefault();last.focus();}
    else if(!event.shiftKey&&document.activeElement===last){event.preventDefault();first.focus();}
  }
});
window.addEventListener('beforeunload',event=>{if(store.skillEditor){captureEditor();event.preventDefault();event.returnValue='';}});
hydrateIcons();render();void bootstrap();
// Reconcile only real server snapshots. Hidden tabs resume when visible, without synthetic events.
let refreshBusy=false;
setInterval(async()=>{
  if(!store.authenticated||document.hidden||refreshBusy||current().loading)return;
  refreshBusy=true;try{await refresh();}finally{refreshBusy=false;}
},5000);
document.addEventListener('visibilitychange',()=>{if(!document.hidden&&store.authenticated)void refresh();});
