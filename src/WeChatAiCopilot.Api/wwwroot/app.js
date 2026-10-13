(() => {
  const REVISION_POLL_INTERVAL_MS = 5000;
  const $ = (id) => document.getElementById(id);
  const workspaceView = $('workspaceView');
  const minimalView = $('minimalView');
  const modeButtons = document.querySelectorAll('[data-mode]');
  const pageButtons = document.querySelectorAll('[data-page]');
  const workspacePages = {
    chat: $('chatPage'),
    connection: $('connectionPage'),
    memory: $('memoryPage'),
    status: $('statusPage')
  };
  const refreshSystemStatusButton = $('refreshSystemStatus');
  const systemStatusFeedback = $('systemStatusFeedback');
  const apiHealthValue = $('apiHealthValue');
  const apiHealthDetail = $('apiHealthDetail');
  const databaseHealthValue = $('databaseHealthValue');
  const databaseHealthDetail = $('databaseHealthDetail');
  const agentRuntimeValue = $('agentRuntimeValue');
  const agentRuntimeDetail = $('agentRuntimeDetail');
  const readerStatusList = $('readerStatusList');
  const agentRunValue = $('agentRunValue');
  const agentRunDetail = $('agentRunDetail');
  const memoryRuntimeValue = $('memoryRuntimeValue');
  const memoryRuntimeDetail = $('memoryRuntimeDetail');
  const historyJobsValue = $('historyJobsValue');
  const historyJobsDetail = $('historyJobsDetail');
  const expandButton = $('expandMinimal');
  const minimalDetails = $('minimalDetails');
  const webAuthForm = $('webAuthForm');
  const webTokenInput = $('webToken');
  const connectButton = $('connectApi');
  const clearButton = $('clearApi');
  const apiStatus = $('apiStatus');
  const serviceIndicator = $('serviceIndicator');
  const serviceIndicatorText = $('serviceIndicatorText');
  const accountSelect = $('accountSelect');
  const chatAccountSelect = $('chatAccountSelect');
  const conversationSelect = $('conversationSelect');
  const chatConversationSelect = $('chatConversationSelect');
  const discoverReaderSessionsButton = $('discoverReaderSessions');
  const discoveryStatus = $('discoveryStatus');
  const readerTargetList = $('readerTargetList');
  const readerTargetSearch = $('readerTargetSearch');
  const saveReaderTargetsButton = $('saveReaderTargets');
  const readerTargetStatus = $('readerTargetStatus');
  const currentSessionValue = $('currentSessionValue');
  const currentSessionStatus = $('currentSessionStatus');
  const chatSessionValue = $('chatSessionValue');
  const chatSessionType = $('chatSessionType');
  const chatCaptureStatus = $('chatCaptureStatus');
  const captureStateValue = $('captureStateValue');
  const memoryCurrentSession = $('memoryCurrentSession');
  const minimalCurrentSession = $('minimalCurrentSession');
  const messageArea = document.querySelector('.message-area');
  const messageStatusPill = $('messageStatusPill');
  const perspectiveSummary = $('perspectiveSummary');
  const perspectiveStatus = $('perspectiveStatus');
  const identityHelp = $('identityHelp');
  const selfIdentity = document.querySelector('[data-identity="self"]');
  const contactIdentity = document.querySelector('[data-identity="contact"]');
  const confirmCurrentButton = $('confirmCurrent');
  const swapButton = $('swapPreview');
  const previewState = $('previewState');
  const confirmHelp = $('confirmHelp');
  const toast = $('toast');
  const memoryStatus = $('memoryStatus');
  const memoryFeedback = $('memoryFeedback');
  const memoryCount = $('memoryCount');
  const memoryItemsState = $('memoryItemsState');
  const memoryList = $('memoryList');
  const memoryCreateForm = $('memoryCreateForm');
  const memoryCreateFields = $('memoryCreateFields');
  const reloadMemoriesButton = $('reloadMemories');
  const suggestionStatus = $('suggestionStatus');
  const suggestionState = $('suggestionState');
  const suggestionStateTitle = $('suggestionStateTitle');
  const suggestionStateDetail = $('suggestionStateDetail');
  const suggestionList = $('suggestionList');
  const personaSelect = $('personaSelect');
  const replyProfileSelect = $('replyProfileSelect');
  const consultationProfileSelect = $('consultationProfileSelect');
  const reloadPersonasButton = $('reloadPersonas');
  const generateSuggestionsButton = $('generateSuggestions');
  const cancelSuggestionButton = $('cancelSuggestion');
  const suggestionFeedback = $('suggestionFeedback');
  const minimalStatus = $('minimalStatus');
  const minimalSuggestionState = $('minimalSuggestionState');
  const minimalChoices = $('minimalChoices');
  const minimalPersonaButton = $('minimalPersonaButton');
  const minimalPersonaLabel = $('minimalPersonaLabel');
  const minimalRefreshButton = $('minimalRefresh');
  const consultationStatus = $('consultationStatus');
  const consultationTurns = $('consultationTurns');
  const consultationForm = $('consultationForm');
  const consultationQuestion = $('consultQuestion');
  const consultationSubmit = $('consultSubmit');
  const cancelConsultationButton = $('cancelConsultation');
  const consultationHint = $('consultationHint');
  const consultationNotice = $('consultationNotice');

  let webToken = null;
  let currentConversation = null;
  let selectedAccountId = null;
  let selectedConversationId = null;
  let directoryAccounts = [];
  let directoryConversations = [];
  let readerTargetVersion = 0;
  let pendingReaderTargetIds = null;
  let pendingReaderTargetAccountId = null;
  let discoveryPollTimer = null;
  let activeDiscoveryJobId = null;
  let currentMessageSnapshot = null;
  let olderPageController = null;
  let olderPageInProgress = false;
  let currentPerspectiveResponse = null;
  let activeScope = null;
  let previewPair = null;
  let requestVersion = 0;
  let activeController = null;
  let toastTimer;
  let memoryRecords = [];
  let memoryWritable = false;
  let editingMemoryId = null;
  let memoryReadVersion = 0;
  let memoryReadController = null;
  let memoryWriteController = null;
  let memoryWritePending = false;
  let currentWorkspacePage = 'chat';
  let revisionPollingEnabled = false;
  let revisionPollTimer = null;
  let revisionPollController = null;
  let revisionPollInProgress = false;
  let lastMessageSetRevision = null;
  let systemStatusController = null;
  let systemStatusRequestVersion = 0;
  let pollFailureSnapshot = null;
  let personaCatalog = [];
  let personaCatalogStatus = 'idle';
  let personaCatalogFeedback = '';
  let personaCatalogVersion = 0;
  let personaCatalogController = null;
  let executionProfileCatalog = [];
  let executionProfileCatalogStatus = 'idle';
  let executionProfileController = null;
  let workspaceReplyProfileId = 'Quick';
  let suggestionController = null;
  let consultationController = null;
  let consultationHistoryController = null;
  let agentStateByScope = new Map();
  let activeAgentScopeKey = null;
  let activeAgentState = null;

  function setWorkspacePage(page) {
    if (!Object.prototype.hasOwnProperty.call(workspacePages, page)) return;
    currentWorkspacePage = page;
    Object.entries(workspacePages).forEach(([name, element]) => {
      element.hidden = name !== page;
    });
    pageButtons.forEach((button) => {
      const active = button.dataset.page === page;
      button.classList.toggle('is-current', active);
      if (active) button.setAttribute('aria-current', 'page');
      else button.removeAttribute('aria-current');
    });
    if (page === 'status') void loadSystemStatus();
    else if (systemStatusController) {
      systemStatusController.abort();
      systemStatusController = null;
      systemStatusRequestVersion++;
    }
    if (page === 'chat' && activeAgentState?.threadId && !activeAgentState.historyLoaded && !activeAgentState.historyLoading) {
      void restoreConsultationHistory(activeAgentState);
    }
  }

  function setSystemStatusValue(element, label, state) {
    element.textContent = label;
    element.dataset.state = state;
  }

  function readerStateLabel(state, targetCount) {
    switch (String(state || '').toLowerCase()) {
      case 'listening': return `在线监听 · ${targetCount} 个会话`;
      case 'idle': return '在线 · 暂无监听目标';
      case 'starting': return '正在启动';
      case 'error': return '运行错误';
      case 'stopped': return '已停止';
      case 'offline': return '离线';
      default: return '状态未知';
    }
  }

  function renderReaderSystemStatus(status) {
    readerStatusList.replaceChildren();
    const accounts = Array.isArray(status.readerAccounts) ? status.readerAccounts : [];
    if (status.readerState !== 'Available') {
      const empty = document.createElement('p');
      empty.className = 'system-status-empty';
      empty.textContent = status.readerState === 'NotAvailable'
        ? '数据库未配置，Reader 状态不可读取。'
        : 'Reader 状态数据暂不可用。';
      readerStatusList.append(empty);
      return;
    }
    if (!accounts.length) {
      const empty = document.createElement('p');
      empty.className = 'system-status-empty';
      empty.textContent = '尚无 Reader 心跳记录；这不代表 Reader 正在运行。';
      readerStatusList.append(empty);
      return;
    }

    accounts.forEach((account) => {
      const row = document.createElement('article');
      row.className = 'system-status-row';
      const main = document.createElement('div');
      main.className = 'system-status-row-main';
      const name = document.createElement('strong');
      name.textContent = account.displayName || account.provider || '本机微信账号';
      const details = document.createElement('span');
      const targetCount = Number.isInteger(account.listeningTargetCount) ? account.listeningTargetCount : 0;
      details.textContent = `最近心跳：${formatTime(account.lastHeartbeatAtUtc)} · 监听目标 ${targetCount} 个`;
      main.append(name, details);
      const state = document.createElement('span');
      const stateText = readerStateLabel(account.state, targetCount);
      state.className = `system-status-badge is-${String(account.state || 'unknown').toLowerCase()}`;
      state.textContent = stateText;
      row.append(main, state);
      if (typeof account.lastErrorCode === 'string' && account.lastErrorCode) {
        const error = document.createElement('small');
        error.className = 'system-status-error';
        error.textContent = `最近错误代码：${account.lastErrorCode}`;
        row.append(error);
      }
      readerStatusList.append(row);
    });
  }

  function renderLatestAgentRun(status) {
    const run = status.latestAgentRun;
    if (!run) {
      setSystemStatusValue(agentRunValue,
        status.agentRunState === 'Available' ? '暂无运行记录' : '不可用',
        status.agentRunState === 'Available' ? 'muted' : 'error');
      agentRunDetail.textContent = status.agentRunState === 'Available'
        ? '尚未发现已记录的建议或咨询任务。'
        : '运行审计数据暂不可读取。';
      return;
    }

    const task = String(run.taskType || '').toLowerCase() === 'replycandidates' ? '回复建议' : 'AI 咨询';
    const runState = String(run.status || '').toLowerCase();
    const labels = { running: '运行中', completed: '已完成', failed: '失败', cancelled: '已取消' };
    const state = runState === 'completed' ? 'success' : runState === 'running' ? 'loading' : 'error';
    setSystemStatusValue(agentRunValue, `${task} · ${labels[runState] || '状态未知'}`, state);
    const parts = [
      `运行方式：${run.runtimeName || '未知'}`,
      `开始时间：${formatTime(run.startedAtUtc)}`
    ];
    if (run.endedAtUtc) parts.push(`结束时间：${formatTime(run.endedAtUtc)}`);
    if (run.errorCode) parts.push(`错误代码：${run.errorCode}`);
    agentRunDetail.textContent = parts.join(' · ');
  }

  async function loadSystemStatus() {
    if (!webToken) {
      refreshSystemStatusButton.disabled = true;
      systemStatusFeedback.textContent = '请先在“接入与会话”完成授权。';
      return;
    }
    if (systemStatusController) systemStatusController.abort();
    const controller = new AbortController();
    const requestVersion = ++systemStatusRequestVersion;
    systemStatusController = controller;
    refreshSystemStatusButton.disabled = true;
    systemStatusFeedback.textContent = '正在读取运行状态…';

    const [apiResult, databaseResult, statusResult] = await Promise.all([
      apiRequest('/health/live', { signal: controller.signal }),
      apiRequest('/health/ready', { signal: controller.signal }),
      apiRequest('/api/system/status', { signal: controller.signal })
    ]);
    if (requestVersion !== systemStatusRequestVersion || apiResult.aborted || databaseResult.aborted || statusResult.aborted) return;
    if (isUnauthorized(statusResult)) {
      handleUnauthorized();
      return;
    }

    if (apiResult.ok && String(apiResult.data?.status || '').toLowerCase() === 'ok') {
      setSystemStatusValue(apiHealthValue, '在线', 'success');
      apiHealthDetail.textContent = 'API 存活探针正常响应。';
    } else {
      setSystemStatusValue(apiHealthValue, '不可用', 'error');
      apiHealthDetail.textContent = 'API 存活探针未成功响应。';
    }

    if (databaseResult.ok && String(databaseResult.data?.database || '').toLowerCase() === 'ready') {
      setSystemStatusValue(databaseHealthValue, '就绪', 'success');
      databaseHealthDetail.textContent = 'PostgreSQL 连通性检查通过。';
    } else {
      const databaseState = String(databaseResult.data?.database || '').toLowerCase();
      const label = databaseState === 'not-configured' ? '未配置' : databaseState === 'unavailable' ? '不可用' : '未就绪';
      setSystemStatusValue(databaseHealthValue, label, 'error');
      databaseHealthDetail.textContent = '数据库未通过就绪检查，消息与 AI 功能可能不可用。';
    }

    if (!statusResult.ok || !statusResult.data || typeof statusResult.data !== 'object') {
      renderReaderSystemStatus({ readerState: 'Unavailable', readerAccounts: [] });
      renderLatestAgentRun({ agentRunState: 'Unavailable', latestAgentRun: null });
      setSystemStatusValue(agentRuntimeValue, '不可用', 'error');
      agentRuntimeDetail.textContent = '系统状态接口没有返回有效信息。';
      setSystemStatusValue(memoryRuntimeValue, '不可用', 'error');
      memoryRuntimeDetail.textContent = '自动记忆运行配置暂不可读取。';
      setSystemStatusValue(historyJobsValue, '不可用', 'error');
      historyJobsDetail.textContent = '历史补录任务状态暂不可读取。';
      systemStatusFeedback.textContent = `部分状态不可用：${formatRequestError(statusResult).message}`;
    } else {
      const status = statusResult.data;
      renderReaderSystemStatus(status);
      renderLatestAgentRun(status);
      const runtimeLabels = { codexcli: 'Codex CLI', fake: 'Fake 演示运行时', disabled: '未配置' };
      const runtimeKey = String(status.configuredRuntime || '').toLowerCase();
      setSystemStatusValue(agentRuntimeValue, runtimeLabels[runtimeKey] || '未知', runtimeKey === 'disabled' ? 'error' : 'success');
      agentRuntimeDetail.textContent = runtimeKey === 'codexcli' && !status.quickModelConfigured
        ? '服务器已选择 Codex CLI，但未配置独立 Quick model；该状态不代表任务已成功或运行速度达标。'
        : '此处显示服务器运行时配置；任务成败以最近运行记录为准。';
      const memoryConfigured = String(status.memoryConsolidationRuntime || '').toLowerCase() === 'codexcli';
      setSystemStatusValue(memoryRuntimeValue, memoryConfigured ? '已选择 Codex CLI' : '未启用', memoryConfigured ? 'success' : 'muted');
      memoryRuntimeDetail.textContent = memoryConfigured
        ? '服务器已选择 Codex CLI；本状态不表示已有记忆沉淀成功。'
        : '服务器未选择自动记忆生成运行时；不会把人工记忆误报为自动学习。';
      const historyImplemented = String(status.historyJobsState || '').toLowerCase() !== 'notimplemented';
      setSystemStatusValue(historyJobsValue, historyImplemented ? '可用' : '未实现', historyImplemented ? 'success' : 'error');
      historyJobsDetail.textContent = historyImplemented
        ? '请在接入与会话页面查看任务详情。'
        : '历史补录任务进度与取消接口尚未实现。';
      systemStatusFeedback.textContent = '运行状态已更新；页面仅展示状态元数据，不包含聊天正文。';
    }

    if (systemStatusController === controller) systemStatusController = null;
    refreshSystemStatusButton.disabled = false;
  }

  function setMode(mode) {
    const minimal = mode === 'minimal';
    const wasMinimal = document.body.classList.contains('is-minimal');
    if (minimal && !wasMinimal) {
      workspaceReplyProfileId = replyProfileSelect.value || workspaceReplyProfileId;
      if (executionProfileCatalog.some((profile) => profile.executionProfileId === 'Quick')) {
        replyProfileSelect.value = 'Quick';
      }
    } else if (!minimal && wasMinimal
      && executionProfileCatalog.some((profile) => profile.executionProfileId === workspaceReplyProfileId)) {
      replyProfileSelect.value = workspaceReplyProfileId;
    }
    workspaceView.hidden = minimal;
    minimalView.hidden = !minimal;
    document.body.classList.toggle('is-minimal', minimal);

    document.querySelectorAll('.mode-button').forEach((button) => {
      const active = button.dataset.mode === mode;
      button.classList.toggle('is-active', active);
      button.setAttribute('aria-pressed', String(active));
    });

    if (minimal) {
      expandButton.focus({ preventScroll: true });
    } else {
      expandButton.setAttribute('aria-expanded', 'false');
      expandButton.innerHTML = '<span class="expand-chevron" aria-hidden="true">⌄</span>展开';
      minimalDetails.hidden = true;
      setWorkspacePage('chat');
    }
    if (activeAgentState) renderAgentState();
  }

  function setApiStatus(message, state = 'idle', fromPoll = false) {
    if (!fromPoll) pollFailureSnapshot = null;
    apiStatus.textContent = message;
    apiStatus.dataset.state = state;
    serviceIndicatorText.textContent = message.split(/[。；]/, 1)[0];
    serviceIndicator.dataset.state = state;
  }

  function isPageVisible() {
    return document.visibilityState !== 'hidden';
  }

  function clearRevisionPollTimer() {
    if (revisionPollTimer !== null) {
      window.clearTimeout(revisionPollTimer);
      revisionPollTimer = null;
    }
  }

  function stopRevisionPolling() {
    clearRevisionPollTimer();
    revisionPollingEnabled = false;
    lastMessageSetRevision = null;
    pollFailureSnapshot = null;
    if (revisionPollController) revisionPollController.abort();
  }

  function pauseRevisionPolling() {
    clearRevisionPollTimer();
    if (revisionPollController) revisionPollController.abort();
  }

  function scheduleRevisionPoll(delay = REVISION_POLL_INTERVAL_MS) {
    clearRevisionPollTimer();
    if (!revisionPollingEnabled || !webToken || !currentConversation || !isPageVisible()) return;
    revisionPollTimer = window.setTimeout(() => {
      revisionPollTimer = null;
      void pollMessageSetRevision();
    }, delay);
  }

  function reportRevisionPollFailure(message, state = 'error') {
    if (!pollFailureSnapshot) {
      pollFailureSnapshot = { message: apiStatus.textContent, state: apiStatus.dataset.state || 'idle' };
    }
    setApiStatus(message, state, true);
  }

  function restoreStatusAfterRevisionPoll() {
    if (!pollFailureSnapshot) return;
    const previous = pollFailureSnapshot;
    pollFailureSnapshot = null;
    setApiStatus(previous.message, previous.state, true);
  }

  function setPill(element, label, kind = 'muted') {
    element.replaceChildren();
    const extraClasses = [...element.classList].filter((name) => name !== 'pill' && !name.startsWith('pill-'));
    element.className = [...extraClasses, 'pill', `pill-${kind}`].join(' ');
    if (kind === 'pending' || kind === 'ok' || kind === 'error') {
      const dot = document.createElement('span');
      dot.className = 'pill-dot';
      dot.setAttribute('aria-hidden', 'true');
      element.append(dot);
    }
    element.append(document.createTextNode(label));
  }

  function setMessageState(title, detail, state = 'idle') {
    messageArea.classList.remove('has-messages');
    const empty = document.createElement('div');
    empty.className = `empty-state${state === 'idle' ? '' : ` is-${state}`}`;
    const illustration = document.createElement('div');
    illustration.className = 'empty-illustration';
    illustration.setAttribute('aria-hidden', 'true');
    illustration.innerHTML = '<span></span><span></span><span></span>';
    const heading = document.createElement('h3');
    heading.textContent = title;
    const paragraph = document.createElement('p');
    paragraph.textContent = detail;
    empty.append(illustration, heading, paragraph);
    messageArea.replaceChildren(empty);
  }

  function setIdentityUnavailable(title, detail, state = 'idle') {
    activeScope = null;
    currentPerspectiveResponse = null;
    previewPair = null;
    perspectiveSummary.textContent = title;
    perspectiveStatus.textContent = title;
    identityHelp.textContent = detail;
    selfIdentity.textContent = '暂不可用';
    contactIdentity.textContent = '暂不可用';
    previewState.textContent = detail;
    confirmHelp.textContent = detail;
    confirmCurrentButton.hidden = true;
    confirmCurrentButton.disabled = true;
    swapButton.disabled = true;
    setPill(perspectiveStatus, title, state === 'error' ? 'error' : 'muted');
  }

  function resetWorkspaceForNoToken() {
    stopRevisionPolling();
    if (systemStatusController) systemStatusController.abort();
    systemStatusController = null;
    systemStatusRequestVersion++;
    if (olderPageController) olderPageController.abort();
    olderPageController = null;
    olderPageInProgress = false;
    resetAgentForNoToken();
    currentConversation = null;
    selectedAccountId = null;
    selectedConversationId = null;
    directoryAccounts = [];
    directoryConversations = [];
    readerTargetVersion = 0;
    pendingReaderTargetIds = null;
    pendingReaderTargetAccountId = null;
    activeDiscoveryJobId = null;
    if (discoveryPollTimer !== null) window.clearTimeout(discoveryPollTimer);
    discoveryPollTimer = null;
    currentMessageSnapshot = null;
    currentSessionValue.textContent = '等待授权读取当前会话';
    chatSessionValue.textContent = '等待授权读取当前会话';
    chatSessionType.textContent = '选择会话后读取其已授权消息';
    captureStateValue.textContent = '等待授权读取';
    memoryCurrentSession.textContent = '等待授权读取';
    minimalCurrentSession.textContent = '等待授权读取';
    setPill(currentSessionStatus, '等待授权', 'pending');
    setPill(chatCaptureStatus, '等待授权', 'pending');
    setPill(messageStatusPill, '等待授权', 'muted');
    setMessageState('尚未读取聊天记录', '请先在“接入与会话”完成授权读取。当前页面没有预置或模拟聊天内容。');
    setIdentityUnavailable('等待授权', '授权读取后展示当前会话的身份范围。点击“互转身份”并确认摘要后，服务端才会提交校准。');
    resetMemoryForNoToken();
    renderDirectorySelectors();
    setDiscoveryStatus('等待 Web 授权');
    setReaderTargetFeedback('');
  }

  function setMemoryFeedback(message, state = 'idle') {
    memoryFeedback.textContent = message;
    memoryFeedback.dataset.state = state;
  }

  function setMemoryActionsEnabled(enabled) {
    memoryWritable = enabled;
    memoryCreateFields.disabled = !enabled || memoryWritePending;
    reloadMemoriesButton.disabled = !enabled || memoryWritePending;
    memoryList.querySelectorAll('button').forEach((button) => {
      button.disabled = !enabled || memoryWritePending;
    });
  }

  function setMemoryLoading() {
    memoryReadVersion++;
    if (memoryReadController) memoryReadController.abort();
    memoryReadController = null;
    memoryRecords = [];
    editingMemoryId = null;
    memoryWritable = false;
    memoryCreateFields.disabled = true;
    reloadMemoriesButton.disabled = true;
    memoryList.replaceChildren();
    memoryItemsState.hidden = false;
    memoryItemsState.textContent = '正在读取当前会话记忆…';
    memoryCount.textContent = '读取中';
    setPill(memoryStatus, '读取中', 'pending');
    setMemoryFeedback('正在读取服务端限定范围内的记忆。', 'loading');
  }

  function resetMemoryForNoToken() {
    memoryReadVersion++;
    if (memoryReadController) memoryReadController.abort();
    if (memoryWriteController) memoryWriteController.abort();
    memoryReadController = null;
    memoryWriteController = null;
    memoryWritePending = false;
    memoryRecords = [];
    editingMemoryId = null;
    memoryWritable = false;
    memoryCreateForm.reset();
    memoryCreateFields.disabled = true;
    reloadMemoriesButton.disabled = true;
    memoryList.replaceChildren();
    memoryItemsState.hidden = false;
    memoryItemsState.textContent = '等待授权读取。';
    memoryCount.textContent = '尚未读取';
    setPill(memoryStatus, '等待授权', 'muted');
    setMemoryFeedback('请先在“接入与会话”完成授权读取。');
  }

  function setMemoryUnavailable(title, detail, state = 'error') {
    memoryRecords = [];
    editingMemoryId = null;
    memoryWritable = false;
    memoryCreateFields.disabled = true;
    reloadMemoriesButton.disabled = !webToken || !currentConversation;
    memoryList.replaceChildren();
    memoryItemsState.hidden = false;
    memoryItemsState.textContent = detail;
    memoryCount.textContent = '不可用';
    setPill(memoryStatus, title, state === 'error' ? 'error' : 'muted');
    setMemoryFeedback(detail, state);
  }

  function appendMemoryField(form, labelText, id, control, hint = '') {
    const label = document.createElement('label');
    label.className = 'memory-field-label';
    label.htmlFor = id;
    label.append(document.createTextNode(labelText));
    if (hint) {
      const note = document.createElement('span');
      note.textContent = hint;
      label.append(note);
    }
    control.id = id;
    control.classList.add('memory-field');
    form.append(label, control);
  }

  function createMemoryEditForm(item) {
    const form = document.createElement('form');
    form.className = 'memory-edit-form';
    form.dataset.memoryId = item.memoryId;

    const text = document.createElement('textarea');
    text.classList.add('memory-textarea');
    text.dataset.memoryField = 'text';
    text.maxLength = 8000;
    text.required = true;
    text.value = item.text || '';
    appendMemoryField(form, '记忆内容', `memory-edit-text-${item.memoryId}`, text, '最多 4,000 字符');

    const row = document.createElement('div');
    row.className = 'memory-field-row';
    const categoryBox = document.createElement('div');
    const category = document.createElement('input');
    category.type = 'text';
    category.dataset.memoryField = 'category';
    category.maxLength = 200;
    category.value = item.category || '';
    appendMemoryField(categoryBox, '分类', `memory-edit-category-${item.memoryId}`, category, '可选');
    const observedBox = document.createElement('div');
    const observed = document.createElement('input');
    observed.type = 'datetime-local';
    observed.dataset.memoryField = 'observedAt';
    observed.value = toDateTimeLocal(item.observedAt);
    appendMemoryField(observedBox, '相关时间', `memory-edit-observed-${item.memoryId}`, observed, '可选');
    row.append(categoryBox, observedBox);
    form.append(row);

    const evidenceIds = Array.isArray(item.sourceMessageIds) ? item.sourceMessageIds : [];
    const evidenceNote = document.createElement('p');
    evidenceNote.className = 'memory-edit-evidence';
    evidenceNote.textContent = evidenceIds.length
      ? `保存时会原样保留现有 ${evidenceIds.length} 条消息证据；本次编辑不更改证据关联。`
      : '当前没有关联聊天证据；保存后仍会标为用户备注。';
    form.append(evidenceNote);

    const actions = document.createElement('div');
    actions.className = 'memory-edit-actions';
    const cancel = document.createElement('button');
    cancel.type = 'button';
    cancel.className = 'button button-secondary';
    cancel.dataset.memoryAction = 'cancel';
    cancel.textContent = '取消';
    const save = document.createElement('button');
    save.type = 'submit';
    save.className = 'button button-soft';
    save.textContent = '保存更改';
    actions.append(cancel, save);
    form.append(actions);
    return form;
  }

  function memoryOriginLabel(item) {
    const origin = String(item.origin || '').toLowerCase();
    const evidenceCount = Array.isArray(item.sourceMessageIds) ? item.sourceMessageIds.length : 0;
    if (origin === 'userentered') {
      return evidenceCount === 0 ? ['用户备注 · 无聊天证据', 'is-note'] : ['用户录入 · 有关联证据', 'is-entered'];
    }
    if (origin === 'messageextracted') return ['消息提取 · 只读', 'is-extracted'];
    return ['来源未知 · 只读', 'is-extracted'];
  }

  function renderMemoryItem(item) {
    const article = document.createElement('article');
    article.className = 'memory-item';
    article.dataset.memoryId = item.memoryId || '';

    const header = document.createElement('div');
    header.className = 'memory-item-header';
    const heading = document.createElement('div');
    heading.className = 'memory-item-heading';
    const labels = document.createElement('div');
    labels.className = 'memory-item-labels';
    const [originText, originClass] = memoryOriginLabel(item);
    const origin = document.createElement('span');
    origin.className = `memory-origin ${originClass}`;
    origin.textContent = originText;
    const subject = document.createElement('span');
    subject.className = 'memory-subject';
    const subjectType = String(item.subjectType || '').toLowerCase();
    subject.textContent = subjectType === 'wechataccount' ? '本人账号' : subjectType === 'contact' ? '联系人' : '对象未知';
    labels.append(origin, subject);
    const version = document.createElement('span');
    version.className = 'memory-item-version';
    version.textContent = Number.isFinite(Number(item.version)) ? `v${item.version}` : '版本未知';
    heading.append(labels);
    header.append(heading, version);
    article.append(header);

    const editable = String(item.origin || '').toLowerCase() === 'userentered'
      && String(item.status || '').toLowerCase() === 'active'
      && typeof item.memoryId === 'string'
      && Number(item.version) > 0;

    if (editingMemoryId === item.memoryId && editable) {
      article.append(createMemoryEditForm(item));
      return article;
    }

    const content = document.createElement('p');
    content.className = 'memory-item-text';
    content.textContent = typeof item.text === 'string' ? item.text : '';
    article.append(content);

    const meta = document.createElement('div');
    meta.className = 'memory-item-meta';
    if (item.category) {
      const category = document.createElement('span');
      category.textContent = `分类：${item.category}`;
      meta.append(category);
    }
    if (item.observedAt) {
      const observed = document.createElement('span');
      observed.textContent = `相关时间：${formatTime(item.observedAt)}`;
      meta.append(observed);
    }
    const updated = document.createElement('span');
    updated.textContent = `更新于 ${formatTime(item.updatedAt)}`;
    meta.append(updated);
    article.append(meta);

    const evidenceIds = Array.isArray(item.sourceMessageIds) ? item.sourceMessageIds : [];
    if (evidenceIds.length > 0) {
      const evidence = document.createElement('div');
      evidence.className = 'memory-item-evidence';
      evidence.textContent = `关联消息 ID（${evidenceIds.length}）：${evidenceIds.join(' · ')}`;
      article.append(evidence);
    } else if (String(item.origin || '').toLowerCase() === 'userentered') {
      const note = document.createElement('div');
      note.className = 'memory-item-evidence';
      note.textContent = '这是人工用户备注，没有关联聊天证据。';
      article.append(note);
    }

    const actions = document.createElement('div');
    actions.className = 'memory-item-actions';
    if (editable) {
      const edit = document.createElement('button');
      edit.type = 'button';
      edit.className = 'button button-secondary';
      edit.dataset.memoryAction = 'edit';
      edit.dataset.memoryId = item.memoryId;
      edit.textContent = '编辑';
      const remove = document.createElement('button');
      remove.type = 'button';
      remove.className = 'button button-secondary danger';
      remove.dataset.memoryAction = 'delete';
      remove.dataset.memoryId = item.memoryId;
      remove.textContent = '移除';
      actions.append(edit, remove);
      article.append(actions);
    } else {
      const readonly = document.createElement('span');
      readonly.className = 'memory-readonly-note';
      readonly.textContent = '只读条目';
      article.append(readonly);
    }
    return article;
  }

  function renderMemoryItems(items, conversation, notice = null) {
    if (!Array.isArray(items)) {
      setMemoryUnavailable('响应无效', '记忆接口返回的内容格式无效。', 'error');
      return false;
    }
    const invalidScope = items.some((item) => !item
      || String(item.accountId || '').toLowerCase() !== String(conversation.accountId || '').toLowerCase()
      || (item.conversationId && String(item.conversationId).toLowerCase() !== String(conversation.conversationId || '').toLowerCase()));
    if (invalidScope) {
      setMemoryUnavailable('范围不匹配', '记忆接口返回了不属于当前服务端会话范围的条目，已全部隐藏。', 'error');
      return false;
    }

    memoryRecords = items;
    editingMemoryId = null;
    memoryList.replaceChildren(...items.map(renderMemoryItem));
    memoryItemsState.hidden = items.length > 0;
    memoryItemsState.textContent = items.length ? '' : '当前服务端账号与会话范围内没有已保存记忆。';
    memoryCount.textContent = `${items.length} 条`;
    setPill(memoryStatus, `${items.length} 条`, items.length ? 'ok' : 'muted');
    setMemoryActionsEnabled(Boolean(webToken && currentConversation));
    setMemoryFeedback(notice || '人工录入且没有关联聊天证据的内容会标为“用户备注”；消息提取内容只读。', 'success');
    return true;
  }

  function toDateTimeLocal(value) {
    if (!value) return '';
    const date = new Date(value);
    if (Number.isNaN(date.getTime())) return '';
    return new Date(date.getTime() - date.getTimezoneOffset() * 60000).toISOString().slice(0, 16);
  }

  function dateTimePayload(value) {
    if (!value) return null;
    const date = new Date(value);
    return Number.isNaN(date.getTime()) ? null : date.toISOString();
  }

  function validateMemoryFields(text, category, observedAt) {
    if (!text.trim()) return '请填写记忆内容。';
    if (Array.from(text).length > 4000) return '记忆内容不能超过 4,000 个字符。';
    if (category && Array.from(category).length > 100) return '分类不能超过 100 个字符。';
    if (observedAt && !dateTimePayload(observedAt)) return '相关时间格式无效。';
    return null;
  }

  async function loadMemoryList(notice = null) {
    if (!webToken || !currentConversation) return;
    setMemoryLoading();
    const version = memoryReadVersion;
    const controller = new AbortController();
    memoryReadController = controller;
    const result = await apiRequest('/api/conversations/current/memory?limit=100', { signal: controller.signal });
    if (version !== memoryReadVersion || result.aborted) return;
    memoryReadController = null;
    if (isUnauthorized(result)) {
      handleUnauthorized();
      return;
    }
    if (!result.ok) {
      const issue = formatRequestError(result);
      setMemoryUnavailable(issue.title, issue.detail, issue.state);
      return;
    }
    renderMemoryItems(result.data, currentConversation, notice);
  }

  async function submitMemoryMutation(path, options, successMessage) {
    if (!webToken || !currentConversation || !memoryWritable || memoryWritePending) return;
    memoryWritePending = true;
    const controller = new AbortController();
    memoryWriteController = controller;
    memoryCreateFields.disabled = true;
    reloadMemoriesButton.disabled = true;
    memoryList.querySelectorAll('button').forEach((button) => { button.disabled = true; });
    connectButton.disabled = true;
    connectButton.textContent = '记忆保存中…';
    setPill(memoryStatus, '保存中', 'pending');
    setMemoryFeedback('正在保存记忆…', 'loading');

    const result = await apiRequest(path, { ...options, signal: controller.signal });
    memoryWriteController = null;
    memoryWritePending = false;
    if (result.aborted) return;
    if (isUnauthorized(result)) {
      handleUnauthorized();
      return;
    }
    if (!result.ok) {
      const issue = formatRequestError(result);
      if (issue.code === 'MEMORY_VERSION_CONFLICT' || issue.code === 'MEMORY_NOT_FOUND') {
        setMemoryFeedback(issue.title, 'error');
        await loadMemoryList();
        setMemoryFeedback(`${issue.title}列表已刷新，请核对后再编辑。`, 'error');
        finishLoading();
        return;
      }
      memoryCreateFields.disabled = !memoryWritable || !webToken || !currentConversation;
      reloadMemoriesButton.disabled = !webToken || !currentConversation;
      memoryList.querySelectorAll('button').forEach((button) => {
        button.disabled = !memoryWritable || !webToken || !currentConversation;
      });
      setPill(memoryStatus, '保存失败', 'error');
      setMemoryFeedback(issue.message, 'error');
      finishLoading();
      return;
    }

    if (path === '/api/conversations/current/memory') memoryCreateForm.reset();
    editingMemoryId = null;
    await loadMemoryList(successMessage);
    finishLoading();
  }

  function setLoadingState() {
    setPill(currentSessionStatus, '读取中', 'pending');
    currentSessionValue.textContent = '正在读取服务端授权会话';
    chatSessionValue.textContent = '正在读取服务端授权会话';
    chatSessionType.textContent = '等待会话和采集状态';
    renderDirectorySelectors();
    captureStateValue.textContent = '正在读取';
    memoryCurrentSession.textContent = '正在读取';
    minimalCurrentSession.textContent = '正在读取';
    setPill(chatCaptureStatus, '读取中', 'pending');
    setPill(messageStatusPill, '读取中', 'pending');
    messageArea.setAttribute('aria-busy', 'true');
    setMessageState('正在读取聊天记录', '聊天内容只会在当前会话接口成功返回后显示。', 'loading');
    setIdentityUnavailable('读取中', '正在读取身份范围，不会在页面自行推断发送者方向。', 'loading');
    setMemoryLoading();
    setApiStatus('正在通过授权接口读取当前会话、消息和身份范围。', 'loading');
    connectButton.disabled = true;
    connectButton.textContent = '读取中…';
  }

  function finishLoading() {
    messageArea.removeAttribute('aria-busy');
    connectButton.disabled = false;
    connectButton.textContent = webToken ? '重新读取' : '连接并读取';
    activeController = null;
  }

  function readApiError(result) {
    const body = result.data;
    const detail = body && (body.error || body.Error);
    const code = detail && (detail.code || detail.Code);
    const serverMessage = detail && (detail.message || detail.Message);
    const retryable = detail && (detail.retryable ?? detail.Retryable);
    const status = result.status;
    const known = {
      AUTH_NOT_CONFIGURED: ['服务端未配置 Web Bearer token。', 'error'],
      UNAUTHENTICATED: ['Web token 未授权或已失效，请重新输入。', 'unauthorized'],
      WEB_SCOPE_NOT_CONFIGURED: ['服务端尚未配置 Web 账号和会话范围。', 'error'],
      DATABASE_NOT_CONFIGURED: ['正式数据库尚未配置，当前会话数据不可读取。', 'error'],
      DATABASE_UNAVAILABLE: ['正式数据库暂不可用，请稍后重新读取。', 'error'],
      MESSAGE_QUERY_NOT_CONFIGURED: ['消息查询服务尚未配置。', 'error'],
      CURRENT_CONVERSATION_NOT_FOUND: ['所选账号与会话没有可读取的记录。', 'error'],
      CONVERSATION_NOT_FOUND: ['所选账号与会话没有可读取的记录。', 'error'],
      INVALID_CONVERSATION_SCOPE: ['账号或会话选择无效，请重新选择。', 'error'],
      ACCOUNT_NOT_ACTIVE_FOR_READER: ['此账号不是本机当前活动的 Reader 账号，不能修改其监听集合。', 'error'],
      READER_TARGET_VERSION_CONFLICT: ['监听目标已在其他位置更新，请重新读取后再保存。', 'error'],
      READER_TARGET_NOT_FOUND: ['监听列表中有会话已失效，请重新读取目录。', 'error'],
      READER_DISCOVERY_JOB_NOT_FOUND: ['会话目录扫描任务已失效，请重新发起。', 'error'],
      INVALID_LIMIT: ['消息读取参数无效。', 'error'],
      INVALID_ANCHOR: ['消息分页位置无效。', 'error'],
      SENDER_SCOPE_AMBIGUOUS: ['同一身份命名空间对应多个表或分片范围，无法安全确认。', 'error'],
      SENDER_SCOPE_UNAVAILABLE: ['身份来源范围不完整或不可用，无法确认。', 'error'],
      SENDER_SCOPE_CHANGED: ['身份来源范围在提交期间发生变化，请重新读取并确认。', 'error'],
      CALIBRATION_VERSION_CONFLICT: ['身份校准版本已经变化，请重新读取并检查后再确认。', 'error'],
      CALIBRATION_NOT_INITIALIZED: ['身份校准状态尚未初始化，无法提交。', 'error'],
      SENDER_SELECTION_NOT_ELIGIBLE: ['选中的发送者不再是此范围内的有效候选，请重新读取。', 'error'],
      MEMORY_VERSION_CONFLICT: ['记忆已被其他操作修改。', 'error'],
      MEMORY_NOT_FOUND: ['这条记忆已不存在或不属于当前授权会话。', 'error'],
      MEMORY_EVIDENCE_UNAVAILABLE: ['关联消息证据已不可用；未保存本次更改。', 'error'],
      INVALID_MEMORY_REQUEST: ['记忆内容不符合接口要求，请检查必填项和长度。', 'error'],
      AGENT_PRESETS_NOT_CONFIGURED: ['服务器未配置可用的人设预设。', 'error'],
      AGENT_MODULE_NOT_CONFIGURED: ['AI 服务尚未配置。', 'error'],
      AGENT_UNAVAILABLE: ['AI 服务暂不可用。', 'error'],
      AGENT_RUNTIME_NOT_CONFIGURED: ['AI Runtime 尚未配置。', 'error'],
      M3_CONTEXT_NOT_CONFIGURED: ['AI 上下文服务尚未配置。', 'error'],
      AGENT_CONFIGURATION_NOT_CONFIGURED: ['AI 执行参数尚未配置。', 'error'],
      AGENT_RUN_AUDIT_NOT_CONFIGURED: ['AI 运行记录服务尚未配置。', 'error'],
      AGENT_RUN_NOT_FOUND: ['AI 运行记录不存在。', 'error'],
      AGENT_EXECUTION_FAILED: ['AI 任务未能完成。', 'error'],
      AGENT_RESULT_INVALID: ['AI 返回结果未通过合同校验。', 'error'],
      AGENT_TOOL_CAPABILITY_DENIED: ['当前服务端未启用此档位所需的受限历史检索能力。', 'error'],
      AGENT_HISTORY_SEARCH_FAILED: ['当前会话的有界历史检索失败；未展示未复核的建议。', 'error'],
      CONTEXT_REVISION_CHANGED: ['生成期间会话内容发生变化；本批结果已丢弃，请重新生成。', 'error'],
      CODEX_REVIEW_OUTPUT_INVALID: ['Max 复核结果未通过格式校验；未展示未复核建议。', 'error'],
      AGENT_STORAGE_UNAVAILABLE: ['AI 结果暂时无法保存或读取。', 'error'],
      INVALID_SUGGESTION_REQUEST: ['建议请求无效；请选择服务器提供的人设后重试。', 'error'],
      INVALID_CONSULTATION_REQUEST: ['咨询问题无效；请检查问题内容后重试。', 'error'],
      CONSULTATION_THREAD_NOT_FOUND: ['当前咨询线程不存在。', 'error'],
      SUGGESTION_BATCH_NOT_FOUND: ['建议批次不存在。', 'error'],
      AGENT_RESOURCE_NOT_FOUND: ['AI 资源在当前会话范围内不可用。', 'error']
    };

    if (result.networkError) {
      return { code: 'NETWORK_ERROR', title: '无法连接正式 API。', detail: '请确认服务已启动并可由当前页面访问。', state: 'error', retryable: true };
    }
    const mapped = known[code];
    if (mapped) {
      const contractCode = code === 'AGENT_RESULT_INVALID' && typeof serverMessage === 'string'
        ? serverMessage.match(/\(([A-Z][A-Z0-9_]{1,63})\)\.?$/)?.[1]
        : null;
      return {
        code,
        title: mapped[0],
        detail: contractCode ? `校验代码：${contractCode}` : mapped[0],
        state: mapped[1],
        retryable: retryable === true
      };
    }
    if (status === 401) {
      return { code: code || 'UNAUTHENTICATED', title: 'Web token 未授权或已失效，请重新输入。', detail: '请求未通过 Web Bearer token 验证。', state: 'unauthorized', retryable: false };
    }
    if (status === 403) {
      return { code: code || 'FORBIDDEN', title: '当前 Web token 无权读取此会话。', detail: '服务端拒绝了当前会话访问范围。', state: 'error', retryable: false };
    }
    return {
      code: code || `HTTP_${status || 'ERROR'}`,
      title: `API 请求失败（${code || status || '未知错误'}）。`,
      detail: retryable === true ? '服务暂时不可用，可以重新读取。' : '请检查服务端返回的错误状态。',
      state: 'error',
      retryable: retryable === true
    };
  }

  async function apiRequest(path, { method = 'GET', body = null, signal = undefined, idempotencyKey = null } = {}) {
    try {
      const headers = new Headers({
        Accept: 'application/json',
        Authorization: `Bearer ${webToken}`
      });
      if (idempotencyKey) headers.set('Idempotency-Key', idempotencyKey);
      const options = { method, headers, signal };
      if (body !== null) {
        headers.set('Content-Type', 'application/json');
        options.body = JSON.stringify(body);
      }

      const requestUrl = new URL(path, window.location.href);
      const pathOnly = requestUrl.pathname;
      const requiresConversationScope = pathOnly.startsWith('/api/conversations/current')
        || (pathOnly.startsWith('/api/ai/')
          && pathOnly !== '/api/ai/personas'
          && pathOnly !== '/api/ai/execution-profiles');
      if (requiresConversationScope && selectedAccountId && selectedConversationId) {
        requestUrl.searchParams.set('accountId', selectedAccountId);
        requestUrl.searchParams.set('conversationId', selectedConversationId);
      }
      const response = await fetch(requestUrl, options);
      let data = null;
      try { data = await response.json(); } catch { /* Keep an empty body as a normal API error state. */ }
      return { ok: response.ok, status: response.status, data };
    } catch (error) {
      if (error && error.name === 'AbortError') return { aborted: true };
      return { ok: false, networkError: true, data: null };
    }
  }

  const GUID_PATTERN = /^(?:[0-9a-f]{32}|[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})$/i;

  function normalizeStatus(value) {
    return String(value || '').replace(/[^a-z]/gi, '').toLowerCase();
  }

  function agentScopeKey(conversation) {
    if (!validConversation(conversation)) return null;
    return `${conversation.accountId.toLowerCase()}:${conversation.conversationId.toLowerCase()}`;
  }

  function consultationStorageKey(accountId, conversationId) {
    const scope = `${String(accountId || '').toLowerCase()}:${String(conversationId || '').toLowerCase()}`;
    return 'wechat-ai-copilot:consultation-thread:' + encodeURIComponent(scope);
  }

  function isValidAgentGuid(value) {
    return typeof value === 'string' && GUID_PATTERN.test(value);
  }

  function toCanonicalAgentGuid(value) {
    if (!isValidAgentGuid(value)) return null;
    const compact = value.replaceAll('-', '').toLowerCase();
    return `${compact.slice(0, 8)}-${compact.slice(8, 12)}-${compact.slice(12, 16)}-${compact.slice(16, 20)}-${compact.slice(20)}`;
  }

  function readStoredThreadId(accountId, conversationId) {
    try {
      const value = sessionStorage.getItem(consultationStorageKey(accountId, conversationId));
      return isValidAgentGuid(value) ? value : null;
    } catch {
      return null;
    }
  }

  function writeStoredThreadId(accountId, conversationId, threadId) {
    try {
      if (isValidAgentGuid(threadId)) sessionStorage.setItem(consultationStorageKey(accountId, conversationId), threadId);
    } catch { /* Session storage can be unavailable in private browsing. */ }
  }

  function clearStoredThreadId(accountId, conversationId) {
    try { sessionStorage.removeItem(consultationStorageKey(accountId, conversationId)); } catch { /* Keep the in-memory state usable. */ }
  }

  function createAgentScopeState(scopeKey, conversation) {
    return {
      scopeKey,
      accountId: conversation.accountId,
      conversationId: conversation.conversationId,
      selectedPersonaPresetId: '',
      suggestionBatch: null,
      suggestionBusy: false,
      suggestionError: '',
      suggestionFeedback: '',
      threadId: readStoredThreadId(conversation.accountId, conversation.conversationId),
      turns: [],
      historyLoaded: false,
      historyLoading: false,
      historyError: '',
      consultationBusy: false,
      consultationBlocked: false,
      consultationError: ''
    };
  }

  function abortAgentRequests() {
    if (personaCatalogController) {
      personaCatalogController.abort();
      if (personaCatalogStatus === 'loading') personaCatalogStatus = 'idle';
    }
    if (suggestionController) suggestionController.abort();
    if (consultationController) consultationController.abort();
    if (consultationHistoryController) consultationHistoryController.abort();
    personaCatalogController = null;
    suggestionController = null;
    consultationController = null;
    consultationHistoryController = null;
    if (activeAgentState) {
      activeAgentState.suggestionBusy = false;
      activeAgentState.historyLoading = false;
      activeAgentState.consultationBusy = false;
    }
  }

  function clearActiveAgentScope() {
    abortAgentRequests();
    activeAgentScopeKey = null;
    activeAgentState = null;
    renderAgentState();
  }

  function resetAgentForNoToken() {
    abortAgentRequests();
    agentStateByScope.clear();
    activeAgentScopeKey = null;
    activeAgentState = null;
    personaCatalog = [];
    personaCatalogStatus = 'idle';
    personaCatalogFeedback = '';
    executionProfileCatalog = [];
    executionProfileCatalogStatus = 'idle';
    if (executionProfileController) executionProfileController.abort();
    executionProfileController = null;
    renderExecutionProfileSelectors();
    renderAgentState();
  }

  function resetAgentForAuthChange() {
    resetAgentForNoToken();
  }

  function activateAgentScope(conversation) {
    const nextKey = agentScopeKey(conversation);
    if (!webToken || !nextKey) {
      clearActiveAgentScope();
      return;
    }

    if (activeAgentScopeKey !== nextKey) {
      abortAgentRequests();
      activeAgentScopeKey = nextKey;
      activeAgentState = agentStateByScope.get(nextKey) || createAgentScopeState(nextKey, conversation);
      agentStateByScope.set(nextKey, activeAgentState);
    }

    renderAgentState();
    if (personaCatalogStatus === 'idle' || personaCatalogStatus === 'error' || personaCatalogStatus === 'unavailable') {
      void loadPersonaCatalog();
    }
    if (executionProfileCatalogStatus === 'idle' || executionProfileCatalogStatus === 'error') {
      void loadExecutionProfileCatalog();
    }
    if (activeAgentState.threadId && !activeAgentState.historyLoaded && !activeAgentState.historyLoading) {
      void restoreConsultationHistory(activeAgentState);
    }
  }

  function setSuggestionState(title, detail, state = 'idle') {
    suggestionStateTitle.textContent = title;
    suggestionStateDetail.textContent = detail;
    suggestionState.dataset.state = state;
    minimalSuggestionState.textContent = detail;
  }

  function setSuggestionFeedback(message, state = 'idle') {
    suggestionFeedback.textContent = message;
    suggestionFeedback.dataset.state = state;
    const help = $('personaHelp');
    if (help) help.textContent = message || '人设列表来自服务端配置';
  }

  function setMinimalStatus(message) {
    const dot = document.createElement('span');
    dot.className = 'status-dot';
    dot.setAttribute('aria-hidden', 'true');
    minimalStatus.replaceChildren(dot, document.createTextNode(message));
  }

  function renderSuggestionButtons(container, batch, minimal = false, allowCopy = true) {
    container.replaceChildren();
    const suggestions = Array.isArray(batch?.suggestions) ? batch.suggestions : [];
    suggestions.forEach((suggestion, index) => {
      const button = document.createElement('button');
      button.type = 'button';
      button.className = minimal ? 'minimal-choice' : 'work-choice';
      button.dataset.copySuggestionIndex = String(index);
      button.disabled = !allowCopy;

      const number = document.createElement('span');
      number.className = 'choice-number';
      number.textContent = `0${index + 1}`;
      const text = document.createElement('span');
      text.className = minimal ? 'choice-placeholder' : 'work-choice-text';
      text.textContent = suggestion.text;
      const copy = document.createElement('span');
      copy.className = 'work-choice-copy';
      copy.textContent = allowCopy ? '复制' : '演示只读';
      button.append(number, text, copy);
      container.append(button);
    });
  }

  function renderPersonaSelect() {
    personaSelect.replaceChildren();
    if (personaCatalogStatus !== 'ready' || personaCatalog.length === 0) {
      const option = document.createElement('option');
      option.value = '';
      option.textContent = personaCatalogStatus === 'loading'
        ? '正在读取人设目录…'
        : personaCatalogStatus === 'error' || personaCatalogStatus === 'unavailable'
          ? '人设目录不可用'
          : '等待服务器人设目录';
      personaSelect.append(option);
      personaSelect.value = '';
      return;
    }

    personaCatalog.forEach((persona) => {
      const option = document.createElement('option');
      option.value = persona.personaPresetId;
      option.textContent = persona.label;
      personaSelect.append(option);
    });
    const selected = personaCatalog.some((item) => item.personaPresetId === activeAgentState?.selectedPersonaPresetId)
      ? activeAgentState.selectedPersonaPresetId
      : personaCatalog[0].personaPresetId;
    personaSelect.value = selected;
    if (activeAgentState) activeAgentState.selectedPersonaPresetId = selected;
  }

  function renderConsultationTurns(state) {
    consultationTurns.replaceChildren();
    if (!state) {
      const empty = document.createElement('div');
      empty.className = 'consultation-empty';
      empty.textContent = '等待授权读取当前会话。';
      consultationTurns.append(empty);
      return;
    }

    if (state.historyLoading) {
      const loading = document.createElement('div');
      loading.className = 'consultation-empty';
      loading.textContent = '正在从服务端恢复当前会话的咨询历史…';
      consultationTurns.append(loading);
    } else if (!state.turns.length) {
      const empty = document.createElement('div');
      empty.className = 'consultation-empty';
      empty.textContent = state.threadId
        ? '此咨询线程目前没有已完成的对话。'
        : '尚无咨询记录。提交问题后会创建当前会话专属的咨询线程。';
      consultationTurns.append(empty);
    } else {
      state.turns.forEach((turn) => {
        const article = document.createElement('article');
        const isDemoRuntime = turn.isDemoRuntime === true || /fake/i.test(String(turn.runtimeName || ''));
        article.className = `consultation-turn${isDemoRuntime ? ' is-demo-runtime' : ''}`;
        const questionLabel = document.createElement('span');
        questionLabel.className = 'consultation-speaker';
        questionLabel.textContent = '你';
        const question = document.createElement('p');
        question.className = 'consultation-user-text';
        question.textContent = turn.userInput;
        const answerLabel = document.createElement('span');
        answerLabel.className = 'consultation-speaker is-ai';
        const executionProfile = executionProfileCatalog.find((item) => item.executionProfileId === turn.executionProfileId);
        answerLabel.textContent = isDemoRuntime
          ? 'FakeRuntime 固定演示 · 非真实 AI 回答'
          : `${executionProfile ? `${executionProfile.label} · ` : ''}${turn.runtimeName ? `AI 咨询 · ${turn.runtimeName}` : 'AI 咨询'}`;
        const answer = document.createElement('p');
        answer.className = 'consultation-answer-text';
        answer.textContent = turn.answer;
        article.append(questionLabel, question, answerLabel, answer);
        if (Array.isArray(turn.evidenceMessageIds) && turn.evidenceMessageIds.length) {
          const evidence = document.createElement('small');
          evidence.className = 'consultation-evidence';
          evidence.textContent = `关联 ${turn.evidenceMessageIds.length} 条消息证据`;
          article.append(evidence);
        }
        consultationTurns.append(article);
      });
    }
    consultationTurns.scrollTop = consultationTurns.scrollHeight;
  }

  function renderAgentState() {
    const authorized = Boolean(webToken && currentConversation && activeAgentState);
    renderPersonaSelect();
    renderExecutionProfileSelectors();
    const catalogReady = personaCatalogStatus === 'ready' && personaCatalog.length > 0;
    const profileCatalogReady = executionProfileCatalogStatus === 'ready' && executionProfileCatalog.length > 0;
    const selectedPersona = personaCatalog.find((item) => item.personaPresetId === activeAgentState?.selectedPersonaPresetId);
    const selectedProfileId = replyProfileSelect.value || 'Quick';
    const batch = activeAgentState?.suggestionBatch || null;
    const batchMatchesPersona = Boolean(batch && selectedPersona && batch.personaPresetId === selectedPersona.personaPresetId
      && (batch.executionProfileId || 'Balanced') === selectedProfileId);

    personaSelect.disabled = !authorized || !catalogReady || activeAgentState?.suggestionBusy === true;
    replyProfileSelect.disabled = !authorized || !profileCatalogReady || activeAgentState?.suggestionBusy === true;
    generateSuggestionsButton.disabled = !authorized || !catalogReady || !profileCatalogReady || !selectedPersona || activeAgentState?.suggestionBusy === true;
    cancelSuggestionButton.hidden = activeAgentState?.suggestionBusy !== true;
    cancelSuggestionButton.disabled = activeAgentState?.suggestionBusy !== true;
    generateSuggestionsButton.textContent = activeAgentState?.suggestionBusy
      ? '正在生成…'
      : batchMatchesPersona ? '换一批建议' : '生成三条建议';
    reloadPersonasButton.hidden = !['error', 'unavailable'].includes(personaCatalogStatus);
    reloadPersonasButton.disabled = !authorized || personaCatalogStatus === 'loading';

    if (personaCatalogStatus === 'loading') {
      setSuggestionState('正在读取人设目录', '仅使用服务端返回的人设预设；目录返回前不会请求生成。', 'loading');
      setPill(suggestionStatus, '读取中', 'pending');
    } else if (!authorized) {
      setSuggestionState('等待授权', '完成授权并读取当前会话后，才会加载服务器人设目录。', 'idle');
      setPill(suggestionStatus, '等待授权', 'muted');
    } else if (personaCatalogStatus === 'error' || personaCatalogStatus === 'unavailable') {
      setSuggestionState('人设目录不可用', personaCatalogFeedback || '服务器尚未提供可用人设；建议生成已禁用。', 'error');
      setPill(suggestionStatus, '不可用', 'error');
    } else if (!profileCatalogReady) {
      setSuggestionState(
        executionProfileCatalogStatus === 'error' ? '档位目录不可用' : '读取 AI 档位',
        executionProfileCatalogStatus === 'error' ? '服务器未返回受控执行档位；建议生成已禁用。' : '正在读取服务器执行档位目录…',
        executionProfileCatalogStatus === 'error' ? 'error' : 'loading');
      setPill(suggestionStatus, executionProfileCatalogStatus === 'error' ? '不可用' : '读取中', executionProfileCatalogStatus === 'error' ? 'error' : 'pending');
    } else if (!catalogReady) {
      setSuggestionState('等待服务器人设目录', '当前没有可用人设配置，不会提交建议请求。', 'idle');
      setPill(suggestionStatus, '未配置', 'muted');
    } else if (activeAgentState?.suggestionBusy && !batchMatchesPersona) {
      setSuggestionState('正在请求回复建议', '正在请求当前授权会话的三条建议。', 'loading');
      setPill(suggestionStatus, '生成中', 'pending');
    } else if (!batchMatchesPersona) {
      const detail = activeAgentState?.suggestionError
        || (batch ? '当前人设或执行档位与上一批不同，请生成新的建议。' : '服务端尚无本会话建议批次。点击“生成三条建议”后才会请求 AI。');
      setSuggestionState(activeAgentState?.suggestionError ? '建议暂不可用' : '尚无当前人设建议', detail, activeAgentState?.suggestionError ? 'error' : 'idle');
      setPill(suggestionStatus, activeAgentState?.suggestionError ? '请求失败' : '待生成', activeAgentState?.suggestionError ? 'error' : 'muted');
    } else {
      suggestionState.hidden = true;
      setPill(suggestionStatus, '服务端建议', 'ok');
    }

    const visibleBatch = batchMatchesPersona ? batch : null;
    const demoBatch = Boolean(visibleBatch && activeAgentState?.suggestionIsDemoRuntime);
    if (demoBatch) {
      setSuggestionState('FakeRuntime 固定演示输出', '以下内容来自 FakeRuntime，仅供接口演示，不代表真实 AI 建议或理解。', 'demo');
      setPill(suggestionStatus, '演示输出', 'muted');
    }
    suggestionState.hidden = Boolean(visibleBatch) && !demoBatch;
    renderSuggestionButtons(suggestionList, visibleBatch, false, !demoBatch);
    renderSuggestionButtons(minimalChoices, visibleBatch, true, !demoBatch);
    if (demoBatch) {
      setSuggestionFeedback('FakeRuntime 固定演示输出，不是真实 AI 建议。', 'demo');
    } else if (activeAgentState?.suggestionFeedback) {
      setSuggestionFeedback(activeAgentState.suggestionFeedback, 'success');
    } else {
      setSuggestionFeedback(activeAgentState?.suggestionError || '', activeAgentState?.suggestionError ? 'error' : 'idle');
    }

    minimalPersonaButton.disabled = !authorized || !catalogReady || !profileCatalogReady || activeAgentState?.suggestionBusy === true;
    minimalRefreshButton.disabled = !authorized || !catalogReady || !profileCatalogReady || !selectedPersona || activeAgentState?.suggestionBusy === true;
    minimalPersonaLabel.textContent = selectedPersona ? `人设 · ${selectedPersona.label}` : '人设 · 等待服务';
    setMinimalStatus(!authorized
      ? '等待授权'
      : personaCatalogStatus === 'loading' ? '正在读取服务器人设'
        : catalogReady ? selectedPersona?.label || '服务已连接' : '人设服务不可用');
    if (demoBatch) {
      minimalSuggestionState.hidden = false;
      minimalSuggestionState.textContent = 'FakeRuntime 固定演示输出 · 非真实 AI 建议';
    } else if (!visibleBatch) {
      minimalSuggestionState.hidden = false;
      minimalSuggestionState.textContent = activeAgentState?.suggestionError
        || (personaCatalogStatus === 'error' || personaCatalogStatus === 'unavailable'
          ? personaCatalogFeedback
          : !authorized ? '请先在“接入与会话”完成授权并读取当前会话。'
            : executionProfileCatalogStatus === 'error' ? '服务器 AI 档位目录不可用。'
              : executionProfileCatalogStatus === 'loading' ? '正在读取服务器 AI 档位目录…'
                : personaCatalogStatus === 'loading' ? '正在读取服务端人设目录…'
              : activeAgentState?.suggestionBusy ? '正在向服务端请求三条建议…'
                : batch ? '当前人设尚无建议；请生成一批新的服务端建议。'
                  : '当前会话暂无服务端建议。');
    } else {
      minimalSuggestionState.hidden = true;
    }

    renderConsultation(stateForActiveScope());
  }

  function stateForActiveScope() {
    return webToken && currentConversation && activeAgentState ? activeAgentState : null;
  }

  function renderConsultation(state) {
    const authorized = Boolean(webToken && currentConversation && state);
    const busy = state?.consultationBusy === true || state?.historyLoading === true;
    const profileReady = executionProfileCatalogStatus === 'ready' && executionProfileCatalog.length > 0;
    consultationProfileSelect.disabled = !authorized || !profileReady || busy || state?.consultationBlocked === true;
    consultationQuestion.disabled = !authorized || busy || state?.consultationBlocked === true;
    consultationSubmit.disabled = !authorized || !profileReady || busy || state?.consultationBlocked === true;
    cancelConsultationButton.hidden = state?.consultationBusy !== true;
    cancelConsultationButton.disabled = state?.consultationBusy !== true;
    consultationQuestion.placeholder = authorized
      ? '输入希望咨询的问题；回答由正式 AI 服务返回'
      : '请先授权并读取当前会话';
    consultationHint.textContent = '仅提交给 AI 咨询，不会发送微信消息';
    if (!authorized) {
      setPill(consultationStatus, '等待授权', 'muted');
      consultationNotice.textContent = '请先在“接入与会话”完成授权读取。';
      consultationNotice.dataset.state = 'idle';
      renderConsultationTurns(null);
      return;
    }
    if (!profileReady) {
      setPill(consultationStatus, 'AI 档位不可用', 'error');
      consultationNotice.textContent = '服务器执行档位目录尚未就绪；暂不能提交咨询。';
      consultationNotice.dataset.state = 'error';
      renderConsultationTurns(state);
      return;
    }
    if (state.historyLoading) setPill(consultationStatus, '恢复历史', 'pending');
    else if (busy) setPill(consultationStatus, '处理中', 'pending');
    else if (state.consultationError || state.historyError || state.consultationBlocked) setPill(consultationStatus, '暂不可用', 'error');
    else setPill(consultationStatus, state.threadId ? '会话线程已绑定' : '等待提问', state.threadId ? 'ok' : 'muted');

    const message = state.consultationError || state.historyError
      || (state.consultationBlocked ? '已提交的问题尚未完成运行来源校验，回答未展示。请切换离开再返回以重试恢复，勿重复提交同一问题。' : '')
      || (state.threadId ? '该咨询线程绑定当前授权会话；历史仅从服务端恢复。' : '提交后会创建绑定当前会话的咨询线程。');
    consultationNotice.textContent = message;
    consultationNotice.dataset.state = state.consultationError || state.historyError || state.consultationBlocked ? 'error' : 'idle';
    renderConsultationTurns(state);
  }

  async function loadPersonaCatalog(force = false) {
    if (!webToken || !currentConversation || !activeAgentState) return;
    if (personaCatalogStatus === 'loading') return;
    if (personaCatalogStatus === 'ready' && !force) return;
    if (personaCatalogController) personaCatalogController.abort();
    const controller = new AbortController();
    personaCatalogController = controller;
    const version = ++personaCatalogVersion;
    const tokenAtStart = webToken;
    const scopeAtStart = activeAgentScopeKey;
    personaCatalogStatus = 'loading';
    personaCatalogFeedback = '';
    renderAgentState();

    const result = await apiRequest('/api/ai/personas', { signal: controller.signal });
    if (result.aborted || version !== personaCatalogVersion || tokenAtStart !== webToken || scopeAtStart !== activeAgentScopeKey) return;
    if (personaCatalogController === controller) personaCatalogController = null;
    if (isUnauthorized(result)) {
      handleUnauthorized();
      return;
    }
    if (!result.ok) {
      const issue = formatRequestError(result);
      personaCatalog = [];
      personaCatalogStatus = issue.code === 'AGENT_PRESETS_NOT_CONFIGURED' ? 'unavailable' : 'error';
      personaCatalogFeedback = issue.message;
      renderAgentState();
      return;
    }

    const received = Array.isArray(result.data?.personas) ? result.data.personas : [];
    const unique = new Set();
    personaCatalog = received.filter((item) => {
      if (!item || typeof item.personaPresetId !== 'string' || !item.personaPresetId.trim()
        || typeof item.label !== 'string' || !item.label.trim() || unique.has(item.personaPresetId)) return false;
      unique.add(item.personaPresetId);
      return true;
    }).map((item) => ({ personaPresetId: item.personaPresetId, label: item.label.trim() }));
    if (!personaCatalog.length) {
      personaCatalogStatus = 'unavailable';
      personaCatalogFeedback = '服务器人设目录为空或响应格式无效；生成控件已禁用。';
    } else {
      personaCatalogStatus = 'ready';
      personaCatalogFeedback = '';
      if (activeAgentState && !personaCatalog.some((item) => item.personaPresetId === activeAgentState.selectedPersonaPresetId)) {
        activeAgentState.selectedPersonaPresetId = personaCatalog[0].personaPresetId;
      }
    }
    renderAgentState();
  }

  function renderExecutionProfileSelectors() {
    const replySelection = replyProfileSelect.value;
    const consultationSelection = consultationProfileSelect.value;
    if (!executionProfileCatalog.length) {
      const waitingLabel = !webToken ? '等待 Web 授权'
        : executionProfileCatalogStatus === 'loading' ? '正在读取档位目录…'
          : '档位目录不可用';
      [replyProfileSelect, consultationProfileSelect].forEach((select) => {
        select.replaceChildren(new Option(waitingLabel, ''));
        select.value = '';
        select.disabled = true;
      });
      $('replyProfileHelp').textContent = executionProfileCatalogStatus === 'error'
        ? '服务器档位目录不可用；建议生成与咨询已禁用。'
        : '档位由服务器控制上下文和历史检索范围。';
      return;
    }
    const makeOptions = () => executionProfileCatalog.map((profile) => {
      const option = new Option(`${profile.label} · ${profile.recentMessageLimit} 条`, profile.executionProfileId);
      option.title = `${profile.description} ${profile.runtimeNote}`;
      return option;
    });
    [replyProfileSelect, consultationProfileSelect].forEach((select) => {
      const previous = select === replyProfileSelect ? replySelection : consultationSelection;
      select.replaceChildren(...makeOptions());
      const fallback = select === replyProfileSelect ? 'Quick' : 'Balanced';
      select.value = executionProfileCatalog.some((profile) => profile.executionProfileId === previous)
        ? previous : executionProfileCatalog.some((profile) => profile.executionProfileId === fallback) ? fallback : '';
      select.disabled = !webToken || executionProfileCatalogStatus !== 'ready' || executionProfileCatalog.length === 0;
    });
    const selected = executionProfileCatalog.find((profile) => profile.executionProfileId === replyProfileSelect.value);
    $('replyProfileHelp').textContent = selected
      ? `${selected.description} ${selected.runtimeNote}`
      : executionProfileCatalogStatus === 'loading' ? '正在读取服务端档位目录…' : '服务器档位目录不可用；生成操作已禁用。';
  }

  async function loadExecutionProfileCatalog(force = false) {
    if (!webToken) return;
    if (executionProfileCatalogStatus === 'loading') return;
    if (executionProfileCatalogStatus === 'ready' && !force) return;
    if (executionProfileController) executionProfileController.abort();
    const controller = new AbortController();
    executionProfileController = controller;
    const tokenAtStart = webToken;
    executionProfileCatalogStatus = 'loading';
    renderExecutionProfileSelectors();
    const result = await apiRequest('/api/ai/execution-profiles', { signal: controller.signal });
    if (result.aborted || tokenAtStart !== webToken) return;
    if (executionProfileController === controller) executionProfileController = null;
    if (isUnauthorized(result)) {
      handleUnauthorized();
      return;
    }
    const received = Array.isArray(result.data?.profiles) ? result.data.profiles : [];
    const supported = new Set(['Quick', 'Balanced', 'Deep', 'Max']);
    executionProfileCatalog = result.ok ? received.filter((item) => item
      && supported.has(item.executionProfileId)
      && typeof item.label === 'string'
      && typeof item.description === 'string'
      && Number.isInteger(item.recentMessageLimit)) : [];
    executionProfileCatalogStatus = executionProfileCatalog.length ? 'ready' : 'error';
    renderExecutionProfileSelectors();
    renderAgentState();
  }

  function isValidSuggestionBatch(batch, state, personaPresetId, executionProfileId) {
    if (!batch || !isSameScope(batch, currentConversation)
      || normalizeStatus(batch.taskType) !== 'replycandidates'
      || normalizeStatus(batch.status) !== 'completed'
      || batch.personaPresetId !== personaPresetId
      || batch.executionProfileId !== executionProfileId
      || !isValidAgentGuid(batch.runId)
      || !isValidAgentGuid(batch.batchId)
      || !Array.isArray(batch.suggestions) || batch.suggestions.length !== 3) return false;
    const ids = new Set();
    return batch.suggestions.every((item) => {
      if (!item || typeof item.id !== 'string' || !item.id.trim()
        || typeof item.text !== 'string' || !item.text.trim() || ids.has(item.id)) return false;
      ids.add(item.id);
      return true;
    }) && state.scopeKey === activeAgentScopeKey;
  }

  function isValidAgentRun(run, runId, expectedTask, state, expectedProfileId = null) {
    return run && isValidAgentGuid(run.runId) && run.runId.toLowerCase() === runId.toLowerCase()
      && normalizeStatus(run.taskType) === normalizeStatus(expectedTask)
      && normalizeStatus(run.status) === 'completed'
      && (!expectedProfileId || run.executionProfileId === expectedProfileId)
      && typeof run.runtimeName === 'string' && run.runtimeName.trim().length > 0
      && state && String(run.accountId || '').toLowerCase() === String(currentConversation?.accountId || '').toLowerCase()
      && String(run.conversationId || '').toLowerCase() === String(currentConversation?.conversationId || '').toLowerCase()
      && state.scopeKey === activeAgentScopeKey;
  }

  async function fetchAgentRun(runId, expectedTask, state, signal, expectedProfileId = null) {
    const result = await apiRequest(`/api/ai/runs/${encodeURIComponent(runId)}`, { signal });
    if (!result.ok) return { result, run: null };
    return { result, run: isValidAgentRun(result.data, runId, expectedTask, state, expectedProfileId) ? result.data : null };
  }

  async function createSuggestionBatch() {
    const state = activeAgentState;
    const personaPresetId = state?.selectedPersonaPresetId;
    const executionProfileId = document.body.classList.contains('is-minimal') ? 'Quick' : (replyProfileSelect.value || 'Quick');
    if (!state || !webToken || !currentConversation || personaCatalogStatus !== 'ready'
      || executionProfileCatalogStatus !== 'ready'
      || !personaCatalog.some((item) => item.personaPresetId === personaPresetId) || state.suggestionBusy) return;
    if (suggestionController) suggestionController.abort();
    const controller = new AbortController();
    suggestionController = controller;
    const tokenAtStart = webToken;
    const scopeAtStart = activeAgentScopeKey;
    const previousBatchId = state.suggestionBatch?.personaPresetId === personaPresetId
      && state.suggestionBatch?.executionProfileId === executionProfileId
      ? toCanonicalAgentGuid(state.suggestionBatch.batchId) : null;
    state.suggestionBusy = true;
    state.suggestionError = '';
    state.suggestionFeedback = '';
    renderAgentState();
    const body = { personaPresetId, executionProfileId };
    if (previousBatchId) body.previousBatchId = previousBatchId;
    const result = await apiRequest('/api/ai/suggestions', { method: 'POST', body, signal: controller.signal });
    if (result.aborted || tokenAtStart !== webToken || scopeAtStart !== activeAgentScopeKey || state !== activeAgentState) return;
    if (isUnauthorized(result)) {
      handleUnauthorized();
      return;
    }
    if (!result.ok) {
      if (suggestionController === controller) suggestionController = null;
      state.suggestionBusy = false;
      state.suggestionError = formatRequestError(result).message;
      renderAgentState();
      return;
    }
    if (!isValidSuggestionBatch(result.data, state, personaPresetId, executionProfileId)) {
      if (suggestionController === controller) suggestionController = null;
      state.suggestionBusy = false;
      state.suggestionError = '建议响应未通过范围、人设、状态或三条内容校验；未展示该结果。';
      renderAgentState();
      return;
    }
    const audit = await fetchAgentRun(result.data.runId, 'ReplyCandidates', state, controller.signal, executionProfileId);
    if (audit.result.aborted || tokenAtStart !== webToken || scopeAtStart !== activeAgentScopeKey || state !== activeAgentState) return;
    if (isUnauthorized(audit.result)) {
      handleUnauthorized();
      return;
    }
    if (!audit.result.ok || !audit.run) {
      if (suggestionController === controller) suggestionController = null;
      state.suggestionBusy = false;
      const detail = audit.result.ok
        ? 'AI 运行记录与当前任务或会话不一致，无法确认 Runtime 来源。'
        : formatRequestError(audit.result).message;
      state.suggestionError = `无法确认 Runtime 来源，未展示建议。${detail}`;
      renderAgentState();
      return;
    }
    if (suggestionController === controller) suggestionController = null;
    state.suggestionBusy = false;
    state.suggestionBatch = result.data;
    state.suggestionRuntimeName = audit.run.runtimeName;
    state.suggestionIsDemoRuntime = /fake/i.test(audit.run.runtimeName);
    state.suggestionError = '';
    state.suggestionFeedback = '';
    renderAgentState();
  }

  function cancelSuggestionGeneration() {
    if (!suggestionController || !activeAgentState?.suggestionBusy) return;
    const controller = suggestionController;
    suggestionController = null;
    activeAgentState.suggestionBusy = false;
    activeAgentState.suggestionError = '已发出取消请求；如果服务端已经完成，运行记录仍会保留。';
    controller.abort();
    renderAgentState();
  }

  async function copySuggestion(index) {
    if (activeAgentState?.suggestionIsDemoRuntime) return;
    const batch = activeAgentState?.suggestionBatch;
    const suggestion = Array.isArray(batch?.suggestions) ? batch.suggestions[index] : null;
    if (!suggestion || typeof suggestion.text !== 'string') return;
    try {
      if (navigator.clipboard?.writeText) {
        await navigator.clipboard.writeText(suggestion.text);
      } else {
        const input = document.createElement('textarea');
        input.value = suggestion.text;
        input.setAttribute('readonly', '');
        input.style.position = 'fixed';
        input.style.opacity = '0';
        document.body.append(input);
        input.select();
        const copied = document.execCommand('copy');
        input.remove();
        if (!copied) throw new Error('clipboard unavailable');
      }
      activeAgentState.suggestionFeedback = '已复制服务器返回的建议；请自行在微信中手动粘贴。';
      activeAgentState.suggestionError = '';
      renderAgentState();
      announce('建议已复制；请自行在微信中手动粘贴。');
    } catch {
      setSuggestionFeedback('浏览器未授予剪贴板权限，请手动选择并复制这条建议。', 'error');
      announce('复制失败，请手动选择并复制这条建议。');
    }
  }

  function clearConsultationThread(state) {
    if (!state) return;
    if (state.accountId && state.conversationId) clearStoredThreadId(state.accountId, state.conversationId);
    state.threadId = null;
    state.turns = [];
    state.historyLoaded = true;
    state.historyError = '';
    state.consultationBlocked = false;
  }

  function validConsultationThread(thread, state) {
    return thread && state && isValidAgentGuid(thread.threadId)
      && thread.threadId === state.threadId
      && String(thread.accountId || '').toLowerCase() === String(currentConversation?.accountId || '').toLowerCase()
      && String(thread.conversationId || '').toLowerCase() === String(currentConversation?.conversationId || '').toLowerCase();
  }

  function validConsultationTurn(turn, state) {
    return turn && state && turn.threadId === state.threadId
      && isSameScope(turn, currentConversation)
      && isValidAgentGuid(turn.runId)
      && typeof turn.userInput === 'string' && turn.userInput.trim().length > 0
      && typeof turn.answer === 'string' && turn.answer.trim().length > 0
      && normalizeStatus(turn.status) === 'completed';
  }

  async function restoreConsultationHistory(state) {
    if (!state?.threadId || !webToken || !currentConversation || state.historyLoading) return false;
    if (consultationHistoryController) consultationHistoryController.abort();
    const controller = new AbortController();
    consultationHistoryController = controller;
    const tokenAtStart = webToken;
    const scopeAtStart = activeAgentScopeKey;
    const threadId = state.threadId;
    state.historyLoading = true;
    state.historyError = '';
    renderAgentState();
    const result = await apiRequest(`/api/ai/consultations/${encodeURIComponent(threadId)}`, { signal: controller.signal });
    if (result.aborted || tokenAtStart !== webToken || scopeAtStart !== activeAgentScopeKey || state !== activeAgentState) return false;
    if (consultationHistoryController === controller) consultationHistoryController = null;
    state.historyLoading = false;
    if (isUnauthorized(result)) {
      handleUnauthorized();
      return false;
    }
    if (!result.ok) {
      const issue = formatRequestError(result);
      if (result.status === 404 || issue.code === 'CONSULTATION_THREAD_NOT_FOUND') {
        clearConsultationThread(state);
        renderAgentState();
        return false;
      }
      state.historyError = issue.message;
      state.consultationBlocked = true;
      renderAgentState();
      return false;
    }
    const history = result.data;
    if (!validConsultationThread(history?.thread, state)
      || !Array.isArray(history.turns)
      || !history.turns.every((turn) => turn && turn.threadId === state.threadId && isSameScope(turn, currentConversation))) {
      clearConsultationThread(state);
      state.historyError = '服务端历史线程与当前授权会话不一致；已清除本地线程标识，未展示该历史。';
      renderAgentState();
      return false;
    }
    if (!history.turns.every((turn) => normalizeStatus(turn.status) !== 'completed' || validConsultationTurn(turn, state))) {
      state.historyError = '咨询历史格式无效；未展示不完整或范围异常的回答。';
      state.consultationBlocked = true;
      renderAgentState();
      return false;
    }
    const completedTurns = history.turns.filter((turn) => normalizeStatus(turn.status) === 'completed');
    const audits = await Promise.all(completedTurns.map((turn) => fetchAgentRun(turn.runId, 'FreeConsultation', state, controller.signal)));
    if (audits.some((audit) => audit.result.aborted)
      || tokenAtStart !== webToken || scopeAtStart !== activeAgentScopeKey || state !== activeAgentState) return false;
    const unauthorizedAudit = audits.find((audit) => isUnauthorized(audit.result));
    if (unauthorizedAudit) {
      handleUnauthorized();
      return false;
    }
    if (audits.some((audit) => !audit.result.ok || !audit.run)) {
      state.historyLoaded = false;
      state.historyError = '无法确认一条或多条历史回答的 Runtime 来源；未展示这些回答。请稍后重试恢复历史。';
      state.consultationBlocked = true;
      renderAgentState();
      return false;
    }
    state.turns = completedTurns.map((turn, index) => ({
      ...turn,
      runtimeName: audits[index].run.runtimeName,
      executionProfileId: audits[index].run.executionProfileId,
      isDemoRuntime: /fake/i.test(audits[index].run.runtimeName)
    }));
    state.historyLoaded = true;
    state.historyError = '';
    state.consultationBlocked = false;
    renderAgentState();
    return true;
  }

  async function createConsultationThread(state) {
    const result = await apiRequest('/api/ai/consultations', { method: 'POST', signal: consultationController?.signal });
    if (result.aborted) return null;
    if (isUnauthorized(result)) {
      handleUnauthorized();
      return null;
    }
    if (!result.ok) {
      state.consultationError = formatRequestError(result).message;
      return null;
    }
    const thread = result.data;
    if (!thread || !isValidAgentGuid(thread.threadId)
      || state.scopeKey !== activeAgentScopeKey
      || String(thread.accountId || '').toLowerCase() !== String(currentConversation?.accountId || '').toLowerCase()
      || String(thread.conversationId || '').toLowerCase() !== String(currentConversation?.conversationId || '').toLowerCase()) {
      state.consultationError = '新建咨询线程响应与当前授权会话不一致；未继续提交问题。';
      return null;
    }
    state.threadId = thread.threadId;
    state.turns = [];
    state.historyLoaded = true;
    state.historyError = '';
    state.consultationBlocked = false;
    state.consultationError = '';
    writeStoredThreadId(state.accountId, state.conversationId, thread.threadId);
    return thread.threadId;
  }

  async function submitConsultation(event) {
    event.preventDefault();
    const state = activeAgentState;
    const userInput = consultationQuestion.value.trim();
    if (!state || !webToken || !currentConversation || !userInput
      || executionProfileCatalogStatus !== 'ready'
      || state.consultationBusy || state.historyLoading || state.consultationBlocked) return;
    if (userInput.length > 4000) {
      state.consultationError = '问题最多 4,000 个字符。';
      renderAgentState();
      return;
    }
    if (consultationController) consultationController.abort();
    const controller = new AbortController();
    consultationController = controller;
    const tokenAtStart = webToken;
    const scopeAtStart = activeAgentScopeKey;
    const executionProfileId = consultationProfileSelect.value || 'Balanced';
    state.consultationBusy = true;
    state.consultationError = '';
    renderAgentState();

    if (state.threadId && !state.historyLoaded) await restoreConsultationHistory(state);
    if (tokenAtStart !== webToken || scopeAtStart !== activeAgentScopeKey || state !== activeAgentState) return;
    if (state.consultationBlocked) {
      state.consultationBusy = false;
      if (consultationController === controller) consultationController = null;
      renderAgentState();
      return;
    }
    if (!state.threadId) {
      const createdId = await createConsultationThread(state);
      if (!createdId) {
        state.consultationBusy = false;
        if (consultationController === controller) consultationController = null;
        renderAgentState();
        return;
      }
    }

    if (controller.signal.aborted || tokenAtStart !== webToken || scopeAtStart !== activeAgentScopeKey || state !== activeAgentState) {
      state.consultationBusy = false;
      if (consultationController === controller) consultationController = null;
      return;
    }

    const threadId = state.threadId;
    const result = await apiRequest(`/api/ai/consultations/${encodeURIComponent(threadId)}/turns`, {
      method: 'POST',
      body: { userInput, executionProfileId },
      signal: controller.signal
    });
    if (result.aborted || tokenAtStart !== webToken || scopeAtStart !== activeAgentScopeKey || state !== activeAgentState) return;
    if (isUnauthorized(result)) {
      handleUnauthorized();
      return;
    }
    if (!result.ok) {
      if (consultationController === controller) consultationController = null;
      state.consultationBusy = false;
      const issue = formatRequestError(result);
      if (result.status === 404 || issue.code === 'CONSULTATION_THREAD_NOT_FOUND') clearConsultationThread(state);
      state.consultationError = issue.message;
      renderAgentState();
      return;
    }
    if (!validConsultationTurn(result.data, state)) {
      if (consultationController === controller) consultationController = null;
      state.consultationBusy = false;
      state.consultationError = '咨询结果未通过线程、会话范围或完成状态校验；未展示该回答。';
      renderAgentState();
      return;
    }
    const audit = await fetchAgentRun(result.data.runId, 'FreeConsultation', state, controller.signal, executionProfileId);
    if (audit.result.aborted || tokenAtStart !== webToken || scopeAtStart !== activeAgentScopeKey || state !== activeAgentState) return;
    if (isUnauthorized(audit.result)) {
      handleUnauthorized();
      return;
    }
    if (!audit.result.ok || !audit.run) {
      if (consultationController === controller) consultationController = null;
      state.consultationBusy = false;
      state.consultationError = `问题已提交，但无法确认 Runtime 来源；回答未展示。${audit.result.ok ? '运行记录范围或状态校验失败。' : formatRequestError(audit.result).message}`;
      state.consultationBlocked = true;
      state.historyLoaded = false;
      consultationQuestion.value = '';
      renderAgentState();
      return;
    }
    if (consultationController === controller) consultationController = null;
    state.consultationBusy = false;
    state.turns.push({
      ...result.data,
      executionProfileId,
      runtimeName: audit.run.runtimeName,
      isDemoRuntime: /fake/i.test(audit.run.runtimeName)
    });
    state.historyLoaded = true;
    state.historyError = '';
    state.consultationError = '';
    consultationQuestion.value = '';
    renderAgentState();
  }

  function cancelConsultationTurn() {
    if (!consultationController || !activeAgentState?.consultationBusy) return;
    const controller = consultationController;
    consultationController = null;
    activeAgentState.consultationBusy = false;
    activeAgentState.consultationError = '已发出取消请求；如果服务端已经完成，本轮记录仍会保留。';
    controller.abort();
    renderAgentState();
  }

  function formatRequestError(result) {
    const issue = readApiError(result);
    return { ...issue, message: `${issue.title}${issue.detail && issue.detail !== issue.title ? ` ${issue.detail}` : ''}` };
  }

  function isSameScope(data, conversation) {
    return data
      && conversation
      && String(data.accountId || '').toLowerCase() === String(conversation.accountId || '').toLowerCase()
      && String(data.conversationId || '').toLowerCase() === String(conversation.conversationId || '').toLowerCase();
  }

  function validConversation(data) {
    return data
      && typeof data.accountId === 'string'
      && typeof data.conversationId === 'string'
      && data.accountId.length > 0
      && data.conversationId.length > 0;
  }

  function populateSelect(select, placeholder, items, selectedId, getId, getLabel) {
    if (!select) return;
    const options = [new Option(placeholder, '')];
    for (const item of items) {
      const id = getId(item);
      if (typeof id !== 'string' || !id) continue;
      options.push(new Option(getLabel(item), id));
    }
    select.replaceChildren(...options);
    select.value = selectedId || '';
    select.disabled = !webToken || items.length === 0;
  }

  function renderDirectorySelectors() {
    const accounts = [...directoryAccounts];
    if (currentConversation && !accounts.some((item) => String(item.accountId).toLowerCase() === String(currentConversation.accountId).toLowerCase())) {
      accounts.push({ accountId: currentConversation.accountId, displayName: '当前微信账号' });
    }
    const conversations = [...directoryConversations];
    if (currentConversation && !conversations.some((item) => String(item.conversationId).toLowerCase() === String(currentConversation.conversationId).toLowerCase())) {
      conversations.push(currentConversation);
    }
    const accountLabel = (item, index) => {
      const name = typeof item.displayName === 'string' && item.displayName.trim()
        ? item.displayName.trim()
        : `微信账号 ${index + 1}`;
      const state = String(item.availability || '').toLowerCase();
      return `${name} · ${state === 'readeronline' ? 'Reader 在线' : state === 'offline' ? 'Reader 离线' : state === 'error' ? 'Reader 错误' : '仅有历史记录'}`;
    };
    const conversationLabel = (item, index) => typeof item.displayName === 'string' && item.displayName.trim()
      ? item.displayName.trim()
      : `未命名会话 ${index + 1}`;
    [accountSelect, chatAccountSelect].forEach((select) => {
      populateSelect(select, '选择微信账号', accounts, selectedAccountId, (item) => item.accountId,
        (item) => accountLabel(item, accounts.indexOf(item)));
    });
    [conversationSelect, chatConversationSelect].forEach((select) => {
      populateSelect(select, '选择联系人/会话', conversations, selectedConversationId,
        (item) => item.conversationId, (item) => conversationLabel(item, conversations.indexOf(item)));
    });
    renderReaderTargetList();
  }

  function setDiscoveryStatus(message, state = 'idle') {
    discoveryStatus.textContent = message;
    discoveryStatus.dataset.state = state;
  }

  function setReaderTargetFeedback(message, state = 'idle') {
    readerTargetStatus.textContent = message;
    readerTargetStatus.dataset.state = state;
  }

  function renderReaderTargetList() {
    if (!readerTargetList) return;
    readerTargetList.replaceChildren();
    if (!webToken || directoryConversations.length === 0) {
      const empty = document.createElement('span');
      empty.className = 'directory-empty';
      empty.textContent = webToken ? '当前账号尚无已导入的 Direct 会话。' : '授权后读取已导入会话。';
      readerTargetList.append(empty);
      saveReaderTargetsButton.disabled = true;
      discoverReaderSessionsButton.disabled = !webToken || !selectedAccountId;
      readerTargetSearch.disabled = true;
      return;
    }

    if (pendingReaderTargetIds === null || pendingReaderTargetAccountId !== selectedAccountId) {
      pendingReaderTargetIds = new Set(directoryConversations.filter((item) => item.isMonitored === true)
        .map((item) => item.conversationId));
      pendingReaderTargetAccountId = selectedAccountId;
    }
    readerTargetSearch.disabled = false;
    const search = readerTargetSearch.value.trim().toLocaleLowerCase();
    const visibleConversations = directoryConversations.filter((item) => {
      if (!search) return true;
      return String(item.displayName || '').toLocaleLowerCase().includes(search);
    });
    visibleConversations.forEach((conversation, visibleIndex) => {
      const index = directoryConversations.indexOf(conversation);
      const label = document.createElement('label');
      label.className = 'reader-target-option';
      const checkbox = document.createElement('input');
      checkbox.type = 'checkbox';
      checkbox.value = conversation.conversationId;
      checkbox.checked = pendingReaderTargetIds.has(conversation.conversationId);
      checkbox.setAttribute('aria-label', `${conversation.displayName || `未命名会话 ${index + 1}`} 后台监听`);
      const copy = document.createElement('span');
      copy.textContent = conversation.displayName || `未命名会话 ${index + 1}`;
      const state = document.createElement('small');
      const captureState = String(conversation.captureState || 'Unknown');
      state.textContent = conversation.isMonitored ? `监听 · ${captureState}` : '未订阅';
      copy.append(state);
      label.append(checkbox, copy);
      readerTargetList.append(label);
      checkbox.addEventListener('change', () => {
        if (checkbox.checked && pendingReaderTargetIds.size >= 20) {
          checkbox.checked = false;
          setReaderTargetFeedback('最多同时选择 20 个会话。', 'error');
          return;
        }
        if (checkbox.checked) pendingReaderTargetIds.add(conversation.conversationId);
        else pendingReaderTargetIds.delete(conversation.conversationId);
        setReaderTargetFeedback(`已选择 ${pendingReaderTargetIds.size}/20 个后台监听会话。`);
      });
    });
    if (visibleConversations.length === 0) {
      const empty = document.createElement('span');
      empty.className = 'directory-empty';
      empty.textContent = '没有匹配的会话。';
      readerTargetList.append(empty);
    }

    saveReaderTargetsButton.disabled = !webToken || !selectedAccountId;
    discoverReaderSessionsButton.disabled = !webToken || !selectedAccountId;
  }

  async function loadAccountDirectory(current, signal = undefined) {
    const accountResult = await apiRequest('/api/accounts', { signal });
    if (accountResult.aborted || isUnauthorized(accountResult)) return accountResult;
    if (!accountResult.ok || !Array.isArray(accountResult.data?.accounts)) {
      directoryAccounts = [];
      directoryConversations = [current];
      renderDirectorySelectors();
      return accountResult;
    }

    directoryAccounts = accountResult.data.accounts.filter((item) => item && isValidAgentGuid(item.accountId));
    selectedAccountId = current.accountId;
    selectedConversationId = current.conversationId;
    if (!directoryAccounts.some((item) => String(item.accountId).toLowerCase() === String(current.accountId).toLowerCase())) {
      directoryAccounts.push({ accountId: current.accountId, displayName: null, availability: 'Unknown' });
    }
    renderDirectorySelectors();

    const conversationResult = await apiRequest(
      `/api/accounts/${encodeURIComponent(current.accountId)}/conversations?limit=2000`, { signal });
    if (conversationResult.aborted || isUnauthorized(conversationResult)) return conversationResult;
    if (!conversationResult.ok || !Array.isArray(conversationResult.data?.conversations)) {
      directoryConversations = [current];
      renderDirectorySelectors();
      return conversationResult;
    }

    directoryConversations = conversationResult.data.conversations.filter((item) =>
      item && String(item.accountId).toLowerCase() === String(current.accountId).toLowerCase()
      && isValidAgentGuid(item.conversationId));
    readerTargetVersion = Number(directoryConversations[0]?.targetVersion || 0);
    if (pendingReaderTargetAccountId !== current.accountId) {
      pendingReaderTargetIds = null;
      pendingReaderTargetAccountId = current.accountId;
    }
    if (!directoryConversations.some((item) => String(item.conversationId).toLowerCase() === String(current.conversationId).toLowerCase())) {
      directoryConversations.push(current);
    }
    renderDirectorySelectors();
    return { ok: true, status: 200, data: null };
  }

  async function selectAccount(accountId) {
    if (!webToken || !isValidAgentGuid(accountId) || accountId === selectedAccountId) return;
    selectedAccountId = accountId;
    selectedConversationId = null;
    directoryConversations = [];
    pendingReaderTargetIds = null;
    pendingReaderTargetAccountId = accountId;
    readerTargetSearch.value = '';
    renderDirectorySelectors();
    clearCurrentConversationSelection('请选择此账号下的会话。');
    const result = await apiRequest(`/api/accounts/${encodeURIComponent(accountId)}/conversations?limit=2000`);
    if (isUnauthorized(result)) {
      handleUnauthorized();
      return;
    }
    if (!result.ok || !Array.isArray(result.data?.conversations)) {
      const issue = formatRequestError(result);
      setApiStatus(`读取会话目录失败：${issue.message}`, issue.state);
      return;
    }
    directoryConversations = result.data.conversations.filter((item) => item
      && String(item.accountId).toLowerCase() === String(accountId).toLowerCase()
      && isValidAgentGuid(item.conversationId));
    readerTargetVersion = Number(directoryConversations[0]?.targetVersion || 0);
    pendingReaderTargetIds = null;
    pendingReaderTargetAccountId = accountId;
    renderDirectorySelectors();
    setApiStatus(directoryConversations.length
      ? '账号目录已读取，请选择要查看的会话。'
      : '此账号目前没有已导入的 Direct 会话。', directoryConversations.length ? 'success' : 'idle');
    setDiscoveryStatus('仅扫描本机活动 Reader 账号；扫描需由你主动发起。');
  }

  async function startReaderDiscovery() {
    if (!webToken || !selectedAccountId || activeDiscoveryJobId) return;
    discoverReaderSessionsButton.disabled = true;
    setDiscoveryStatus('已请求 Reader 发现本机 Direct 会话，正在等待处理…', 'loading');
    const accountId = selectedAccountId;
    const result = await apiRequest(`/api/accounts/${encodeURIComponent(accountId)}/discovery-jobs`, { method: 'POST', body: {} });
    if (isUnauthorized(result)) {
      handleUnauthorized();
      return;
    }
    if (!result.ok || !isValidAgentGuid(result.data?.jobId)) {
      const issue = formatRequestError(result);
      setDiscoveryStatus(`无法开始目录扫描：${issue.message}`, 'error');
      discoverReaderSessionsButton.disabled = false;
      return;
    }
    activeDiscoveryJobId = result.data.jobId;
    await pollReaderDiscovery(accountId, activeDiscoveryJobId);
  }

  async function pollReaderDiscovery(accountId, jobId) {
    if (!webToken || activeDiscoveryJobId !== jobId) return;
    const result = await apiRequest(
      `/api/accounts/${encodeURIComponent(accountId)}/discovery-jobs/${encodeURIComponent(jobId)}`);
    if (isUnauthorized(result)) {
      handleUnauthorized();
      return;
    }
    if (!result.ok) {
      const issue = formatRequestError(result);
      activeDiscoveryJobId = null;
      setDiscoveryStatus(`目录扫描状态不可用：${issue.message}`, 'error');
      discoverReaderSessionsButton.disabled = false;
      return;
    }
    const status = String(result.data?.status || '').toLowerCase();
    if (status === 'pending') {
      setDiscoveryStatus('请求已进入队列；Reader 离线时请先启动本机读取器。', 'loading');
    } else if (status === 'running') {
      setDiscoveryStatus('Reader 正在读取会话目录元数据；没有读取或上传聊天正文。', 'loading');
    } else if (status === 'completed') {
      activeDiscoveryJobId = null;
      setDiscoveryStatus(`目录扫描完成，登记 ${Number(result.data.discoveredConversationCount) || 0} 个 Direct 会话。`, 'success');
      if (selectedAccountId === accountId) {
        const conversationsResult = await apiRequest(`/api/accounts/${encodeURIComponent(accountId)}/conversations?limit=2000`);
        if (conversationsResult.ok && Array.isArray(conversationsResult.data?.conversations)) {
          directoryConversations = conversationsResult.data.conversations.filter((item) => item
            && String(item.accountId).toLowerCase() === String(accountId).toLowerCase()
            && isValidAgentGuid(item.conversationId));
          readerTargetVersion = Number(directoryConversations[0]?.targetVersion || 0);
          renderDirectorySelectors();
        }
      }
      discoverReaderSessionsButton.disabled = false;
      return;
    } else if (status === 'failed') {
      activeDiscoveryJobId = null;
      setDiscoveryStatus(`目录扫描失败（${result.data.errorCode || 'READER_DISCOVERY_FAILED'}）。`, 'error');
      discoverReaderSessionsButton.disabled = false;
      return;
    } else {
      activeDiscoveryJobId = null;
      setDiscoveryStatus('目录扫描返回了未知状态。', 'error');
      discoverReaderSessionsButton.disabled = false;
      return;
    }
    discoveryPollTimer = window.setTimeout(() => {
      discoveryPollTimer = null;
      void pollReaderDiscovery(accountId, jobId);
    }, 2000);
  }

  async function saveReaderTargets() {
    if (!webToken || !selectedAccountId) return;
    const conversationIds = [...(pendingReaderTargetIds || [])];
    if (conversationIds.length > 20) {
      setReaderTargetFeedback('最多同时监听 20 个会话。', 'error');
      return;
    }
    saveReaderTargetsButton.disabled = true;
    setReaderTargetFeedback('正在保存监听目标…', 'loading');
    const accountId = selectedAccountId;
    const result = await apiRequest(`/api/accounts/${encodeURIComponent(accountId)}/reader-targets`, {
      method: 'PUT',
      body: { expectedVersion: readerTargetVersion, conversationIds }
    });
    if (isUnauthorized(result)) {
      handleUnauthorized();
      return;
    }
    if (!result.ok) {
      const issue = formatRequestError(result);
      setReaderTargetFeedback(issue.message, 'error');
      saveReaderTargetsButton.disabled = false;
      return;
    }
    readerTargetVersion = Number(result.data.targetVersion);
    pendingReaderTargetIds = new Set(conversationIds);
    pendingReaderTargetAccountId = accountId;
    const selected = new Set(conversationIds);
    directoryConversations = directoryConversations.map((conversation) => ({
      ...conversation,
      targetVersion: readerTargetVersion,
      isMonitored: selected.has(conversation.conversationId),
      captureState: selected.has(conversation.conversationId) ? 'Unknown' : 'NotMonitored'
    }));
    renderDirectorySelectors();
    setReaderTargetFeedback('监听集合已保存；Reader 将在下一次同步时应用。', 'success');
  }

  function clearCurrentConversationSelection(message) {
    requestVersion++;
    if (activeController) activeController.abort();
    activeController = null;
    if (olderPageController) olderPageController.abort();
    olderPageController = null;
    olderPageInProgress = false;
    stopRevisionPolling();
    clearActiveAgentScope();
    currentConversation = null;
    currentMessageSnapshot = null;
    lastMessageSetRevision = null;
    currentSessionValue.textContent = message;
    chatSessionValue.textContent = message;
    chatSessionType.textContent = '当前账号下的会话';
    captureStateValue.textContent = '实时状态未知';
    memoryCurrentSession.textContent = message;
    minimalCurrentSession.textContent = message;
    setPill(currentSessionStatus, '请选择会话', 'pending');
    setPill(chatCaptureStatus, '状态未知', 'muted');
    setPill(messageStatusPill, '请选择会话', 'muted');
    setMessageState('请选择会话', '选择一个已导入的一对一会话后，聊天记录和 AI 功能才会加载。');
    setIdentityUnavailable('请选择会话', '身份方向只会在选定会话并读取其服务器数据后显示。');
    setMemoryUnavailable('请选择会话', '选择会话后读取对应的人工记忆。', 'idle');
    renderDirectorySelectors();
  }

  async function selectConversation(conversationId) {
    if (!webToken || !isValidAgentGuid(conversationId) || conversationId === selectedConversationId) return;
    selectedConversationId = conversationId;
    renderDirectorySelectors();
    setWorkspacePage('chat');
    await loadWorkspace('已切换会话；聊天、记忆和 AI 上下文已按所选会话重新读取。');
  }

  function updateCurrentConversation(conversation) {
    currentConversation = conversation;
    selectedAccountId = conversation.accountId;
    selectedConversationId = conversation.conversationId;
    const name = typeof conversation.displayName === 'string' && conversation.displayName.trim()
      ? conversation.displayName.trim()
      : '已读取当前会话';
    const type = typeof conversation.conversationType === 'string' ? conversation.conversationType : '';
    const sessionLabel = type ? `${name} · ${type}` : name;
    currentSessionValue.textContent = sessionLabel;
    chatSessionValue.textContent = name;
    chatSessionType.textContent = type ? `${type} · 当前查看会话` : '当前查看会话';
    renderDirectorySelectors();
    memoryCurrentSession.textContent = sessionLabel;
    minimalCurrentSession.textContent = sessionLabel;
    const captureStateRaw = String(conversation.captureState || '').trim();
    const captureState = captureStateRaw.toLowerCase();
    const captureLabels = {
      listening: '实时监听中',
      starting: 'Reader 启动中',
      idle: 'Reader 在线 · 暂无目标',
      notmonitored: '未订阅监听',
      offline: 'Reader 离线',
      error: 'Reader 错误',
      unknown: '实时状态未知'
    };
    const captureLabel = captureLabels[captureState] || '实时状态未知';
    captureStateValue.textContent = captureLabel;
    if (captureState === 'listening') {
      setPill(currentSessionStatus, captureLabel, 'ok');
      setPill(chatCaptureStatus, captureLabel, 'ok');
    } else if (captureState === 'notmonitored' || captureState === 'idle') {
      setPill(currentSessionStatus, captureLabel, 'pending');
      setPill(chatCaptureStatus, captureLabel, 'pending');
    } else if (captureState === 'error' || captureState === 'offline') {
      setPill(currentSessionStatus, captureLabel, 'error');
      setPill(chatCaptureStatus, captureLabel, 'error');
    } else {
      setPill(currentSessionStatus, captureLabel, 'muted');
      setPill(chatCaptureStatus, captureLabel, 'muted');
    }
    activateAgentScope(conversation);
  }

  function updateMessageError(issue) {
    setPill(messageStatusPill, issue.code === 'CURRENT_CONVERSATION_NOT_FOUND' ? '无当前会话' : '读取失败', 'error');
    setMessageState(issue.title, issue.detail, 'error');
  }

  function updateCurrentFailure(issue) {
    stopRevisionPolling();
    clearActiveAgentScope();
    currentConversation = null;
    currentMessageSnapshot = null;
    currentSessionValue.textContent = issue.title;
    chatSessionValue.textContent = issue.title;
    chatSessionType.textContent = '会话不可用';
    captureStateValue.textContent = '当前会话不可用';
    memoryCurrentSession.textContent = '当前会话不可用';
    minimalCurrentSession.textContent = '当前会话不可用';
    renderDirectorySelectors();
    setPill(currentSessionStatus, issue.code === 'CURRENT_CONVERSATION_NOT_FOUND' ? '未找到' : '不可用', 'error');
    setPill(chatCaptureStatus, issue.code === 'CURRENT_CONVERSATION_NOT_FOUND' ? '未找到' : '不可用', 'error');
    updateMessageError(issue);
    setIdentityUnavailable(issue.title, issue.detail, 'error');
    setMemoryUnavailable(issue.title, issue.detail, 'error');
    setApiStatus(issue.message, issue.state);
  }

  function formatTime(value) {
    if (!value) return '时间未知';
    const date = new Date(value);
    if (Number.isNaN(date.getTime())) return '时间未知';
    return new Intl.DateTimeFormat('zh-HK', { dateStyle: 'short', timeStyle: 'short' }).format(date);
  }

  function directionEvidenceLabel(evidence) {
    switch (String(evidence || '').toLowerCase()) {
      case 'userconfirmed': return '身份已确认';
      case 'sourceconfirmed': return '来源已确认';
      case 'defaultsmallsenderid': return '默认方向 · 待确认';
      default: return '方向待确认';
    }
  }

  function renderMessageRows(snapshot, conversation) {
    if (!isSameScope(snapshot, conversation) || !Array.isArray(snapshot.messages)) {
      const issue = { code: 'INVALID_MESSAGE_SNAPSHOT', title: '消息响应范围或格式无效。', detail: '为避免显示其他会话内容，已隐藏此响应。' };
      updateMessageError(issue);
      return false;
    }

    currentMessageSnapshot = snapshot;
    const currentCalibrationVersion = Number(conversation.calibrationVersion);
    const messageCalibrationVersion = Number(snapshot.calibrationVersion);
    const directionsMatchCurrent = Number.isFinite(currentCalibrationVersion)
      && Number.isFinite(messageCalibrationVersion)
      && currentCalibrationVersion === messageCalibrationVersion;
    setPill(
      messageStatusPill,
      directionsMatchCurrent ? `${snapshot.messages.length} 条消息` : '方向已隐藏',
      directionsMatchCurrent ? (snapshot.messages.length ? 'ok' : 'muted') : 'pending'
    );
    messageArea.classList.add('has-messages');

    if (snapshot.messages.length === 0) {
      const coverage = String(snapshot.historyCoverage || 'Unknown');
      setMessageState(
        '当前会话没有已归档消息',
        `消息接口读取成功；历史覆盖状态：${coverage}。此状态不代表微信历史完整。`
      );
      messageArea.classList.add('has-messages');
      const status = document.createElement('div');
      status.className = 'message-status';
      status.textContent = `消息集版本 ${snapshot.messageSetRevision || '未知'} · 校准版本 ${snapshot.calibrationVersion ?? '未知'}`;
      messageArea.append(status);
      return true;
    }

    const list = document.createElement('div');
    list.className = 'message-list';
    snapshot.messages.forEach((message) => {
      list.append(createMessageRow(message, directionsMatchCurrent));
    });

    const status = createMessageStatus(snapshot, directionsMatchCurrent);
    const olderButton = createLoadOlderButton(snapshot.hasMoreMessages);
    messageArea.replaceChildren(...[olderButton, list, status].filter(Boolean));
    messageArea.scrollTop = messageArea.scrollHeight;
    return directionsMatchCurrent ? true : 'version-mismatch';
  }

  function createMessageRow(message, directionsMatchCurrent) {
      const rawDirection = String(message.direction || 'Unknown').toLowerCase();
      const direction = directionsMatchCurrent ? rawDirection : 'unknown';
      const outgoing = direction === 'outgoing';
      const incoming = direction === 'incoming';
      const row = document.createElement('article');
      row.className = `message-row ${outgoing ? 'is-outgoing' : incoming ? 'is-incoming' : 'is-unknown'}`;
      row.dataset.messageId = String(message.messageId || '').toLowerCase();

      const avatar = document.createElement('span');
      avatar.className = 'message-avatar';
      avatar.setAttribute('aria-hidden', 'true');
      avatar.textContent = outgoing ? '我' : incoming ? '会话' : '?';

      const content = document.createElement('div');
      content.className = 'message-content';
      const meta = document.createElement('div');
      meta.className = 'message-meta';
      const role = document.createElement('span');
      role.textContent = outgoing ? '我' : incoming ? '对方' : '方向未知';
      const time = document.createElement('time');
      time.textContent = formatTime(message.occurredAt);
      meta.append(role, time);

      const bubble = document.createElement('div');
      bubble.className = 'message-bubble';
      const text = typeof message.text === 'string' ? message.text : '';
      if (text.length > 0) {
        bubble.textContent = text;
      } else {
        const messageType = typeof message.messageType === 'string' ? message.messageType : 'Unknown';
        bubble.textContent = `当前界面未渲染此消息类型的正文（${messageType}）。`;
      }
      content.append(meta, bubble);

      const evidence = document.createElement('div');
      evidence.className = 'message-evidence';
      evidence.textContent = directionsMatchCurrent
        ? directionEvidenceLabel(message.directionEvidence)
        : '身份版本在读取期间变化 · 方向隐藏';
      content.append(evidence);

      row.append(avatar, content);
      return row;
  }

  function createMessageStatus(snapshot, directionsMatchCurrent) {
    const status = document.createElement('div');
    status.className = 'message-status';
    const coverage = String(snapshot.historyCoverage || 'Unknown');
    const coverageText = coverage.toLowerCase() === 'complete'
      ? '完整性状态：Complete'
      : coverage.toLowerCase() === 'partial'
        ? '完整性状态：Partial'
        : '历史覆盖状态：Unknown';
    const versionText = directionsMatchCurrent
      ? `校准版本 ${snapshot.calibrationVersion}`
      : '身份版本不一致，方向已隐藏';
    status.textContent = `已加载 ${snapshot.messages.length} 条 · ${coverageText} · ${versionText}`;
    return status;
  }

  function createLoadOlderButton(hasMoreMessages) {
    if (!hasMoreMessages) return null;
    const button = document.createElement('button');
    button.type = 'button';
    button.className = 'load-older-messages';
    button.dataset.loadOlderMessages = 'true';
    button.textContent = '加载更早消息';
    button.setAttribute('aria-label', '加载更早的聊天消息');
    return button;
  }

  async function loadOlderMessages(button) {
    if (olderPageInProgress || !webToken || !currentConversation || !currentMessageSnapshot) return;
    const existingMessages = currentMessageSnapshot.messages;
    const oldestMessageId = existingMessages[0]?.messageId;
    if (!isValidAgentGuid(oldestMessageId)) {
      button.disabled = true;
      button.textContent = '无法确定翻页位置';
      return;
    }

    olderPageInProgress = true;
    const scopeAtStart = {
      accountId: currentConversation.accountId,
      conversationId: currentConversation.conversationId
    };
    const versionAtStart = requestVersion;
    const controller = new AbortController();
    olderPageController = controller;
    const oldHeight = messageArea.scrollHeight;
    const oldTop = messageArea.scrollTop;
    button.disabled = true;
    button.textContent = '正在加载…';

    try {
      const result = await apiRequest(
        `/api/conversations/current/messages?limit=50&anchorMessageId=${encodeURIComponent(oldestMessageId)}`,
        { signal: controller.signal }
      );
      if (result.aborted || versionAtStart !== requestVersion) return;
      if (isUnauthorized(result)) {
        handleUnauthorized();
        return;
      }
      if (!result.ok || !isSameScope(result.data, scopeAtStart) || !Array.isArray(result.data?.messages)) {
        button.disabled = false;
        button.textContent = '加载失败，点击重试';
        return;
      }

      const page = result.data;
      if (Number(page.calibrationVersion) !== Number(currentConversation.calibrationVersion)) {
        await loadWorkspace('身份校准版本已变化，已重新读取聊天记录。');
        return;
      }

      const existingIds = new Set(
        [...messageArea.querySelectorAll('.message-row[data-message-id]')]
          .map((row) => row.dataset.messageId)
      );
      const pageMessageIds = new Set();
      const olderRows = page.messages
        .filter((message) => {
          const id = String(message.messageId || '').toLowerCase();
          if (!isValidAgentGuid(id) || existingIds.has(id) || pageMessageIds.has(id)) return false;
          pageMessageIds.add(id);
          return true;
        })
        .map((message) => createMessageRow(message, true));
      const list = messageArea.querySelector('.message-list');
      if (!list) {
        await loadWorkspace('聊天窗口状态已重置，已重新读取聊天记录。');
        return;
      }

      const mergedMessageIds = new Set();
      const mergedMessages = [];
      [...page.messages, ...existingMessages].forEach((message) => {
        const id = String(message.messageId || '').toLowerCase();
        if (!isValidAgentGuid(id) || mergedMessageIds.has(id)) return;
        mergedMessageIds.add(id);
        mergedMessages.push(message);
      });
      const mergedSnapshot = {
        ...currentMessageSnapshot,
        ...page,
        messages: mergedMessages,
        hasMoreMessages: page.hasMoreMessages
      };

      if (olderRows.length) {
        const fragment = document.createDocumentFragment();
        olderRows.forEach((row) => fragment.append(row));
        list.prepend(fragment);
      }
      currentMessageSnapshot = mergedSnapshot;
      const status = messageArea.querySelector('.message-status');
      if (status) status.replaceWith(createMessageStatus(mergedSnapshot, true));
      const loadMoreButton = messageArea.querySelector('[data-load-older-messages]');
      if (mergedSnapshot.hasMoreMessages) {
        button.disabled = false;
        button.textContent = '加载更早消息';
      } else {
        loadMoreButton?.remove();
      }

      const newHeight = messageArea.scrollHeight;
      messageArea.scrollTop = oldTop + (newHeight - oldHeight);
      setPill(messageStatusPill, `${mergedSnapshot.messages.length} 条消息`, 'ok');
    } finally {
      if (olderPageController === controller) olderPageController = null;
      olderPageInProgress = false;
    }
  }

  function getScopePair(scope) {
    if (!scope) return null;
    const self = scope.effectiveSelfRawSenderId || scope.defaultSelfRawSenderId;
    const contact = scope.contactRawSenderId;
    const candidates = Array.isArray(scope.candidateRawSenderIds) ? scope.candidateRawSenderIds : [];
    if (!self || !contact || self === contact) return null;
    if (!candidates.includes(self) || !candidates.includes(contact)) return null;
    return { self, contact };
  }

  function visiblePreviewPair() {
    return previewPair;
  }

  function updateIdentityPreview() {
    const visible = visiblePreviewPair();
    selfIdentity.textContent = visible ? visible.self : '身份候选不可比较';
    contactIdentity.textContent = visible ? visible.contact : '身份候选不可比较';
    if (!activeScope || !visible) {
      previewState.textContent = '没有唯一且可比较的候选；不能互转身份。';
      confirmCurrentButton.hidden = true;
      confirmCurrentButton.disabled = true;
      swapButton.disabled = true;
      return;
    }

    const state = String(activeScope.perspectiveState || 'Unresolved').toLowerCase();
    const confirmed = state === 'confirmed';
    const actionablePerspective = state === 'confirmed' || state === 'pendingconfirmation';
    const canSwap = Boolean(
      webToken
      && currentConversation
      && activeScope.canConfirm
      && actionablePerspective
      && activeScope.identityNamespace
      && getScopePair(activeScope)
    );
    const canConfirmCurrent = canSwap && state === 'pendingconfirmation';
    confirmCurrentButton.hidden = !canConfirmCurrent;
    confirmCurrentButton.disabled = !canConfirmCurrent;
    swapButton.disabled = !canSwap;
    swapButton.textContent = confirmed ? '互换并保存' : '互换并确认';
    previewState.textContent = confirmed
      ? '当前身份映射已确认；操作会将本人和联系人互换。'
      : state === 'pendingconfirmation'
        ? '默认映射待确认。可确认当前方向，或互换两方后确认。'
        : '方向状态未解析，不能互转身份。';
    confirmHelp.textContent = canConfirmCurrent
      ? `当前本人预览为 ${visible.self}。方向正确时点“确认当前方向”；只有预览反了才点“互换并确认”。`
      : canSwap
        ? `互换后本人候选将由 ${visible.self} 变为 ${visible.contact}，并由服务端重新计算当前身份范围下的消息方向与关联记忆。`
      : '当前范围不满足安全确认条件，不能提交身份互转。';
  }

  function renderPerspective(response, conversation) {
    if (!isSameScope(response, conversation) || !Array.isArray(response.scopes)) {
      setIdentityUnavailable('身份响应无效', '身份响应与当前会话不匹配，已隐藏候选。', 'error');
      return false;
    }
    currentPerspectiveResponse = response;

    if (response.scopes.length === 0) {
      setIdentityUnavailable('身份未知', '此会话尚无可用的发送者身份范围；不会根据消息内容推断本人方向。');
      return true;
    }

    if (response.scopes.length !== 1) {
      const namespaceCounts = new Map();
      response.scopes.forEach((scope) => {
        const namespace = scope.identityNamespace || '(空命名空间)';
        namespaceCounts.set(namespace, (namespaceCounts.get(namespace) || 0) + 1);
      });
      const sameNamespaceAmbiguous = [...namespaceCounts.values()].some((count) => count > 1);
      const message = sameNamespaceAmbiguous
        ? '同一 identity namespace 对应多个表或分片范围，来源不唯一；已禁用交换和确认。'
        : '服务端返回多个身份范围；当前页面没有范围选择器，不能安全混合候选，已禁用交换和确认。';
      setIdentityUnavailable('来源范围不唯一', message, 'error');
      return true;
    }

    const scope = response.scopes[0];
    if (Number(scope.calibrationVersion) !== Number(conversation.calibrationVersion)) {
      setIdentityUnavailable('身份版本不一致', '当前会话与身份范围的校准版本不一致。请重新读取，期间不会显示或提交混合版本候选。', 'error');
      return false;
    }

    activeScope = scope;
    previewPair = getScopePair(scope);
    const state = String(scope.perspectiveState || 'Unresolved').toLowerCase();
    const statusText = state === 'confirmed'
      ? '已确认'
      : state === 'pendingconfirmation'
        ? '默认，待确认'
        : '方向未知';
    perspectiveSummary.textContent = `${statusText} · 仅本地预览可互换`;
    perspectiveStatus.textContent = statusText;
    identityHelp.textContent = scope.identityNamespace
      ? `身份命名空间：${scope.identityNamespace}。预览交换只在本页面内存中反转两方；确认会提交明确 raw sender ID 和当前校准版本。`
      : '服务端没有提供 identity namespace，因此不能安全提交身份确认。';
    setPill(perspectiveStatus, statusText, state === 'confirmed' ? 'ok' : state === 'pendingconfirmation' ? 'pending' : 'muted');

    if (!previewPair) {
      previewState.textContent = scope.canConfirm
        ? '服务端未返回一致的本人/联系人候选映射。'
        : '当前身份范围没有一对可比较的候选。';
      confirmCurrentButton.hidden = true;
      confirmCurrentButton.disabled = true;
      swapButton.disabled = true;
      confirmHelp.textContent = '候选映射不完整或不符合比较条件，不能提交身份校准。';
      selfIdentity.textContent = '候选映射不可用';
      contactIdentity.textContent = '候选映射不可用';
      return true;
    }

    updateIdentityPreview();
    return true;
  }

  function handleUnauthorized() {
    requestVersion++;
    if (activeController) activeController.abort();
    activeController = null;
    webToken = null;
    webTokenInput.value = '';
    resetWorkspaceForNoToken();
    setPill(currentSessionStatus, '未授权', 'error');
    setPill(chatCaptureStatus, '未授权', 'error');
    setPill(messageStatusPill, '未授权', 'error');
    setMessageState('请求未获授权', 'Web Bearer token 无效或已过期。重新输入后，页面会再次发送带 Authorization 的请求。', 'error');
    setIdentityUnavailable('未授权', '身份候选仅在 Web token 验证成功后读取。', 'error');
    setMemoryUnavailable('未授权', 'Web token 未通过验证；重新授权后才能读取或维护记忆。', 'error');
    setApiStatus('Web token 未授权或已失效，请重新输入。token 已从本页面内存清除。', 'unauthorized');
    finishLoading();
  }

  function isUnauthorized(result) {
    if (!result) return false;
    const body = result.data;
    const detail = body && (body.error || body.Error);
    const code = detail && (detail.code || detail.Code);
    return result.status === 401 || code === 'UNAUTHENTICATED';
  }

  async function loadWorkspace(successMessage = null, suppliedCurrentSnapshot = null) {
    if (!webToken) {
      setApiStatus('请粘贴 Web Bearer token 后读取。', 'unauthorized');
      webTokenInput.focus();
      return;
    }

    if (selectedAccountId && !selectedConversationId) {
      clearCurrentConversationSelection('请选择此账号下的会话。');
      return;
    }

    clearRevisionPollTimer();
    if (!suppliedCurrentSnapshot && revisionPollController) revisionPollController.abort();
    if (olderPageController) olderPageController.abort();
    olderPageController = null;
    olderPageInProgress = false;
    const version = ++requestVersion;
    if (activeController) activeController.abort();
    const controller = new AbortController();
    activeController = controller;
    setLoadingState();

    const accountDirectoryResult = await apiRequest('/api/accounts', { signal: controller.signal });
    if (version !== requestVersion || accountDirectoryResult.aborted) return;
    if (isUnauthorized(accountDirectoryResult)) {
      handleUnauthorized();
      return;
    }
    if (accountDirectoryResult.ok && Array.isArray(accountDirectoryResult.data?.accounts)) {
      directoryAccounts = accountDirectoryResult.data.accounts.filter((item) => item && isValidAgentGuid(item.accountId));
      renderDirectorySelectors();
    }

    const currentResult = suppliedCurrentSnapshot
      ? { ok: true, status: 200, data: suppliedCurrentSnapshot }
      : await apiRequest('/api/conversations/current', { signal: controller.signal });
    if (version !== requestVersion || currentResult.aborted) return;
    if (isUnauthorized(currentResult)) {
      handleUnauthorized();
      return;
    }
    if (!currentResult.ok) {
      const issue = formatRequestError(currentResult);
      updateCurrentFailure(issue);
      finishLoading();
      return;
    }
    if (!validConversation(currentResult.data)) {
      updateCurrentFailure({
        code: 'INVALID_CONVERSATION_SNAPSHOT',
        title: '当前会话接口返回了无效响应。',
        detail: '缺少账号或会话范围，已停止读取相关数据。',
        message: '当前会话响应格式无效。',
        state: 'error'
      });
      finishLoading();
      return;
    }

    const conversation = currentResult.data;
    updateCurrentConversation(conversation);
    const directoryResult = await loadAccountDirectory(conversation, controller.signal);
    if (version !== requestVersion || directoryResult.aborted) return;
    if (isUnauthorized(directoryResult)) {
      handleUnauthorized();
      return;
    }
    const currentRevision = typeof conversation.messageSetRevision === 'string'
      ? conversation.messageSetRevision
      : '';
    revisionPollingEnabled = Boolean(webToken && currentRevision);
    if (!currentRevision) lastMessageSetRevision = null;
    const [messagesResult, perspectiveResult, memoryResult] = await Promise.all([
      apiRequest('/api/conversations/current/messages?limit=50', { signal: controller.signal }),
      apiRequest('/api/conversations/current/sender-perspective', { signal: controller.signal }),
      apiRequest('/api/conversations/current/memory?limit=100', { signal: controller.signal })
    ]);
    if (version !== requestVersion || messagesResult.aborted || perspectiveResult.aborted || memoryResult.aborted) return;

    if (isUnauthorized(messagesResult) || isUnauthorized(perspectiveResult) || isUnauthorized(memoryResult)) {
      handleUnauthorized();
      return;
    }

    const issues = [];
    if (!directoryResult.ok) issues.push('账号/会话目录不可用');
    if (messagesResult.ok) {
      const messageResult = renderMessageRows(messagesResult.data, conversation);
      if (messageResult === 'version-mismatch') {
        issues.push('身份校准版本变化，消息方向已隐藏');
      } else if (!messageResult) {
        issues.push('消息响应校验失败');
      }
    } else {
      const issue = formatRequestError(messagesResult);
      updateMessageError(issue);
      issues.push(issue.title);
    }

    if (perspectiveResult.ok) {
      if (!renderPerspective(perspectiveResult.data, conversation)) {
        issues.push('身份范围校验失败');
      }
    } else {
      const issue = formatRequestError(perspectiveResult);
      setIdentityUnavailable(issue.title, issue.detail, 'error');
      issues.push(issue.title);
    }

    if (memoryResult.ok) {
      if (!renderMemoryItems(memoryResult.data, conversation)) {
        issues.push('记忆响应范围或格式校验失败');
      }
    } else {
      const issue = formatRequestError(memoryResult);
      setMemoryUnavailable(issue.title, issue.detail, issue.state);
      issues.push(issue.title);
    }

    if (issues.length) {
      setApiStatus(`当前会话已读取，但部分数据不可用：${issues.join('；')}`, 'error');
    } else {
      const count = Array.isArray(messagesResult.data?.messages) ? messagesResult.data.messages.length : 0;
      setApiStatus(successMessage || `读取完成：当前会话、${count} 条消息及身份范围。`, 'success');
    }
    if (currentRevision && !issues.length) lastMessageSetRevision = currentRevision;
    finishLoading();
    scheduleRevisionPoll();
  }

  async function pollMessageSetRevision() {
    if (!revisionPollingEnabled || !webToken || !currentConversation || !isPageVisible()) return;
    if (revisionPollInProgress || activeController || memoryWritePending) {
      scheduleRevisionPoll();
      return;
    }

    revisionPollInProgress = true;
    const controller = new AbortController();
    const tokenAtStart = webToken;
    revisionPollController = controller;
    try {
      const result = await apiRequest('/api/conversations/current', { signal: controller.signal });
      if (result.aborted || tokenAtStart !== webToken || !revisionPollingEnabled || !isPageVisible()) return;
      if (isUnauthorized(result)) {
        handleUnauthorized();
        return;
      }
      if (!result.ok) {
        const issue = formatRequestError(result);
        reportRevisionPollFailure(`自动检查失败：${issue.message} 当前显示保留，稍后重试。`, issue.state);
        return;
      }
      if (!validConversation(result.data)
        || typeof result.data.messageSetRevision !== 'string'
        || !result.data.messageSetRevision) {
        reportRevisionPollFailure('自动检查未返回有效的消息集版本。当前显示保留，稍后重试。');
        return;
      }

      const nextRevision = result.data.messageSetRevision;
      const nextScopeKey = agentScopeKey(result.data);
      const currentScopeKey = agentScopeKey(currentConversation);
      if (nextScopeKey !== currentScopeKey || lastMessageSetRevision === null || nextRevision !== lastMessageSetRevision) {
        await loadWorkspace(null, result.data);
      } else {
        restoreStatusAfterRevisionPoll();
      }
    } catch {
      if (tokenAtStart === webToken && revisionPollingEnabled) {
        reportRevisionPollFailure('自动检查连接失败。当前显示保留，稍后重试。');
      }
    } finally {
      if (revisionPollController === controller) revisionPollController = null;
      revisionPollInProgress = false;
      scheduleRevisionPoll();
    }
  }

  function newIdempotencyKey() {
    if (globalThis.crypto && typeof globalThis.crypto.randomUUID === 'function') {
      return globalThis.crypto.randomUUID();
    }
    if (globalThis.crypto && typeof globalThis.crypto.getRandomValues === 'function') {
      const bytes = new Uint8Array(16);
      globalThis.crypto.getRandomValues(bytes);
      return [...bytes].map((value) => value.toString(16).padStart(2, '0')).join('');
    }
    return `${Date.now()}-${Math.random().toString(36).slice(2)}-${Math.random().toString(36).slice(2)}`;
  }

  async function confirmIdentity(swap) {
    const visible = visiblePreviewPair();
    if (!webToken || !currentConversation || !activeScope || !visible || !activeScope.canConfirm) return;
    const state = String(activeScope.perspectiveState || '').toLowerCase();
    if (state !== 'confirmed' && state !== 'pendingconfirmation') return;
    const selectedSelfRawSenderId = swap ? visible.contact : visible.self;
    const namespace = activeScope.identityNamespace;
    if (!namespace) return;

    const summary = [
      `当前本人 raw sender ID：${visible.self}`,
      `${swap ? '互换后' : '确认后'}本人 raw sender ID：${selectedSelfRawSenderId}`,
      `身份命名空间：${namespace}`,
      `校准版本：${activeScope.calibrationVersion}`,
      '',
      `${swap ? '确认后会互换本人和联系人，并由服务端更新该会话消息方向。' : '确认后会保留当前预览，并由服务端更新该会话消息方向。'}是否继续？`
    ].join('\n');
    if (!window.confirm(summary)) return;

    const version = ++requestVersion;
    if (activeController) activeController.abort();
    const controller = new AbortController();
    activeController = controller;
    confirmCurrentButton.disabled = true;
    swapButton.disabled = true;
    setApiStatus('正在提交明确的身份校准选择…', 'loading');
    connectButton.disabled = true;
    connectButton.textContent = '提交中…';

    const payload = {
      accountId: currentConversation.accountId,
      conversationId: currentConversation.conversationId,
      identityNamespace: namespace,
      accountSelfRawSenderId: selectedSelfRawSenderId,
      expectedCalibrationVersion: Number(activeScope.calibrationVersion)
    };
    const result = await apiRequest('/api/conversations/current/sender-perspective', {
      method: 'PUT',
      body: payload,
      signal: controller.signal,
      idempotencyKey: newIdempotencyKey()
    });
    if (version !== requestVersion || result.aborted) return;
    if (isUnauthorized(result)) {
      handleUnauthorized();
      return;
    }
    if (!result.ok) {
      const issue = formatRequestError(result);
      setApiStatus(`身份确认未提交：${issue.message}`, issue.state);
      confirmHelp.textContent = issue.code === 'SENDER_SCOPE_AMBIGUOUS'
        ? '服务端发现同一身份命名空间下有多个表或分片，已拒绝确认。请检查来源范围。'
        : issue.code === 'CALIBRATION_VERSION_CONFLICT' || issue.code === 'SENDER_SCOPE_CHANGED'
          ? '校准版本或来源范围已变化；请重新读取并审阅新摘要后再确认。'
          : issue.detail;
      if (issue.code === 'SENDER_SCOPE_AMBIGUOUS' || issue.code === 'CALIBRATION_VERSION_CONFLICT' || issue.code === 'SENDER_SCOPE_CHANGED') {
        await loadWorkspace('身份确认未提交；已刷新当前会话以供重新审阅。');
        return;
      }
      updateIdentityPreview();
      confirmCurrentButton.disabled = true;
      swapButton.disabled = true;
      confirmHelp.textContent = `${issue.detail} 请先重新读取并检查当前身份范围。`;
      finishLoading();
      return;
    }

    setApiStatus('身份校准已提交；正在重新读取消息方向。', 'success');
    await loadWorkspace('身份校准已提交，当前会话与消息方向已重新读取。');
  }

  function announce(message) {
    window.clearTimeout(toastTimer);
    toast.textContent = message;
    toast.hidden = false;
    requestAnimationFrame(() => toast.classList.add('is-visible'));
    toastTimer = window.setTimeout(() => {
      toast.classList.remove('is-visible');
      window.setTimeout(() => { toast.hidden = true; }, 180);
    }, 2200);
  }

  function createMemoryPayload() {
    const text = $('memoryText').value;
    const categoryValue = $('memoryCategory').value.trim();
    const observedValue = $('memoryObservedAt').value;
    const validationError = validateMemoryFields(text, categoryValue, observedValue);
    if (validationError) return { error: validationError };
    return {
      payload: {
        subjectType: $('memorySubjectType').value,
        text: text.trim(),
        category: categoryValue || null,
        observedAt: dateTimePayload(observedValue)
      }
    };
  }

  async function handleMemoryCreate(event) {
    event.preventDefault();
    if (!memoryWritable || !webToken || !currentConversation) return;
    const result = createMemoryPayload();
    if (result.error) {
      setMemoryFeedback(result.error, 'error');
      return;
    }
    await submitMemoryMutation(
      '/api/conversations/current/memory',
      { method: 'POST', body: result.payload },
      '已添加人工条目，并明确归类为用户备注（无关联聊天证据）。'
    );
  }

  async function handleMemoryEdit(event) {
    const form = event.target.closest('.memory-edit-form');
    if (!form) return;
    event.preventDefault();
    if (!memoryWritable || !webToken || !currentConversation) return;
    const item = memoryRecords.find((record) => record.memoryId === form.dataset.memoryId);
    if (!item || String(item.origin || '').toLowerCase() !== 'userentered') return;

    const text = form.querySelector('[data-memory-field="text"]').value;
    const categoryValue = form.querySelector('[data-memory-field="category"]').value.trim();
    const observedValue = form.querySelector('[data-memory-field="observedAt"]').value;
    const validationError = validateMemoryFields(text, categoryValue, observedValue);
    if (validationError) {
      setMemoryFeedback(validationError, 'error');
      return;
    }

    const payload = {
      expectedVersion: Number(item.version),
      text: text.trim(),
      category: categoryValue || null,
      observedAt: dateTimePayload(observedValue),
      sourceMessageIds: Array.isArray(item.sourceMessageIds) ? [...item.sourceMessageIds] : []
    };
    await submitMemoryMutation(
      `/api/conversations/current/memory/${encodeURIComponent(item.memoryId)}`,
      { method: 'PUT', body: payload },
      '已保存记忆更改；原有消息证据已按完整替换规则保留。'
    );
  }

  async function handleMemoryDelete(item) {
    if (!item || !memoryWritable || !webToken || !currentConversation) return;
    if (String(item.origin || '').toLowerCase() !== 'userentered'
      || String(item.status || '').toLowerCase() !== 'active') return;
    const excerpt = Array.from(String(item.text || '')).slice(0, 120).join('');
    const summary = [
      '将软删除这条人工记忆。它会从当前记忆列表移除，不会删除聊天消息。',
      '',
      `版本：${item.version}`,
      excerpt
    ].join('\n');
    if (!window.confirm(summary)) return;
    await submitMemoryMutation(
      `/api/conversations/current/memory/${encodeURIComponent(item.memoryId)}?expectedVersion=${encodeURIComponent(item.version)}`,
      { method: 'DELETE' },
      '这条人工记忆已软删除，并从当前列表移除。'
    );
  }

  modeButtons.forEach((button) => {
    button.addEventListener('click', () => setMode(button.dataset.mode));
  });

  [accountSelect, chatAccountSelect].forEach((select) => {
    select.addEventListener('change', () => { void selectAccount(select.value); });
  });
  [conversationSelect, chatConversationSelect].forEach((select) => {
    select.addEventListener('change', () => { void selectConversation(select.value); });
  });
  discoverReaderSessionsButton.addEventListener('click', () => { void startReaderDiscovery(); });
  saveReaderTargetsButton.addEventListener('click', () => { void saveReaderTargets(); });

  personaSelect.addEventListener('change', () => {
    if (!activeAgentState) return;
    activeAgentState.selectedPersonaPresetId = personaSelect.value;
    activeAgentState.suggestionError = '';
    activeAgentState.suggestionFeedback = '';
    renderAgentState();
  });
  replyProfileSelect.addEventListener('change', () => {
    if (!document.body.classList.contains('is-minimal')) workspaceReplyProfileId = replyProfileSelect.value || 'Quick';
    renderAgentState();
  });
  consultationProfileSelect.addEventListener('change', () => { renderAgentState(); });
  reloadPersonasButton.addEventListener('click', () => { void loadPersonaCatalog(true); });
  generateSuggestionsButton.addEventListener('click', () => { void createSuggestionBatch(); });
  cancelSuggestionButton.addEventListener('click', cancelSuggestionGeneration);
  minimalPersonaButton.addEventListener('click', () => {
    if (!activeAgentState || !personaCatalog.length) return;
    const index = personaCatalog.findIndex((item) => item.personaPresetId === activeAgentState.selectedPersonaPresetId);
    activeAgentState.selectedPersonaPresetId = personaCatalog[(index + 1 + personaCatalog.length) % personaCatalog.length].personaPresetId;
    activeAgentState.suggestionError = '';
    activeAgentState.suggestionFeedback = '';
    renderAgentState();
    void createSuggestionBatch();
  });
  minimalRefreshButton.addEventListener('click', () => { void createSuggestionBatch(); });
  [suggestionList, minimalChoices].forEach((container) => {
    container.addEventListener('click', (event) => {
      const button = event.target.closest('[data-copy-suggestion-index]');
      if (!button) return;
      void copySuggestion(Number(button.dataset.copySuggestionIndex));
    });
  });
  consultationForm.addEventListener('submit', submitConsultation);
  cancelConsultationButton.addEventListener('click', cancelConsultationTurn);
  messageArea.addEventListener('click', (event) => {
    const button = event.target.closest('[data-load-older-messages]');
    if (button && !button.disabled) void loadOlderMessages(button);
  });

  pageButtons.forEach((button) => {
    button.addEventListener('click', () => setWorkspacePage(button.dataset.page));
  });
  readerTargetSearch.addEventListener('input', renderReaderTargetList);
  document.addEventListener('visibilitychange', () => {
    if (!isPageVisible()) pauseRevisionPolling();
    else scheduleRevisionPoll(0);
  });

  expandButton.addEventListener('click', () => {
    const expanded = expandButton.getAttribute('aria-expanded') !== 'true';
    expandButton.setAttribute('aria-expanded', String(expanded));
    expandButton.innerHTML = `<span class="expand-chevron" aria-hidden="true">⌄</span>${expanded ? '收起' : '展开'}`;
    minimalDetails.hidden = !expanded;
  });

  confirmCurrentButton.addEventListener('click', () => { void confirmIdentity(false); });
  swapButton.addEventListener('click', () => { void confirmIdentity(true); });

  webAuthForm.addEventListener('submit', async (event) => {
    event.preventDefault();
    const enteredToken = webTokenInput.value.trim();
    webTokenInput.value = '';
    if (enteredToken && enteredToken !== webToken) {
      webToken = enteredToken;
      resetAgentForAuthChange();
    }
    if (!webToken) {
      setApiStatus('请粘贴 Web Bearer token 后读取。', 'unauthorized');
      webTokenInput.focus();
      return;
    }
    await loadWorkspace();
  });

  memoryCreateForm.addEventListener('submit', handleMemoryCreate);
  memoryList.addEventListener('submit', handleMemoryEdit);
  memoryList.addEventListener('click', async (event) => {
    const button = event.target.closest('[data-memory-action]');
    if (!button || button.disabled || !memoryWritable) return;
    const action = button.dataset.memoryAction;
    const memoryId = button.dataset.memoryId || button.closest('.memory-edit-form')?.dataset.memoryId;
    if (action === 'edit') {
      const item = memoryRecords.find((record) => record.memoryId === memoryId);
      if (!item) return;
      editingMemoryId = memoryId;
      renderMemoryItems(memoryRecords, currentConversation);
      setMemoryFeedback('编辑现有人工记忆；保存时会原样保留现有关联消息证据。');
      memoryList.querySelector(`[data-memory-field="text"]`)?.focus();
    } else if (action === 'cancel') {
      editingMemoryId = null;
      renderMemoryItems(memoryRecords, currentConversation, '已取消编辑，未提交更改。');
    } else if (action === 'delete') {
      const item = memoryRecords.find((record) => record.memoryId === memoryId);
      await handleMemoryDelete(item);
    }
  });
  reloadMemoriesButton.addEventListener('click', () => loadMemoryList());
  refreshSystemStatusButton.addEventListener('click', () => { void loadSystemStatus(); });

  setWorkspacePage('chat');

  clearButton.addEventListener('click', () => {
    requestVersion++;
    if (activeController) activeController.abort();
    activeController = null;
    webToken = null;
    webTokenInput.value = '';
    resetWorkspaceForNoToken();
    setApiStatus('已清除本页面内存中的 Web token。', 'idle');
    finishLoading();
    announce('本页面授权已清除。');
  });

  confirmCurrentButton.disabled = true;
  swapButton.disabled = true;
  resetWorkspaceForNoToken();

  // UI mode and identity preview are local page state only. API data is loaded only
  // after the user supplies a Web Bearer token; no token or response is persisted.
})();
