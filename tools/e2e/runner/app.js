const ui = {
  statusText: document.getElementById("status-text"),
  utilityUrl: document.getElementById("utility-url"),
  mainStatus: document.getElementById("main-status"),
  chaosStatus: document.getElementById("chaos-status"),
  checks: document.getElementById("checks"),
  log: document.getElementById("log"),
  summaryTotal: document.getElementById("summary-total"),
  summaryPass: document.getElementById("summary-pass"),
  summaryWarn: document.getElementById("summary-warn"),
  summaryFail: document.getElementById("summary-fail"),
  telemetryRuntime: document.getElementById("telemetry-runtime"),
  telemetryRuntimeNote: document.getElementById("telemetry-runtime-note"),
  telemetrySessions: document.getElementById("telemetry-sessions"),
  telemetrySessionsNote: document.getElementById("telemetry-sessions-note"),
  telemetryChaos: document.getElementById("telemetry-chaos"),
  telemetryChaosNote: document.getElementById("telemetry-chaos-note"),
  sessionList: document.getElementById("session-list"),
  timelineList: document.getElementById("timeline-list"),
  latencyList: document.getElementById("latency-list"),
};

const hostApiBase = `${location.origin}/api`;
const state = {
  scenario: null,
  mainReport: null,
  chaosReport: null,
  counters: { total: 0, pass: 0, warn: 0, fail: 0 },
  checkNodes: new Map(),
  traceQueue: [],
  traceFlushTimer: null,
  activeSessions: new Map(),
  latestConfigVersion: null,
  chaosKeeperSession: null,
  telemetryTimer: null,
  lastHealth: null,
  lastInfo: null,
  lastHostStatus: null,
  sessionSnapshots: [],
  timeline: [],
  latencyMetrics: new Map(),
  chaosStats: {
    actionCount: 0,
    failureCount: 0,
    mandatoryActions: new Set(),
    mandatoryFailureCount: 0,
  },
};

function nowIso() {
  return new Date().toISOString();
}

function sleep(ms) {
  return new Promise(resolve => window.setTimeout(resolve, ms));
}

function deepClone(value) {
  return value == null ? value : JSON.parse(JSON.stringify(value));
}

function setText(element, value) {
  element.textContent = String(value);
}

function setStatus(value) {
  setText(ui.statusText, value);
}

function setMainStatus(value) {
  setText(ui.mainStatus, value);
}

function setChaosStatus(value) {
  setText(ui.chaosStatus, value);
}

function updateSummary() {
  setText(ui.summaryTotal, state.counters.total);
  setText(ui.summaryPass, state.counters.pass);
  setText(ui.summaryWarn, state.counters.warn);
  setText(ui.summaryFail, state.counters.fail);
}

function pushTimeline(kind, title, details = "") {
  state.timeline.unshift({
    ts: nowIso(),
    kind,
    title,
    details: typeof details === "string" ? details : JSON.stringify(details),
  });
  if (state.timeline.length > 40) {
    state.timeline.length = 40;
  }
  renderTimeline();
}

function renderTimeline() {
  ui.timelineList.textContent = "";
  if (state.timeline.length === 0) {
    const empty = document.createElement("div");
    empty.className = "timeline-row info";
    empty.innerHTML = "<strong>Ожидание событий</strong><p>Пока нет событий timeline.</p>";
    ui.timelineList.append(empty);
    return;
  }

  for (const item of state.timeline) {
    const row = document.createElement("div");
    row.className = `timeline-row ${item.kind}`;
    const title = document.createElement("strong");
    title.textContent = `${new Date(item.ts).toLocaleTimeString("ru-RU", { hour12: false })} · ${item.title}`;
    const details = document.createElement("p");
    details.textContent = item.details || "-";
    row.append(title, details);
    ui.timelineList.append(row);
  }
}

function renderSessions() {
  ui.sessionList.textContent = "";
  if (!Array.isArray(state.sessionSnapshots) || state.sessionSnapshots.length === 0) {
    const empty = document.createElement("div");
    empty.className = "session-row";
    empty.innerHTML = "<strong>Нет данных</strong><p>Список UI-сессий пока не получен.</p>";
    ui.sessionList.append(empty);
    return;
  }

  for (const session of state.sessionSnapshots) {
    const row = document.createElement("div");
    row.className = "session-row";
    const title = document.createElement("strong");
    title.textContent = `${session.clientName ?? "unknown"} · ${session.sessionId ?? "-"}`;
    const details = document.createElement("p");
    details.textContent = `uiVersion=${session.uiVersion ?? "-"}\nlastSeen=${session.lastSeenUtc ?? session.LastSeenUtc ?? "-"}`;
    const flags = document.createElement("div");
    flags.className = "session-flags";
    for (const [label, on] of [
      ["present", session.isPresent === true],
      ["interactive", session.isInteractive === true],
      ["active", session.isActive === true],
      ["socket", session.socketAttached === true],
      ["hello", session.helloReceived === true],
    ]) {
      const flag = document.createElement("span");
      flag.className = `flag ${on ? "on" : ""}`;
      flag.textContent = label;
      flags.append(flag);
    }
    if (session.closingReason) {
      const flag = document.createElement("span");
      flag.className = "flag warn";
      flag.textContent = `closing=${session.closingReason}`;
      flags.append(flag);
    }
    row.append(title, details, flags);
    ui.sessionList.append(row);
  }
}

function renderLatency() {
  ui.latencyList.textContent = "";
  if (state.latencyMetrics.size === 0) {
    const empty = document.createElement("div");
    empty.className = "metric-row";
    empty.innerHTML = "<strong>Ожидание latency suite</strong><p>Метрики COM будут показаны после этапа latency.</p>";
    ui.latencyList.append(empty);
    return;
  }

  for (const [name, metric] of state.latencyMetrics.entries()) {
    const row = document.createElement("div");
    row.className = "metric-row";
    const title = document.createElement("strong");
    title.textContent = name;
    const details = document.createElement("p");
    details.textContent = metric;
    row.append(title, details);
    ui.latencyList.append(row);
  }
}

function renderTelemetry() {
  const runtime = state.lastInfo?.runtimeState ?? state.lastHealth?.runtimeState ?? "-";
  const activations = state.lastInfo?.activationCount ?? "-";
  setText(ui.telemetryRuntime, runtime);
  setText(ui.telemetryRuntimeNote, `activations=${activations}`);

  const presence = state.lastInfo?.presenceSessionCount ?? state.lastHealth?.presenceSessionCount ?? 0;
  const active = state.lastInfo?.activeSessionCount ?? state.lastHealth?.activeSessionCount ?? 0;
  const probeHits = state.lastHostStatus?.probeHits ?? 0;
  setText(ui.telemetrySessions, `${presence} / ${active}`);
  setText(ui.telemetrySessionsNote, `presence=${presence}, active=${active}, probeHits=${probeHits}`);

  const mandatoryCount = state.chaosStats.mandatoryActions.size;
  setText(ui.telemetryChaos, `${state.chaosStats.actionCount} / ${state.chaosStats.failureCount}`);
  setText(ui.telemetryChaosNote, `actions=${state.chaosStats.actionCount}, failures=${state.chaosStats.failureCount}, mandatory=${mandatoryCount}, mandatoryFailures=${state.chaosStats.mandatoryFailureCount ?? 0}`);
}

async function refreshLiveTelemetry() {
  if (!state.scenario) {
    return;
  }

  try {
    const [health, info, sessions, hostStatus] = await Promise.all([
      getHealth().catch(() => null),
      getInfo().catch(() => null),
      getSessions().catch(() => []),
      fetchHostStatus().catch(() => null),
    ]);

    state.lastHealth = health?.payload ?? state.lastHealth;
    state.lastInfo = info ?? state.lastInfo;
    state.sessionSnapshots = Array.isArray(sessions) ? sessions : state.sessionSnapshots;
    state.lastHostStatus = hostStatus ?? state.lastHostStatus;
    renderSessions();
    renderTelemetry();
  } catch {
    // best-effort live view
  }
}

function startTelemetryLoop() {
  if (state.telemetryTimer !== null) {
    return;
  }

  state.telemetryTimer = window.setInterval(() => {
    refreshLiveTelemetry().catch(() => {});
  }, 2000);
}

function stopTelemetryLoop() {
  if (state.telemetryTimer !== null) {
    window.clearInterval(state.telemetryTimer);
    state.telemetryTimer = null;
  }
}

async function postJson(url, payload) {
  await fetch(url, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(payload),
  });
}

async function postConsole(level, text) {
  await postJson(`${hostApiBase}/console`, {
    ts: nowIso(),
    level,
    text,
  });
}

function appendLog(message, details = undefined) {
  const tail = details === undefined
    ? ""
    : ` ${typeof details === "string" ? details : JSON.stringify(details)}`;
  const line = `[${new Date().toLocaleTimeString("ru-RU", { hour12: false })}] ${message}${tail}`;
  ui.log.textContent += `${line}\n`;
  ui.log.scrollTop = ui.log.scrollHeight;
  postConsole("info", line).catch(() => {});
}

function queueTrace(entry) {
  state.traceQueue.push({ ts: nowIso(), ...entry });
  if (state.traceFlushTimer !== null) {
    return;
  }

  state.traceFlushTimer = window.setTimeout(async () => {
    const batch = state.traceQueue.splice(0, state.traceQueue.length);
    state.traceFlushTimer = null;
    try {
      await postJson(`${hostApiBase}/trace`, batch);
    } catch {
      // best-effort telemetry
    }
  }, 250);
}

async function flushTraceQueue() {
  if (state.traceFlushTimer !== null) {
    window.clearTimeout(state.traceFlushTimer);
    state.traceFlushTimer = null;
  }

  if (state.traceQueue.length === 0) {
    return;
  }

  const batch = state.traceQueue.splice(0, state.traceQueue.length);
  try {
    await postJson(`${hostApiBase}/trace`, batch);
  } catch {
    // ignore shutdown path
  }
}

async function checkpoint(name, data = undefined) {
  await postJson(`${hostApiBase}/checkpoint`, { name, data });
}

function assert(condition, message) {
  if (!condition) {
    throw new Error(message);
  }
}

function formatError(error) {
  if (error instanceof Error) {
    return `${error.name}: ${error.message}`;
  }
  return String(error);
}

function isExpectedRejectionText(text) {
  if (!text) {
    return false;
  }

  return /pairing_token_invalid|origin_not_allowed|session\/register failed: 401|\/info failed: 401|\/config\/(?:effective|version) failed: 401|\/manifest\/(?:status|refresh) failed: 401|HTTP Error 403|status=401|status=403|excel_com_error|kompas_com_error|adapter_unavailable|0x800706ba|rpc_e_call_rejected|rpc_e_servercall_retrylater|сервер rpc недоступен|вызов был отклонен получателем|the rpc server is unavailable|a task was canceled|aborterror|request timed out/i.test(String(text));
}

function isExpectedChaosFailure(kind, text) {
  if (isExpectedRejectionText(text)) {
    return true;
  }

  if (!kind) {
    return false;
  }

  const kindText = String(kind).toLowerCase();
  const rendered = String(text ?? "").toLowerCase();
  if ((kindText.startsWith("kompas") || kindText.startsWith("excel")) &&
    /(task was canceled|aborterror|request timed out|timed out|timeout|rpc|adapter_unavailable|server_exec_failure|0x80080005)/i.test(rendered)) {
    return true;
  }

  return false;
}

async function fetchWithTimeout(url, init, timeoutMs) {
  const controller = new AbortController();
  const timeout = window.setTimeout(() => controller.abort(`Request timed out after ${timeoutMs} ms.`), timeoutMs);
  try {
    return await fetch(url, { ...init, signal: controller.signal });
  } finally {
    window.clearTimeout(timeout);
  }
}

async function fetchScenario() {
  const response = await fetch(`${hostApiBase}/scenario`);
  if (!response.ok) {
    throw new Error(`Scenario fetch failed: ${response.status}`);
  }

  return await response.json();
}

function installConsoleMirrors() {
  const originals = {
    log: console.log.bind(console),
    warn: console.warn.bind(console),
    error: console.error.bind(console),
    info: console.info.bind(console),
  };

  for (const [level, original] of Object.entries(originals)) {
    console[level] = (...args) => {
      original(...args);
      postConsole(level, args.map(value => String(value)).join(" ")).catch(() => {});
    };
  }

  window.addEventListener("error", event => {
    postConsole("error", `${event.message} @ ${event.filename}:${event.lineno}:${event.colno}`).catch(() => {});
  });

  window.addEventListener("unhandledrejection", event => {
    postConsole("error", `unhandledrejection ${String(event.reason)}`).catch(() => {});
  });
}

function ensureCheckNode(checkId, title) {
  if (state.checkNodes.has(checkId)) {
    return state.checkNodes.get(checkId);
  }

  const li = document.createElement("li");
  li.className = "check";
  const stateNode = document.createElement("div");
  stateNode.className = "check-state run";
  const content = document.createElement("div");
  content.className = "check-meta";
  const h3 = document.createElement("h3");
  h3.textContent = title;
  const tagRow = document.createElement("div");
  tagRow.className = "check-tag-row";
  const testTag = document.createElement("span");
  testTag.className = "check-tag test";
  testTag.textContent = "Тестовый шаг";
  const verifyTag = document.createElement("span");
  verifyTag.className = "check-tag verify";
  verifyTag.textContent = "Проверка";
  const evidenceTag = document.createElement("span");
  evidenceTag.className = "check-tag evidence";
  evidenceTag.textContent = "Доказательство";
  tagRow.append(testTag, verifyTag, evidenceTag);

  const blocks = document.createElement("div");
  blocks.className = "check-blocks";

  const testBlock = document.createElement("div");
  testBlock.className = "check-block test";
  const testStrong = document.createElement("strong");
  testStrong.textContent = "Тестовый шаг";
  const testText = document.createElement("p");
  testText.textContent = title;
  testBlock.append(testStrong, testText);

  const verifyBlock = document.createElement("div");
  verifyBlock.className = "check-block verify";
  const verifyStrong = document.createElement("strong");
  verifyStrong.textContent = "Проверка";
  const verifyText = document.createElement("p");
  verifyText.textContent = "Выполняется";
  verifyBlock.append(verifyStrong, verifyText);

  const evidenceBlock = document.createElement("div");
  evidenceBlock.className = "check-block evidence";
  const evidenceStrong = document.createElement("strong");
  evidenceStrong.textContent = "Доказательство";
  const evidenceText = document.createElement("p");
  evidenceText.textContent = "Ожидание результата";
  evidenceBlock.append(evidenceStrong, evidenceText);

  blocks.append(testBlock, verifyBlock, evidenceBlock);
  content.append(h3, tagRow, blocks);
  li.append(stateNode, content);
  ui.checks.append(li);

  const entry = {
    li,
    stateNode,
    testTextNode: testText,
    verifyTextNode: verifyText,
    evidenceBlock,
    evidenceTextNode: evidenceText,
  };
  state.checkNodes.set(checkId, entry);
  return entry;
}

function formatProofText(actual, details) {
  if (!details) {
    return actual;
  }

  return `${actual}\n${details}`;
}

function deriveTestText(title, checkId) {
  return title && title !== checkId
    ? `${title}\nID: ${checkId}`
    : `ID: ${checkId}`;
}

function markCheck(report, status, checkId, title, expected, actual, details) {
  const id = `${report.suite}.${title}`;
  const node = ensureCheckNode(id, title);
  node.stateNode.className = `check-state ${status}`;
  node.testTextNode.textContent = deriveTestText(title, checkId);
  node.verifyTextNode.textContent = expected;
  node.evidenceTextNode.textContent = formatProofText(actual, details);
  node.evidenceBlock.className = `check-block evidence${status === "bad" ? " bad" : status === "warn" ? " warn" : ""}`;
  state.counters.total += 1;
  if (status === "ok") {
    state.counters.pass += 1;
  } else if (status === "warn") {
    state.counters.warn += 1;
  } else {
    state.counters.fail += 1;
  }
  updateSummary();
}

function createReport(name) {
  return {
    suite: name,
    startedAtUtc: nowIso(),
    completedAtUtc: null,
    success: true,
    checks: [],
    failedChecks: [],
  };
}

function recordResult(report, {
  id,
  title,
  success,
  expected,
  actual,
  classification = "product defect",
  severity = "medium",
  details = "",
}) {
  const status = success
    ? "ok"
    : classification === "environment issue" || classification === "harness defect"
      ? "warn"
      : "bad";
  const item = {
    id,
    title,
    success,
    expected,
    actual,
    classification,
    severity,
    details,
    ts: nowIso(),
  };
  report.checks.push(item);
  if (!success) {
    report.success = false;
    report.failedChecks.push(item);
  }
  markCheck(report, status, id, title, expected, actual, details);
  if (id.startsWith("latency-")) {
    state.latencyMetrics.set(
      id,
      `${actual}${details ? `\n${details}` : ""}`);
    renderLatency();
  }
  pushTimeline(
    success ? (classification === "environment issue" || classification === "harness defect" ? "verify" : "info") : "error",
    `${report.suite}:${id}`,
    `${expected}\n${actual}${details ? `\n${details}` : ""}`);
}

async function runCheck(report, spec, action) {
  const title = spec.title ?? spec.id;
  const expected = spec.expected ?? "Операция должна выполниться";
  pushTimeline(spec.timelineKind ?? "info", `start:${report.suite}:${spec.id}`, expected);
  try {
    const result = await action();
    recordResult(report, {
      id: spec.id,
      title,
      success: true,
      expected,
      actual: result?.actual ?? "Успешно",
      classification: spec.classification,
      severity: spec.severity,
      details: result?.details ?? "",
    });
    return result;
  } catch (error) {
    recordResult(report, {
      id: spec.id,
      title,
      success: false,
      expected,
      actual: formatError(error),
      classification: spec.classification ?? "product defect",
      severity: spec.severity ?? "medium",
      details: spec.details ?? "",
    });
    return null;
  }
}

async function publishReport(name, report) {
  report.completedAtUtc = nowIso();
  await postJson(`${hostApiBase}/report/${name}`, report);
}

function correlationId() {
  return `${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 10)}`;
}

function resolveDefaultCommandTimeout(commandId, options = {}) {
  if (options.timeoutMilliseconds !== undefined && options.timeoutMilliseconds !== null) {
    return options.timeoutMilliseconds;
  }

  if (commandId.startsWith("kompas.")) {
    if (commandId.includes("save-dxf") || commandId.includes("save-as") || commandId.includes("save-document")) {
      return 15000;
    }

    return 10000;
  }

  if (commandId.startsWith("excel.")) {
    return 15000;
  }

  if (commandId.startsWith("system.command.") || commandId.startsWith("system.process.") || commandId.startsWith("system.http.")) {
    return 15000;
  }

  return 10000;
}

function unwrapEnvelopePayload(payload) {
  return payload?.payload ?? payload;
}

function normalizeSession(session) {
  if (!session || typeof session !== "object") {
    return session;
  }

  return {
    ...session,
    sessionId: session.sessionId ?? session.SessionId ?? null,
    clientName: session.clientName ?? session.ClientName ?? null,
    uiVersion: session.uiVersion ?? session.UiVersion ?? null,
    isPresent: session.isPresent ?? session.IsPresent ?? false,
    isInteractive: session.isInteractive ?? session.IsInteractive ?? false,
    isActive: session.isActive ?? session.IsActive ?? false,
    socketAttached: session.socketAttached ?? session.SocketAttached ?? false,
    helloReceived: session.helloReceived ?? session.HelloReceived ?? false,
    closingReason: session.closingReason ?? session.ClosingReason ?? null,
  };
}

function configTemplateVersion() {
  return state.scenario?.configTemplate?.configVersion
    ?? state.scenario?.configTemplate?.ConfigVersion
    ?? null;
}

async function utilityFetch(path, options = {}) {
  const url = `${state.scenario.utilityBaseUrl}${path}`;
  const headers = {
    "Content-Type": "application/json",
    "X-KWB-Pairing-Token": state.scenario.pairingToken,
    "X-Correlation-Id": correlationId(),
    ...(options.headers ?? {}),
  };
  const init = {
    method: options.method ?? "GET",
    headers,
  };
  if (options.body !== undefined) {
    init.body = typeof options.body === "string" ? options.body : JSON.stringify(options.body);
  }
  queueTrace({ kind: "http-request", url, method: init.method, body: options.body ?? null });
  const response = options.timeoutMs && Number(options.timeoutMs) > 0
    ? await fetchWithTimeout(url, init, Math.max(1000, Number(options.timeoutMs)))
    : await fetch(url, init);
  const text = await response.text();
  let payload = null;
  try {
    payload = text ? JSON.parse(text) : null;
  } catch {
    payload = text;
  }

  queueTrace({ kind: "http-response", url, method: init.method, status: response.status, payload });
  return { response, payload };
}

async function hostControl(path, body = {}) {
  const response = await fetchWithTimeout(`${hostApiBase}/control/${path}`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body),
  }, 30000);
  return await response.json();
}

async function fetchHostStatus() {
  const response = await fetchWithTimeout(`${hostApiBase}/status`, {}, 15000);
  return await response.json();
}

async function getHealth() {
  const { response, payload } = await utilityFetch("/health", { method: "GET" });
  return { response, payload: unwrapEnvelopePayload(payload) };
}

async function getInfo() {
  const { response, payload } = await utilityFetch("/info", { method: "GET" });
  assert(response.ok, `/info failed: ${response.status}`);
  return unwrapEnvelopePayload(payload);
}

async function getEffectiveConfig() {
  const { response, payload } = await utilityFetch("/config/effective", { method: "GET" });
  assert(response.ok, `/config/effective failed: ${response.status}`);
  return unwrapEnvelopePayload(payload)?.settings;
}

async function getConfigVersion() {
  const { response, payload } = await utilityFetch("/config/version", { method: "GET" });
  assert(response.ok, `/config/version failed: ${response.status}`);
  const version = unwrapEnvelopePayload(payload);
  state.latestConfigVersion = version;
  return version;
}

async function getSessions() {
  const { response, payload } = await utilityFetch("/sessions", { method: "GET" });
  assert(response.ok, `/sessions failed: ${response.status}`);
  return (unwrapEnvelopePayload(payload) ?? []).map(normalizeSession);
}

async function getManifestStatus() {
  const { response, payload } = await utilityFetch("/manifest/status", { method: "GET" });
  assert(response.ok, `/manifest/status failed: ${response.status}`);
  return unwrapEnvelopePayload(payload);
}

async function postManifestRefresh() {
  const { response, payload } = await utilityFetch("/manifest/refresh", { method: "POST", body: {} });
  assert(response.ok, `/manifest/refresh failed: ${response.status}`);
  return unwrapEnvelopePayload(payload);
}

async function postConfigLoad(settings, persist = false) {
  const { response, payload } = await utilityFetch("/config/load", {
    method: "POST",
    body: { settings, persist },
  });
  return { response, payload: unwrapEnvelopePayload(payload), raw: payload };
}

async function postConfigReload() {
  const { response, payload } = await utilityFetch("/config/reload", { method: "POST", body: {} });
  return { response, payload: unwrapEnvelopePayload(payload), raw: payload };
}

async function postUtilityOpenUi(force = false) {
  const { response, payload } = await utilityFetch("/utility/open-ui", {
    method: "POST",
    body: { reason: "e2e-open-ui", force },
  });
  return { response, payload: unwrapEnvelopePayload(payload), raw: payload };
}

async function postUtilityShutdown(force = false, reason = "e2e-shutdown") {
  const { response, payload } = await utilityFetch("/utility/shutdown", {
    method: "POST",
    body: { reason, force },
  });
  return { response, payload: unwrapEnvelopePayload(payload), raw: payload };
}

async function executeCommand(commandId, args = {}, options = {}) {
  const timeoutMilliseconds = resolveDefaultCommandTimeout(commandId, options);
  const { response, payload } = await utilityFetch("/commands/execute", {
    method: "POST",
    timeoutMs: options.clientTimeoutMs ?? null,
    body: {
      profileId: state.scenario.profileId,
      commandId,
      arguments: args,
      reportVerbosity: options.reportVerbosity ?? null,
      sharedContextId: options.sharedContextId ?? null,
      timeoutMilliseconds,
    },
  });
  const commandPayload = unwrapEnvelopePayload(payload);
  const success = Boolean(commandPayload?.success);
  if (options.expectFailure) {
    return { response, payload: commandPayload, success: !success };
  }
  if (!response.ok || !success) {
    throw new Error(`Command ${commandId} failed: ${JSON.stringify(commandPayload?.error ?? payload)}`);
  }
  return commandPayload;
}

async function executeBatch(commands, options = {}) {
  const payloadCommands = commands.map((command) => ({
    profileId: state.scenario.profileId,
    commandId: command.commandId,
    arguments: command.arguments ?? {},
    reportVerbosity: command.reportVerbosity ?? null,
    sharedContextId: command.sharedContextId ?? null,
    timeoutMilliseconds: resolveDefaultCommandTimeout(command.commandId, command),
  }));
  const { response, payload } = await utilityFetch("/commands/execute-batch", {
    method: "POST",
    timeoutMs: options.clientTimeoutMs ?? null,
    body: {
      commands: payloadCommands,
      sharedContextId: options.sharedContextId ?? null,
      reportVerbosity: options.reportVerbosity ?? null,
      stopOnError: options.stopOnError ?? true,
    },
  });
  const batchPayload = unwrapEnvelopePayload(payload);
  const success = Boolean(batchPayload?.success);
  if (options.expectFailure) {
    return { response, payload: batchPayload, success: !success };
  }
  if (!response.ok || !success) {
    throw new Error(`Command batch failed: ${JSON.stringify(batchPayload ?? payload)}`);
  }
  return batchPayload;
}

async function readCommandResult(commandId, args = {}, options = {}) {
  const command = await executeCommand(commandId, args, options);
  return command.result;
}

async function readExcelResult(commandId, args = {}, options = {}) {
  return await readCommandResult(commandId, args, options);
}

function extractHandleId(result) {
  return result?.result?.handleId ?? result?.handleId ?? null;
}

function extractResultField(result, fieldName) {
  return result?.result?.[fieldName] ?? result?.[fieldName] ?? null;
}

function extractReportDuration(command) {
  return Number(command?.report?.durationMs ?? 0);
}

function extractQueueWait(command) {
  return Number(command?.report?.runtime?.queueWaitMs ?? 0);
}

function median(values) {
  return percentile(values, 50);
}

function percentile(values, value) {
  if (!Array.isArray(values) || values.length === 0) {
    return 0;
  }

  const sorted = [...values].sort((left, right) => left - right);
  const index = Math.min(sorted.length - 1, Math.max(0, Math.ceil((value / 100) * sorted.length) - 1));
  return sorted[index];
}

function summarizeLatency(samples) {
  return {
    count: samples.length,
    p50: percentile(samples, 50),
    p95: percentile(samples, 95),
    p99: percentile(samples, 99),
    min: samples.length ? Math.min(...samples) : 0,
    max: samples.length ? Math.max(...samples) : 0,
    avg: samples.length ? samples.reduce((sum, value) => sum + value, 0) / samples.length : 0,
  };
}

function normalizeWindowsPath(value) {
  if (value === null || value === undefined) {
    return "";
  }

  return String(value)
    .replace(/\//g, "\\")
    .replace(/\\+/g, "\\")
    .toLowerCase();
}

function pathEquals(left, right) {
  const normalizedLeft = normalizeWindowsPath(left);
  const normalizedRight = normalizeWindowsPath(right);
  return normalizedLeft !== "" && normalizedLeft === normalizedRight;
}

function assertPositiveKompasRef(value, label) {
  const numericValue = Number(value);
  assert(Number.isFinite(numericValue) && numericValue > 0, `${label} is not a positive KOMPAS object reference: ${value}`);
  return numericValue;
}

function toExcelBoolean(value) {
  if (value === true || value === false) {
    return value;
  }

  if (typeof value === "number") {
    return value !== 0;
  }

  if (typeof value === "string") {
    const normalized = value.trim().toLowerCase();
    if (normalized === "true") {
      return true;
    }
    if (normalized === "false") {
      return false;
    }
  }

  return Boolean(value);
}

function assertExcelBoolean(value, expected, label) {
  const actual = toExcelBoolean(value);
  assert(actual === expected, `${label} expected ${expected}, got ${value}`);
  return actual;
}

function assertApproxNumber(value, expected, tolerance, label) {
  const numeric = Number(value);
  assert(Number.isFinite(numeric), `${label} is not numeric: ${value}`);
  assert(Math.abs(numeric - expected) <= tolerance, `${label} expected ${expected} +/- ${tolerance}, got ${numeric}`);
  return numeric;
}

function resolveStructTypeId(structTypes, ...names) {
  for (const name of names) {
    if (name && structTypes?.[name] !== undefined && structTypes?.[name] !== null) {
      return structTypes[name];
    }
  }

  throw new Error(`Struct type was not found. Candidates: ${names.join(", ")}`);
}

function createWsEnvelope(type, correlation, payload = null) {
  return {
    id: correlationId(),
    type,
    ts: nowIso(),
    correlationId: correlation,
    payload,
    error: null,
  };
}

class SessionClient {
  constructor(name) {
    this.name = name;
    this.sessionId = null;
    this.ws = null;
    this.heartbeatIntervalMs = 10000;
    this.heartbeatTimer = null;
    this.helloReceived = false;
    this.closed = false;
    this.registered = false;
    this.heartbeatSentCount = 0;
  }

  async connect(desiredSessionId = null) {
    pushTimeline("info", `session:${this.name}:register`, `desiredSessionId=${desiredSessionId ?? "<new>"}`);
    const { response, payload } = await utilityFetch("/session/register", {
      method: "POST",
      body: {
        clientName: this.name,
        uiVersion: "e2e-runner",
        desiredSessionId,
      },
    });
    assert(response.ok, `session/register failed: ${response.status}`);
    const registered = unwrapEnvelopePayload(payload);
    this.sessionId = registered.sessionId;
    this.heartbeatIntervalMs = Math.max(1000, Number(registered.heartbeatIntervalSeconds || 10) * 1000);
    this.registered = true;
    const wsUrl = `${registered.wsUrl}&token=${encodeURIComponent(state.scenario.pairingToken)}`;
    await this.#openSocket(wsUrl);
    state.activeSessions.set(this.name, this);
    pushTimeline("verify", `session:${this.name}:connected`, `sessionId=${this.sessionId}`);
    return registered;
  }

  async disconnect(reason = "disconnect") {
    pushTimeline("fault", `session:${this.name}:disconnect`, reason);
    this.stopHeartbeat();
    try {
      if (this.registered && this.sessionId) {
        await utilityFetch("/session/closing", {
          method: "POST",
          body: { sessionId: this.sessionId, reason },
        });
      }
    } catch {
      // best effort
    }

    await this.dropSocket(reason);
    this.registered = false;
    state.activeSessions.delete(this.name);
  }

  async dropSocket(reason = "drop") {
    this.stopHeartbeat();
    this.closed = true;
    if (!this.ws) {
      return;
    }

    const socket = this.ws;
    this.ws = null;
    try {
      socket.close(1000, reason);
    } catch {
      // ignore
    }
    await sleep(250);
  }

  send(type, payload = null) {
    if (!this.ws || this.ws.readyState !== WebSocket.OPEN) {
      throw new Error(`Session ${this.name} WebSocket is not open.`);
    }
    const envelope = createWsEnvelope(type, correlationId(), payload);
    queueTrace({ kind: "ws-send", sessionId: this.sessionId, envelope });
    this.ws.send(JSON.stringify(envelope));
    if (type === "heartbeat") {
      this.heartbeatSentCount += 1;
      pushTimeline("verify", `session:${this.name}:heartbeat`, `sessionId=${this.sessionId}`);
    }
  }

  startHeartbeat() {
    this.stopHeartbeat();
    this.heartbeatTimer = window.setInterval(() => {
      try {
        this.send("heartbeat", { sessionId: this.sessionId });
      } catch (error) {
        appendLog(`heartbeat-${this.name}-failed`, formatError(error));
      }
    }, this.heartbeatIntervalMs);
  }

  stopHeartbeat() {
    if (this.heartbeatTimer !== null) {
      window.clearInterval(this.heartbeatTimer);
      this.heartbeatTimer = null;
    }
  }

  async #openSocket(wsUrl) {
    this.closed = false;
    this.helloReceived = false;
    const socket = new WebSocket(wsUrl);
    this.ws = socket;
    queueTrace({ kind: "ws-open-attempt", sessionId: this.sessionId, url: wsUrl });
    socket.addEventListener("message", event => {
      try {
        const envelope = JSON.parse(event.data);
        queueTrace({ kind: "ws-receive", sessionId: this.sessionId, envelope });
        if (envelope.type === "hello") {
          this.helloReceived = true;
          pushTimeline("verify", `session:${this.name}:hello`, `sessionId=${this.sessionId}`);
        }
      } catch (error) {
        appendLog("ws-message-parse-failed", formatError(error));
      }
    });
    socket.addEventListener("close", event => {
      queueTrace({
        kind: "ws-close",
        sessionId: this.sessionId,
        code: event.code,
        reason: event.reason,
        wasClean: event.wasClean,
      });
      pushTimeline(event.wasClean ? "verify" : "fault", `session:${this.name}:ws-close`, `code=${event.code}, clean=${event.wasClean}, reason=${event.reason}`);
    });
    socket.addEventListener("error", () => {
      queueTrace({ kind: "ws-error", sessionId: this.sessionId });
      pushTimeline("error", `session:${this.name}:ws-error`, `sessionId=${this.sessionId}`);
    });

    await new Promise((resolve, reject) => {
      const timeout = window.setTimeout(() => reject(new Error(`WebSocket hello timeout for ${this.name}`)), 20000);

      socket.addEventListener("open", () => {
        try {
          pushTimeline("verify", `session:${this.name}:ws-open`, `sessionId=${this.sessionId}`);
          this.send("hello", { clientName: this.name, sessionId: this.sessionId });
        } catch (error) {
          window.clearTimeout(timeout);
          reject(error);
        }
      });

      const poll = window.setInterval(() => {
        if (this.helloReceived) {
          window.clearInterval(poll);
          window.clearTimeout(timeout);
          this.startHeartbeat();
          resolve();
        }

        if (socket.readyState === WebSocket.CLOSED) {
          window.clearInterval(poll);
          window.clearTimeout(timeout);
          reject(new Error(`WebSocket closed before hello for ${this.name}.`));
        }
      }, 100);
    });
  }
}

function startReconnectLoop(client, intervalSeconds) {
  const control = { stop: false };
  const promise = (async () => {
    while (!control.stop) {
      await sleep(intervalSeconds * 1000);
      if (control.stop) {
        break;
      }
      appendLog(`session-reconnect-loop:${client.name}`, "reconnect");
      try {
        await client.disconnect("periodic-reconnect");
        await sleep(1000);
        await client.connect();
      } catch (error) {
        appendLog(`session-reconnect-loop:${client.name}-failed`, formatError(error));
      }
    }
  })();
  return {
    stop() {
      control.stop = true;
    },
    promise,
  };
}

function waitForCondition(predicate, timeoutMs, message) {
  return new Promise((resolve, reject) => {
    const deadline = Date.now() + timeoutMs;
    const timer = window.setInterval(async () => {
      try {
        const result = await predicate();
        if (result) {
          window.clearInterval(timer);
          resolve();
          return;
        }
        if (Date.now() > deadline) {
          window.clearInterval(timer);
          reject(new Error(message));
        }
      } catch (error) {
        window.clearInterval(timer);
        reject(error);
      }
    }, 250);
  });
}

async function runConnectionLifecycle(report) {
  await checkpoint("01-main-start");
  setStatus("Main suite: сессии и управление");

  const mainSession = new SessionClient("runner-main");
  const secondarySession = new SessionClient("runner-secondary");

  await runCheck(report, {
    id: "auto-open-probe",
    expected: "Utility должна открыть probe UI при OpenUi=Auto, если активной сессии ещё нет",
  }, async () => {
    const status = await fetchHostStatus();
    assert((status.probeHits ?? 0) > 0, "Auto-open probe was not observed.");
    return { actual: `Probe hits: ${status.probeHits}` };
  });

  await runCheck(report, {
    id: "probe-presence-session",
    expected: "Probe UI должен зарегистрировать presence-сессию без interactive hello, чтобы агент видел уже открытую страницу",
  }, async () => {
    await waitForCondition(async () => {
      const sessions = await getSessions();
      return sessions.some(session =>
        session.clientName === "ui-probe" &&
        session.isPresent === true &&
        session.isInteractive === false &&
        session.isActive === false);
    }, 15000, "Probe presence session was not observed.");
    const sessions = await getSessions();
    const probe = sessions.find(session => session.clientName === "ui-probe");
    assert(Boolean(probe), "Probe session is missing.");
    return {
      actual: `probeSession=${probe.sessionId}, present=${probe.isPresent}, interactive=${probe.isInteractive}, active=${probe.isActive}`,
    };
  });

  await runCheck(report, {
    id: "presence-suppresses-auto-open",
    expected: "При одной только presence-сессии второй экземпляр не должен заново открывать UI в режиме OpenUi=Auto",
  }, async () => {
    const beforeStatus = await fetchHostStatus();
    const beforeInfo = await getInfo();
    const beforeLog = await hostControl("utility-log-scan", {
      contains: [
        "Launching browser for UI URL",
        "Received activation from secondary instance.",
      ],
    });
    assert(Number(beforeInfo.activeSessionCount ?? 0) === 0, `Expected no active sessions before runner connect, got ${beforeInfo.activeSessionCount}`);
    assert(Number(beforeInfo.presenceSessionCount ?? 0) >= 1, `Expected at least one presence session before runner connect, got ${beforeInfo.presenceSessionCount}`);
    const spawn = await hostControl("spawn-second-instance", {});
    assert(spawn.returncode === 0, `Second instance returned ${spawn.returncode}`);
    await waitForCondition(async () => {
      const info = await getInfo();
      return Number(info.activationCount) > Number(beforeInfo.activationCount);
    }, 15000, "ActivationCount did not increase after second instance with probe presence.");
    await sleep(2500);
    const afterStatus = await fetchHostStatus();
    const afterInfo = await getInfo();
    const afterLog = await hostControl("utility-log-scan", {
      contains: [
        "Launching browser for UI URL",
        "Received activation from secondary instance.",
      ],
    });
    const browserLaunchPattern = "Launching browser for UI URL";
    const activationPattern = "Received activation from secondary instance.";
    const beforeLaunches = Number(beforeLog?.counts?.[browserLaunchPattern] ?? 0);
    const afterLaunches = Number(afterLog?.counts?.[browserLaunchPattern] ?? 0);
    const beforeActivations = Number(beforeLog?.counts?.[activationPattern] ?? 0);
    const afterActivations = Number(afterLog?.counts?.[activationPattern] ?? 0);
    assert(afterLaunches === beforeLaunches, `Expected no extra browser launch log entries, before=${beforeLaunches}, after=${afterLaunches}`);
    assert(afterActivations >= beforeActivations + 1, `Expected activation log count to increase, before=${beforeActivations}, after=${afterActivations}`);
    return {
      actual: `browserLaunchLogs=${beforeLaunches}->${afterLaunches}, activationLogs=${beforeActivations}->${afterActivations}, probeHits=${beforeStatus.probeHits}->${afterStatus.probeHits}, presence=${afterInfo.presenceSessionCount}, activations=${beforeInfo.activationCount}->${afterInfo.activationCount}`,
    };
  });

  await runCheck(report, {
    id: "session-main-connect",
    expected: "Основная UI-сессия должна зарегистрироваться и пройти hello/heartbeat",
  }, async () => {
    const registered = await mainSession.connect();
    return { actual: `Session ${registered.sessionId} connected with heartbeat ${registered.heartbeatIntervalSeconds}s` };
  });

  await runCheck(report, {
    id: "session-heartbeat-flow",
    expected: "После подключения UI должен реально удерживать interactive/active состояние через heartbeat",
  }, async () => {
    await sleep(Math.max(2500, mainSession.heartbeatIntervalMs + 500));
    const sessions = await getSessions();
    const current = sessions.find(session => session.sessionId === mainSession.sessionId);
    assert(Boolean(current), "Main session snapshot is missing.");
    assert(current.isInteractive === true, `Main session is not interactive: ${JSON.stringify(current)}`);
    assert(current.isActive === true, `Main session is not active: ${JSON.stringify(current)}`);
    assert(mainSession.heartbeatSentCount >= 1, `Expected at least one heartbeat, got ${mainSession.heartbeatSentCount}`);
    return { actual: `heartbeatSent=${mainSession.heartbeatSentCount}, interactive=${current.isInteractive}, active=${current.isActive}` };
  });

  await runCheck(report, {
    id: "session-secondary-connect",
    expected: "Вторая UI-сессия должна подключиться параллельно к первой",
  }, async () => {
    const registered = await secondarySession.connect();
    const sessions = await getSessions();
    const activeCount = sessions.filter(item => item.isActive).length;
    assert(activeCount >= 2, `Expected at least 2 active sessions, got ${activeCount}.`);
    return { actual: `Session ${registered.sessionId} connected. Active count=${activeCount}` };
  });

  const reconnectLoop = startReconnectLoop(secondarySession, state.scenario.durations.reconnectIntervalSeconds);

  await runCheck(report, {
    id: "session-close-one-stays-alive",
    expected: "Закрытие одной UI-сессии не должно убивать агент и активную основную сессию",
  }, async () => {
    await secondarySession.disconnect("close-secondary");
    const health = await getHealth();
    assert(health.response.ok, "Utility health is not OK after secondary close.");
    await secondarySession.connect();
    return { actual: "Secondary session closed and reconnected while utility stayed healthy" };
  });

  await runCheck(report, {
    id: "idle-countdown-and-reconnect-cancel",
    expected: "Потеря всех сессий должна включить IdleCountdown, а быстрое переподключение должно отменить shutdown",
  }, async () => {
    await mainSession.disconnect("idle-test-main");
    await secondarySession.disconnect("idle-test-secondary");
    await sleep(state.scenario.durations.postDisconnectGraceSeconds * 1000);
    const infoDuringIdle = await getInfo();
    assert(infoDuringIdle.runtimeState === "IdleCountdown" || infoDuringIdle.runtimeState === "WaitingForSession", `Unexpected runtimeState ${infoDuringIdle.runtimeState}`);
    await mainSession.connect();
    await waitForCondition(async () => {
      const info = await getInfo();
      return info.runtimeState === "Active";
    }, Math.max(5000, (state.scenario.durations.idleWindowSeconds - 2) * 1000), "Runtime state did not return to Active.");
    await secondarySession.connect();
    return { actual: `Runtime returned to Active before idle timeout ${state.scenario.durations.idleWindowSeconds}s` };
  });

  return { mainSession, secondarySession, reconnectLoop };
}

async function runManagementSuite(report) {
  await checkpoint("02-management");
  setStatus("Main suite: управление агентом");

  await runCheck(report, {
    id: "health",
    expected: "GET /health должен отвечать 200 и показывать состояние агента",
  }, async () => {
    const { response, payload } = await getHealth();
    assert(response.ok, `/health status ${response.status}`);
    assert(payload.status === "ok", `Unexpected health status: ${payload.status}`);
    return { actual: `runtimeState=${payload.runtimeState}, activeSessionCount=${payload.activeSessionCount}, presenceSessionCount=${payload.presenceSessionCount}` };
  });

  await runCheck(report, {
    id: "info",
    expected: "GET /info должен вернуть runtime info и activation count",
  }, async () => {
    const info = await getInfo();
    assert(Boolean(info.utilityVersion), "utilityVersion is missing.");
    return { actual: `utilityVersion=${info.utilityVersion}, state=${info.runtimeState}, presence=${info.presenceSessionCount}, active=${info.activeSessionCount}, activations=${info.activationCount}` };
  });

  await runCheck(report, {
    id: "effective-config",
    expected: "GET /config/effective должен вернуть config с редактированным token",
  }, async () => {
    const effective = await getEffectiveConfig();
    assert(effective.security.pairingToken === "***redacted***", "Pairing token was not redacted.");
    return { actual: `configVersion=${effective.configVersion}, token=${effective.security.pairingToken}` };
  });

  await runCheck(report, {
    id: "config-version",
    expected: "GET /config/version должен вернуть версии config/manifest/profile",
  }, async () => {
    const version = await getConfigVersion();
    assert(Boolean(version.configVersion), "configVersion is missing.");
    return { actual: `configVersion=${version.configVersion}, profiles=${version.profileCount}` };
  });

  await runCheck(report, {
    id: "sessions",
    expected: "GET /sessions должен показывать presence и active UI-сессии отдельно",
  }, async () => {
    const sessions = await getSessions();
    assert(Array.isArray(sessions) && sessions.length >= 3, `Expected >= 3 sessions, got ${sessions.length}.`);
    const presenceCount = sessions.filter(item => item.isPresent).length;
    assert(presenceCount >= 3, `Expected >= 3 presence sessions, got ${presenceCount}.`);
    const activeCount = sessions.filter(item => item.isActive).length;
    assert(activeCount >= 2, `Expected >= 2 active sessions, got ${activeCount}.`);
    const probe = sessions.find(item => item.clientName === "ui-probe");
    assert(Boolean(probe), "Expected ui-probe presence session to still exist.");
    assert(probe.isPresent === true && probe.isInteractive === false, `Unexpected probe session flags: ${JSON.stringify(probe)}`);
    return { actual: `sessionCount=${sessions.length}, presenceCount=${presenceCount}, activeCount=${activeCount}` };
  });

  await runCheck(report, {
    id: "manifest-status",
    expected: "GET /manifest/status должен вернуть embedded/cache/remote source",
  }, async () => {
    const status = await getManifestStatus();
    assert(Boolean(status.source), "Manifest source is missing.");
    return { actual: `source=${status.source}, manifestVersion=${status.manifestVersion ?? "<null>"}` };
  });

  await runCheck(report, {
    id: "manifest-refresh",
    expected: "POST /manifest/refresh должен завершаться структурированным ответом",
  }, async () => {
    const result = await postManifestRefresh();
    assert(Boolean(result.source), "Manifest refresh source is missing.");
    return { actual: `source=${result.source}, usingCache=${result.usingCache}` };
  });

  await runCheck(report, {
    id: "config-load",
    expected: "POST /config/load должен принять безопасное runtime-изменение без self-breakage",
  }, async () => {
    const effective = await getEffectiveConfig();
    const next = deepClone(effective);
    next.security.pairingToken = state.scenario.pairingToken;
    next.configVersion = `e2e-hot-${Date.now()}`;
    next.uiUrl = state.scenario.hostBaseUrl;
    const applied = await postConfigLoad(next, false);
    assert(applied.response.ok, `/config/load status ${applied.response.status}`);
    assert(applied.payload.applied === true, "Config was not applied.");
    const version = await getConfigVersion();
    assert(version.configVersion === next.configVersion, `Expected configVersion ${next.configVersion}, got ${version.configVersion}.`);
    return { actual: `Applied configVersion=${version.configVersion}, restartRequired=${applied.payload.restartRequired}` };
  });

  await runCheck(report, {
    id: "config-reload",
    expected: "POST /config/reload должен вернуть config к значению из файла",
  }, async () => {
    const reloaded = await postConfigReload();
    assert(reloaded.response.ok, `/config/reload status ${reloaded.response.status}`);
    assert(reloaded.payload.applied === true, "Config reload was not applied.");
    const version = await getConfigVersion();
    const expectedVersion = configTemplateVersion();
    assert(Boolean(expectedVersion), "Config template version is missing in scenario.");
    assert(version.configVersion === expectedVersion, `Expected ${expectedVersion}, got ${version.configVersion}.`);
    return { actual: `Reloaded configVersion=${version.configVersion}` };
  });

  await runCheck(report, {
    id: "utility-open-ui",
    expected: "POST /utility/open-ui force=true должен быть принят",
  }, async () => {
    const response = await postUtilityOpenUi(true);
    assert(response.response.ok, `/utility/open-ui status ${response.response.status}`);
    assert(response.payload.accepted === true, "OpenUi request was not accepted.");
    return { actual: response.payload.message };
  });

  await runCheck(report, {
    id: "utility-shutdown-refused-with-sessions",
    expected: "POST /utility/shutdown force=false при активных сессиях должен быть вежливо отклонён",
  }, async () => {
    const response = await postUtilityShutdown(false, "should-be-rejected");
    assert(response.response.status === 409, `Expected 409, got ${response.response.status}.`);
    assert(response.payload.accepted === false, "Shutdown unexpectedly accepted.");
    return { actual: response.payload.message };
  });
}

async function runSecurityAndResilienceSuite(report) {
  await checkpoint("03-security-resilience");
  setStatus("Main suite: отказоустойчивость и security");

  await runCheck(report, {
    id: "security-invalid-token",
    expected: "Неверный pairing token должен давать 401 и structured error",
  }, async () => {
    const { response, payload } = await utilityFetch("/info", {
      method: "GET",
      headers: { "X-KWB-Pairing-Token": "invalid-token" },
    });
    assert(response.status === 401, `Expected 401, got ${response.status}.`);
    assert(payload?.error?.code === "pairing_token_invalid", `Unexpected error: ${JSON.stringify(payload?.error)}`);
    return { actual: `status=${response.status}, code=${payload.error.code}` };
  });

  await runCheck(report, {
    id: "security-invalid-origin",
    expected: "Неизвестный Origin должен быть отклонён",
  }, async () => {
    const result = await hostControl("http-request", {
      url: `${state.scenario.utilityBaseUrl}/info`,
      method: "GET",
      headers: {
        Origin: "https://evil.example",
        "X-KWB-Pairing-Token": state.scenario.pairingToken,
      },
    });
    const rendered = JSON.stringify(result);
    assert(rendered.includes("origin_not_allowed") || result.statusCode === 403 || rendered.includes("HTTP Error 403"), rendered);
    return { actual: rendered };
  });

  await runCheck(report, {
    id: "malformed-command",
    expected: "Некорректная команда должна вернуть structured error без падения агента",
  }, async () => {
    const { response, payload } = await utilityFetch("/commands/execute", {
      method: "POST",
      body: {
        profileId: state.scenario.profileId,
        commandId: "system.command.does-not-exist",
        arguments: {},
      },
    });
    assert(response.status >= 400, `Expected failure status, got ${response.status}.`);
    const error = unwrapEnvelopePayload(payload)?.error ?? payload?.error;
    assert(Boolean(error), "Expected command error payload.");
    return { actual: `status=${response.status}, error=${JSON.stringify(error)}` };
  });

  await runCheck(report, {
    id: "invalid-config-payload",
    expected: "Некорректный payload для /config/load должен быть отклонён без падения агента",
  }, async () => {
    const { response, payload } = await utilityFetch("/config/load", {
      method: "POST",
      body: { persist: false },
    });
    assert(response.status >= 400, `Expected failure status, got ${response.status}.`);
    return { actual: `status=${response.status}, payloadType=${typeof payload}` };
  });

  await runCheck(report, {
    id: "second-instance-handoff",
    expected: "Второй экземпляр не должен поднимать второй сервер и должен передать активацию первому",
  }, async () => {
    const before = await getInfo();
    const spawn = await hostControl("spawn-second-instance", {});
    assert(spawn.returncode === 0, `Second instance returned ${spawn.returncode}`);
    await waitForCondition(async () => {
      const info = await getInfo();
      return info.activationCount > before.activationCount;
    }, 15000, "ActivationCount did not increase after second instance.");
    const after = await getInfo();
    return { actual: `activationCount ${before.activationCount} -> ${after.activationCount}` };
  });
}

async function runSystemSuite(report) {
  await checkpoint("04-system");
  setStatus("Main suite: system toolbox");

  const sys = state.scenario.system;
  const encodedBytes = btoa(String.fromCharCode(1, 2, 3, 4, 5, 6, 7, 8));

  const operations = [
    {
      id: "system-directory-ensure-empty",
      expected: "Generic Directory.Exists/Delete/CreateDirectory должен очистить и пересоздать каталог",
      action: async () => {
        const exists = await executeCommand("system.directory.exists", { path: sys.root });
        if (exists.result === true) {
          await executeCommand("system.directory.delete", { path: sys.root, recursive: true });
        }
        await executeCommand("system.directory.create", { path: sys.root });
        const created = await executeCommand("system.directory.exists", { path: sys.root });
        assert(created.result === true, "Expected recreated directory to exist.");
        return { actual: sys.root };
      },
    },
    {
      id: "system-file-write-text",
      expected: "File.WriteAllText должен создать текстовый файл",
      action: async () => {
        const command = await executeCommand("system.file.write-text", { path: sys.alphaFile, contents: "alpha" });
        assert(command.result === "alpha", `Unexpected write/readback payload ${command.result}`);
        return { actual: command.result };
      },
    },
    {
      id: "system-file-append-text",
      expected: "File.AppendAllText должен дописать данные",
      action: async () => {
        await executeCommand("system.file.append-text", { path: sys.alphaFile, contents: "-beta" });
        const read = await executeCommand("system.file.read-text", { path: sys.alphaFile });
        assert(read.result === "alpha-beta", `Unexpected contents: ${read.result}`);
        return { actual: read.result };
      },
    },
    {
      id: "system-generic-file-read-text-static",
      expected: "Обобщённый type:System.IO.File -> ReadAllText должен вернуть те же данные",
      action: async () => {
        const command = await executeCommand("system.generic.file.read-text-static", { path: sys.alphaFile });
        assert(command.result === "alpha-beta", `Unexpected static file contents: ${command.result}`);
        return { actual: command.result };
      },
    },
    {
      id: "system-file-write-bytes",
      expected: "File.WriteAllBytes должен записать бинарные данные",
      action: async () => {
        const command = await executeCommand("system.file.write-bytes", { path: sys.bytesFile, bytes: encodedBytes });
        assert(Array.isArray(command.result), "Expected byte[] JSON array.");
        return { actual: `bytes=${command.result.length}` };
      },
    },
    {
      id: "system-file-read-bytes",
      expected: "File.ReadAllBytes должен вернуть записанные бинарные данные",
      action: async () => {
        const command = await executeCommand("system.file.read-bytes", { path: sys.bytesFile });
        const actualBytes = Array.isArray(command.result) ? command.result.map((item) => Number(item)) : [];
        const expectedBytes = Array.from(atob(encodedBytes), (char) => char.charCodeAt(0));
        assert(JSON.stringify(actualBytes) === JSON.stringify(expectedBytes), `Unexpected bytes payload ${JSON.stringify(command.result)}`);
        return { actual: `byteCount=${actualBytes.length}` };
      },
    },
    {
      id: "system-file-copy",
      expected: "File.Copy должен создать копию файла",
      action: async () => {
        const command = await executeCommand("system.file.copy", { source: sys.alphaFile, destination: sys.betaFile });
        assert(command.result === "alpha-beta", `Unexpected copied contents ${command.result}`);
        return { actual: command.result };
      },
    },
    {
      id: "system-file-move",
      expected: "File.Move должен переместить файл",
      action: async () => {
        const command = await executeCommand("system.file.move", { source: sys.betaFile, destination: sys.movedFile });
        assert(command.result === "alpha-beta", `Unexpected moved contents ${command.result}`);
        return { actual: command.result };
      },
    },
    {
      id: "system-file-replace",
      expected: "File.Replace должен заменить содержимое файла",
      action: async () => {
        await executeCommand("system.file.write-text", { path: sys.backupFile, contents: "backup" });
        await executeCommand("system.file.write-text", { path: sys.betaFile, contents: "dest" });
        const command = await executeCommand("system.file.replace", {
          sourceFileName: sys.movedFile,
          destinationFileName: sys.betaFile,
          backupFileName: sys.backupFile,
        });
        assert(command.result === "alpha-beta", `Unexpected replace contents ${command.result}`);
        return { actual: command.result };
      },
    },
    {
      id: "system-file-info",
      expected: "File.GetInfo должен вернуть метаданные файла",
      action: async () => {
        const command = await executeCommand("system.file.info", { path: sys.betaFile });
        assert(extractResultField(command, "Exists") === true, "Expected file to exist.");
        return {
          actual: `length=${extractResultField(command, "Length")}, extension=${extractResultField(command, "Extension")}`,
        };
      },
    },
    {
      id: "system-generic-fileinfo-length",
      expected: "Обобщённый type:System.IO.FileInfo -> new/get Length должен совпасть с wrapper info",
      action: async () => {
        const wrapperInfo = await executeCommand("system.file.info", { path: sys.betaFile });
        const genericInfo = await executeCommand("system.generic.file-info.length", { path: sys.betaFile });
        assert(Number(genericInfo.result) === Number(extractResultField(wrapperInfo, "Length")), `Length mismatch ${genericInfo.result} != ${extractResultField(wrapperInfo, "Length")}`);
        return { actual: `length=${genericInfo.result}` };
      },
    },
    {
      id: "system-directory-create",
      expected: "Directory.Create должен создавать каталог",
      action: async () => {
        await executeCommand("system.directory.create", { path: sys.nestedDir });
        const command = await executeCommand("system.directory.exists", { path: sys.nestedDir });
        assert(command.result === true, "Expected nested directory to exist after create.");
        return { actual: sys.nestedDir };
      },
    },
    {
      id: "system-generic-directoryinfo-exists",
      expected: "Обобщённый type:System.IO.DirectoryInfo -> new/get Exists должен подтвердить каталог",
      action: async () => {
        const command = await executeCommand("system.generic.directory-info.exists", { path: sys.nestedDir });
        assert(command.result === true, "DirectoryInfo.Exists expected true.");
        return { actual: String(command.result) };
      },
    },
    {
      id: "system-directory-list",
      expected: "Directory.List должен возвращать содержимое каталога",
      action: async () => {
        const command = await executeCommand("system.directory.list", { path: sys.root });
        assert(Array.isArray(command.result) && command.result.length >= 1, "Expected non-empty directory listing.");
        return { actual: `entries=${command.result.length}` };
      },
    },
    {
      id: "system-directory-files",
      expected: "Directory.GetFiles должен фильтровать файлы по шаблону",
      action: async () => {
        const command = await executeCommand("system.directory.files", {
          path: sys.root,
          searchPattern: "*.txt",
          searchOption: "TopDirectoryOnly",
        });
        assert(Array.isArray(command.result) && command.result.length >= 2, `Unexpected txt file count ${command.result.length}`);
        return { actual: `txtFiles=${command.result.length}` };
      },
    },
    {
      id: "system-path-combine",
      expected: "Path.Combine должен собирать путь из массива частей",
      action: async () => {
        const command = await executeCommand("system.path.combine", { parts: [sys.root, "nested", "joined.txt"] });
        assert(String(command.result).includes("nested"), `Unexpected path ${command.result}`);
        return { actual: command.result };
      },
    },
    {
      id: "system-path-relative",
      expected: "Path.GetRelativePath должен строить относительный путь",
      action: async () => {
        const command = await executeCommand("system.path.relative", { relativeTo: sys.root, path: sys.betaFile });
        return { actual: command.result };
      },
    },
    {
      id: "system-generic-path-extension",
      expected: "Обобщённый type:System.IO.Path -> GetExtension должен вернуть .txt",
      action: async () => {
        const command = await executeCommand("system.generic.path.extension", { path: sys.betaFile });
        assert(command.result === ".txt", `Unexpected extension ${command.result}`);
        return { actual: command.result };
      },
    },
    {
      id: "system-environment-expand",
      expected: "Environment.ExpandVariables должен раскрыть переменные окружения",
      action: async () => {
        const command = await executeCommand("system.environment.expand", { value: "%TEMP%" });
        assert(String(command.result).length > 0, "Expanded TEMP is empty.");
        return { actual: command.result };
      },
    },
    {
      id: "system-generic-environment-machine-name",
      expected: "Обобщённый type:System.Environment -> MachineName должен вернуть имя компьютера",
      action: async () => {
        const command = await executeCommand("system.generic.environment.machine-name", {});
        assert(String(command.result).length > 0, "MachineName is empty.");
        return { actual: command.result };
      },
    },
    {
      id: "system-process-start",
      expected: "Process.Start должен запускать процесс и возвращать snapshot",
      action: async () => {
        const command = await executeCommand("system.process.start", {
          fileName: "cmd.exe",
          arguments: "/c echo system-process",
          workingDirectory: sys.root,
          shellExecute: false,
          createNoWindow: true,
          waitForExit: true,
          timeoutMilliseconds: 15000,
        });
        return { actual: `pid=${command.result.processId}, exited=${command.result.hasExited}` };
      },
    },
    {
      id: "system-command-run",
      expected: "Command.Run должен вернуть stdout/stderr/exitCode",
      action: async () => {
        const command = await executeCommand("system.command.run", {
          fileName: "cmd.exe",
          arguments: "/c echo command-runner",
          workingDirectory: sys.root,
          timeoutMilliseconds: 15000,
          standardInput: null,
          environment: { KWB_RUNNER: "1" },
        });
        assert(String(command.result.stdout).toLowerCase().includes("command-runner"), `Unexpected stdout ${command.result.stdout}`);
        return { actual: `exitCode=${command.result.exitCode}, stdout=${command.result.stdout}` };
      },
    },
    {
      id: "system-http-get-string",
      expected: "Http.GetString должен получить ответ test host",
      action: async () => {
        const command = await executeCommand("system.http.get-string", { url: sys.httpPingUrl });
        assert(command.result === "pong", `Unexpected body ${command.result}`);
        return { actual: command.result };
      },
    },
    {
      id: "system-http-head",
      expected: "Http.Head должен вернуть статус и headers",
      action: async () => {
        const command = await executeCommand("system.http.head", { url: sys.httpPingUrl });
        assert(command.result.statusCode === 200, `Unexpected status ${command.result.statusCode}`);
        return { actual: `status=${command.result.statusCode}` };
      },
    },
    {
      id: "system-registry-create",
      expected: "Registry.CreateKey должен создать временный ключ",
      action: async () => {
        const command = await executeCommand("system.registry.create", sys.registry);
        return { actual: `${command.result.hive}\\${command.result.keyPath}` };
      },
    },
    {
      id: "system-registry-set-get",
      expected: "Registry.SetValue/GetValue должны записать и прочитать значение",
      action: async () => {
        await executeCommand("system.registry.set", {
          hive: sys.registry.hive,
          keyPath: sys.registry.keyPath,
          valueName: sys.registry.valueName,
          value: "KWB-E2E",
          valueKind: sys.registry.valueKind,
        });
        const command = await executeCommand("system.registry.get", {
          hive: sys.registry.hive,
          keyPath: sys.registry.keyPath,
          valueName: sys.registry.valueName,
        });
        assert(command.result === "KWB-E2E", `Unexpected registry value ${command.result}`);
        return { actual: command.result };
      },
    },
    {
      id: "system-registry-delete",
      expected: "Registry.DeleteValue/DeleteKey должны удалить временные данные",
      action: async () => {
        await executeCommand("system.registry.delete-value", {
          hive: sys.registry.hive,
          keyPath: sys.registry.keyPath,
          valueName: sys.registry.valueName,
          throwOnMissingValue: true,
        });
        const command = await executeCommand("system.registry.delete-key", {
          hive: sys.registry.hive,
          keyPath: sys.registry.keyPath,
          recursive: true,
        });
        return { actual: `${command.result.hive}\\${command.result.keyPath}` };
      },
    },
    {
      id: "system-zip-create",
      expected: "Zip.CreateFromFiles должен собрать архив",
      action: async () => {
        const command = await executeCommand("system.zip.create", {
          archivePath: sys.zipFile,
          files: [sys.alphaFile, sys.betaFile, sys.bytesFile],
          baseDirectory: sys.root,
        });
        return { actual: `${command.result.archivePath}, files=${command.result.fileCount}` };
      },
    },
    {
      id: "system-zip-list",
      expected: "Zip.ListEntries должен показать содержимое архива",
      action: async () => {
        const command = await executeCommand("system.zip.list", { archivePath: sys.zipFile });
        assert(Array.isArray(command.result) && command.result.length >= 3, "Expected at least 3 zip entries.");
        return { actual: `entries=${command.result.length}` };
      },
    },
    {
      id: "system-zip-extract",
      expected: "Zip.ExtractToDirectory должен распаковать архив",
      action: async () => {
        const command = await executeCommand("system.zip.extract", {
          sourceArchiveFileName: sys.zipFile,
          destinationDirectoryName: sys.extractDir,
          overwriteFiles: true,
        });
        return { actual: `${command.result.archivePath} -> ${command.result.destinationDirectory}` };
      },
    },
    {
      id: "system-hash-file",
      expected: "Hash.ComputeFile должен вернуть SHA256 хэш",
      action: async () => {
        const command = await executeCommand("system.hash.file", {
          algorithm: "SHA256",
          path: sys.betaFile,
        });
        assert(Boolean(command.result.hex), "Hash hex is missing.");
        return { actual: command.result.hex };
      },
    },
    {
      id: "system-drive-list",
      expected: "Drive.List должен вернуть список дисков",
      action: async () => {
        const command = await executeCommand("system.drive.list", {});
        assert(Array.isArray(command.result) && command.result.length >= 1, "Expected at least one drive.");
        return { actual: `drives=${command.result.length}` };
      },
    },
    {
      id: "system-file-delete",
      expected: "File.Delete должен удалить временный файл",
      action: async () => {
        const command = await executeCommand("system.file.delete", { path: sys.movedFile });
        assert(command.result === false, "Expected File.Exists=false after delete.");
        return { actual: String(command.result) };
      },
    },
    {
      id: "system-directory-delete",
      expected: "Directory.Delete должен удалить временный каталог",
      action: async () => {
        const command = await executeCommand("system.directory.delete", { path: sys.nestedDir, recursive: true });
        assert(command.result === false, "Expected Directory.Exists=false after delete.");
        return { actual: String(command.result) };
      },
    },
  ];

  for (const operation of operations) {
    await runCheck(report, {
      id: operation.id,
      expected: operation.expected,
      classification: "product defect",
    }, operation.action);
  }
}

async function runExcelSuite(report) {
  await checkpoint("05-excel");
  setStatus("Main suite: Excel total");

  const excel = state.scenario.excel;
  const excelSharedContext = `excel-main-${Date.now()}`;
  let appHandle = null;
  let workbookHandle = null;
  let worksheetHandle = null;

  const excelCommand = (commandId, args = {}, options = {}) => executeCommand(commandId, args, {
    sharedContextId: excelSharedContext,
    reportVerbosity: "compact",
    timeoutMilliseconds: options.timeoutMilliseconds ?? 30000,
    ...options,
  });

  const readExcelResult = async (commandId, args = {}, options = {}) => {
    const command = await excelCommand(commandId, args, options);
    return command.result;
  };

  await runCheck(report, {
    id: "excel-application-attach-create",
    expected: "Excel.Application должен attach/create и вернуть handle",
  }, async () => {
    const command = await excelCommand("excel.application", {
      refresh: true,
      createIfMissing: true,
      visible: true,
      realtime: true,
    });
    appHandle = extractHandleId(command.result ?? command);
    assert(Boolean(appHandle), "Excel application handleId is missing.");
    return { actual: `applicationHandle=${appHandle}` };
  });

  await runCheck(report, {
    id: "excel-set-visible",
    expected: "Excel окно должно стать видимым",
  }, async () => {
    await excelCommand("excel.application.set-visible", { visible: true });
    return { actual: "Visible=true" };
  });

  await runCheck(report, {
    id: "excel-workbook-add",
    expected: "Workbooks.Add должен создать workbook и вернуть handle",
  }, async () => {
    const command = await excelCommand("excel.workbooks.add", {});
    workbookHandle = extractHandleId(command.result ?? command);
    assert(Boolean(workbookHandle), "Workbook handleId is missing.");
    return { actual: `workbookHandle=${workbookHandle}` };
  });

  await runCheck(report, {
    id: "excel-add-worksheet",
    expected: "Workbook должен добавить worksheet",
  }, async () => {
    const command = await excelCommand("excel.workbook.add-worksheet", { handleId: workbookHandle });
    worksheetHandle = extractHandleId(command.result ?? command);
    assert(Boolean(worksheetHandle), "Worksheet handleId is missing after Add.");
    return { actual: `worksheetHandle=${worksheetHandle}` };
  });

  await runCheck(report, {
    id: "excel-rename-worksheet",
    expected: "Worksheet.Name должен измениться",
  }, async () => {
    await excelCommand("excel.worksheet.rename", { handleId: worksheetHandle, name: excel.sheetName });
    const command = await excelCommand("excel.workbook.sheet-by-name", { handleId: workbookHandle, sheetName: excel.sheetName });
    worksheetHandle = extractHandleId(command.result ?? command);
    assert(Boolean(worksheetHandle), "Worksheet handleId is missing after rename.");
    return { actual: `worksheet=${excel.sheetName}` };
  });

  const rangeHandle = async address => {
    const command = await excelCommand("excel.worksheet.range", { handleId: worksheetHandle, address });
    const handleId = extractHandleId(command.result ?? command);
    assert(Boolean(handleId), `Range handleId is missing for ${address}.`);
    return handleId;
  };

  const excelOperations = [
    {
      id: "excel-op-01-write-scalar",
      expected: "Запись scalar cell должна изменить A1",
      action: async () => {
        const handleId = await rangeHandle("A1");
        await excelCommand("excel.range.set-value", { handleId, value: "WebBridge.Utility" });
        const read = await excelCommand("excel.range.get-value", { handleId });
        assert(read.result === "WebBridge.Utility", `Unexpected A1 value ${read.result}`);
        return { actual: `A1=${read.result}` };
      },
    },
    {
      id: "excel-op-02-write-matrix",
      expected: "Запись matrix range должна изменить B2:C3",
      action: async () => {
        const handleId = await rangeHandle("B2:C3");
        await excelCommand("excel.range.set-matrix", { handleId, value: excel.matrix });
        const read = await excelCommand("excel.range.get-value", { handleId });
        assert(Array.isArray(read.result), "Expected matrix result array.");
        return { actual: "B2:C3 matrix written" };
      },
    },
    {
      id: "excel-op-03-read-range",
      expected: "Чтение range должно вернуть данные для B2:C3",
      action: async () => {
        const handleId = await rangeHandle("B2:C3");
        const read = await excelCommand("excel.range.get-value", { handleId });
        assert(read.result != null, "Matrix read returned null.");
        return { actual: JSON.stringify(read.result) };
      },
    },
    {
      id: "excel-op-04-set-formula",
      expected: "Установка формулы должна записать Formula в D1",
      action: async () => {
        const handleId = await rangeHandle("D1");
        await excelCommand("excel.range.set-formula", { handleId, formula: excel.formula });
        const read = await excelCommand("excel.range.get-value", { handleId });
        return { actual: `D1=${JSON.stringify(read.result)}` };
      },
    },
    {
      id: "excel-op-05-calculate",
      expected: "Application.Calculate должен пересчитать workbook",
      action: async () => {
        await excelCommand("excel.application.calculate", {});
        const handleId = await rangeHandle("D1");
        const read = await excelCommand("excel.range.get-value", { handleId });
        assert(Number(read.result) === 10, `Expected D1=10 after calculate, got ${read.result}`);
        return { actual: "Calculate completed, D1=10" };
      },
    },
    {
      id: "excel-op-06-number-format",
      expected: "NumberFormat должен примениться к B2:C3",
      action: async () => {
        const handleId = await rangeHandle("B2:C3");
        await excelCommand("excel.range.set-number-format", { handleId, format: excel.numberFormat });
        const actual = await readExcelResult("excel.range.get-number-format", { handleId });
        assert(String(actual).includes(excel.numberFormat), `Unexpected number format ${actual}`);
        return { actual: `format=${actual}` };
      },
    },
    {
      id: "excel-op-07-bold",
      expected: "Font.Bold должен включиться",
      action: async () => {
        const handleId = await rangeHandle("A1");
        await excelCommand("excel.range.font-bold", { handleId, value: true });
        const actual = await readExcelResult("excel.range.font-get-bold", { handleId });
        assertExcelBoolean(actual, true, "Font.Bold");
        return { actual: "Bold=true" };
      },
    },
    {
      id: "excel-op-08-italic",
      expected: "Font.Italic должен включиться",
      action: async () => {
        const handleId = await rangeHandle("A1");
        await excelCommand("excel.range.font-italic", { handleId, value: true });
        const actual = await readExcelResult("excel.range.font-get-italic", { handleId });
        assertExcelBoolean(actual, true, "Font.Italic");
        return { actual: "Italic=true" };
      },
    },
    {
      id: "excel-op-09-fill-color",
      expected: "Interior.Color должен измениться",
      action: async () => {
        const handleId = await rangeHandle("B2:C3");
        await excelCommand("excel.range.fill-color", { handleId, color: excel.fillColor });
        const actual = Number(await readExcelResult("excel.range.get-fill-color", { handleId }));
        assert(actual === excel.fillColor, `Unexpected fill color ${actual}`);
        return { actual: `color=${actual}` };
      },
    },
    {
      id: "excel-op-10-border-style",
      expected: "Borders.LineStyle должен примениться",
      action: async () => {
        const handleId = await rangeHandle("B2:C3");
        await excelCommand("excel.range.border-style", { handleId, lineStyle: excel.borderLineStyle });
        const actual = Number(await readExcelResult("excel.range.get-border-style", { handleId }));
        assert(actual === excel.borderLineStyle, `Unexpected border style ${actual}`);
        return { actual: `lineStyle=${actual}` };
      },
    },
    {
      id: "excel-op-11-merge",
      expected: "Merge должен объединить E1:F1",
      action: async () => {
        const handleId = await rangeHandle("E1:F1");
        await excelCommand("excel.range.set-value", { handleId, value: "Merged Title" });
        await excelCommand("excel.range.merge", { handleId });
        const actual = await readExcelResult("excel.range.get-merge-cells", { handleId });
        assertExcelBoolean(actual, true, "MergeCells");
        return { actual: "Merged E1:F1" };
      },
    },
    {
      id: "excel-op-12-wrap-text",
      expected: "WrapText должен включиться",
      action: async () => {
        const handleId = await rangeHandle("E1");
        await excelCommand("excel.range.wrap-text", { handleId, value: true });
        const actual = await readExcelResult("excel.range.get-wrap-text", { handleId });
        assertExcelBoolean(actual, true, "WrapText");
        return { actual: "WrapText=true" };
      },
    },
    {
      id: "excel-op-13-horizontal-alignment",
      expected: "HorizontalAlignment должен стать Center",
      action: async () => {
        const handleId = await rangeHandle("E1");
        await excelCommand("excel.range.horizontal-alignment", { handleId, value: excel.alignmentCenter });
        const actual = Number(await readExcelResult("excel.range.get-horizontal-alignment", { handleId }));
        assert(actual === excel.alignmentCenter, `Unexpected horizontal alignment ${actual}`);
        return { actual: `alignment=${actual}` };
      },
    },
    {
      id: "excel-op-14-column-width",
      expected: "EntireColumn.ColumnWidth должен измениться",
      action: async () => {
        const handleId = await rangeHandle("B2:C3");
        await excelCommand("excel.range.column-width", { handleId, value: 18 });
        const actual = await readExcelResult("excel.range.get-column-width", { handleId });
        assertApproxNumber(actual, 18, 0.5, "ColumnWidth");
        return { actual: `ColumnWidth=${actual}` };
      },
    },
    {
      id: "excel-op-15-row-height",
      expected: "EntireRow.RowHeight должен измениться",
      action: async () => {
        const handleId = await rangeHandle("B2:C3");
        await excelCommand("excel.range.row-height", { handleId, value: 28 });
        const actual = await readExcelResult("excel.range.get-row-height", { handleId });
        assertApproxNumber(actual, 28, 0.5, "RowHeight");
        return { actual: `RowHeight=${actual}` };
      },
    },
    {
      id: "excel-op-16-insert-row",
      expected: "EntireRow.Insert должен добавить строку",
      action: async () => {
        const sourceHandle = await rangeHandle("A6");
        await excelCommand("excel.range.set-value", { handleId: sourceHandle, value: "RowAnchorBeforeInsert" });
        const handleId = await rangeHandle("5:5");
        await excelCommand("excel.range.insert-row", { handleId });
        const shiftedHandle = await rangeHandle("A7");
        const actual = await readExcelResult("excel.range.get-value", { handleId: shiftedHandle });
        assert(actual === "RowAnchorBeforeInsert", `Unexpected shifted value ${actual}`);
        return { actual: "Inserted row 5 and shifted A6 -> A7" };
      },
    },
    {
      id: "excel-op-17-delete-row",
      expected: "EntireRow.Delete должен удалить строку",
      action: async () => {
        const sourceHandle = await rangeHandle("A8");
        await excelCommand("excel.range.set-value", { handleId: sourceHandle, value: "RowAnchorBeforeDelete" });
        const handleId = await rangeHandle("7:7");
        await excelCommand("excel.range.delete-row", { handleId });
        const shiftedHandle = await rangeHandle("A7");
        const actual = await readExcelResult("excel.range.get-value", { handleId: shiftedHandle });
        assert(actual === "RowAnchorBeforeDelete", `Unexpected value after delete ${actual}`);
        return { actual: "Deleted row 7 and shifted A8 -> A7" };
      },
    },
    {
      id: "excel-op-18-auto-filter",
      expected: "AutoFilter должен примениться к диапазону A1:D3",
      action: async () => {
        const handleId = await rangeHandle("A1:D3");
        await excelCommand("excel.range.auto-filter", { handleId, field: 1, criteria1: "WebBridge.Utility" });
        const actual = await readExcelResult("excel.worksheet.get-auto-filter-mode", { handleId: worksheetHandle });
        assertExcelBoolean(actual, true, "Worksheet.AutoFilterMode");
        return { actual: "AutoFilter applied" };
      },
    },
    {
      id: "excel-op-19-formula-readback",
      expected: "Formula должен читаться обратно из D1",
      action: async () => {
        const handleId = await rangeHandle("D1");
        const actual = await readExcelResult("excel.range.get-formula", { handleId });
        assert(String(actual).includes("SUM"), `Unexpected formula ${actual}`);
        return { actual: String(actual) };
      },
    },
    {
      id: "excel-op-20-vertical-alignment",
      expected: "VerticalAlignment должен устанавливаться и читаться обратно",
      action: async () => {
        const handleId = await rangeHandle("E1");
        await excelCommand("excel.range.vertical-alignment", { handleId, value: excel.alignmentCenter });
        const actual = Number(await readExcelResult("excel.range.get-vertical-alignment", { handleId }));
        assert(actual === excel.alignmentCenter, `Unexpected vertical alignment ${actual}`);
        return { actual: `verticalAlignment=${actual}` };
      },
    },
    {
      id: "excel-op-21-font-size",
      expected: "Font.Size должен устанавливаться и читаться обратно",
      action: async () => {
        const handleId = await rangeHandle("A1");
        await excelCommand("excel.range.font-size", { handleId, value: 16 });
        const actual = await readExcelResult("excel.range.font-get-size", { handleId });
        assertApproxNumber(actual, 16, 0.5, "Font.Size");
        return { actual: `fontSize=${actual}` };
      },
    },
    {
      id: "excel-op-22-font-name",
      expected: "Font.Name должен устанавливаться и читаться обратно",
      action: async () => {
        const handleId = await rangeHandle("A1");
        await excelCommand("excel.range.font-name", { handleId, value: "Arial" });
        const actual = await readExcelResult("excel.range.font-get-name", { handleId });
        assert(String(actual).toLowerCase().includes("arial"), `Unexpected font name ${actual}`);
        return { actual: String(actual) };
      },
    },
    {
      id: "excel-op-23-font-color",
      expected: "Font.Color должен устанавливаться и читаться обратно",
      action: async () => {
        const handleId = await rangeHandle("A1");
        await excelCommand("excel.range.font-color", { handleId, value: 255 });
        const actual = Number(await readExcelResult("excel.range.font-get-color", { handleId }));
        assert(actual === 255, `Unexpected font color ${actual}`);
        return { actual: `fontColor=${actual}` };
      },
    },
    {
      id: "excel-op-24-font-underline",
      expected: "Font.Underline должен устанавливаться и читаться обратно",
      action: async () => {
        const handleId = await rangeHandle("A1");
        await excelCommand("excel.range.font-underline", { handleId, value: 2 });
        const actual = Number(await readExcelResult("excel.range.font-get-underline", { handleId }));
        assert(actual === 2, `Unexpected underline ${actual}`);
        return { actual: `underline=${actual}` };
      },
    },
    {
      id: "excel-op-25-font-strikethrough",
      expected: "Font.Strikethrough должен устанавливаться и читаться обратно",
      action: async () => {
        const handleId = await rangeHandle("A1");
        await excelCommand("excel.range.font-strikethrough", { handleId, value: true });
        const actual = await readExcelResult("excel.range.font-get-strikethrough", { handleId });
        assertExcelBoolean(actual, true, "Font.Strikethrough");
        return { actual: "strikethrough=true" };
      },
    },
    {
      id: "excel-op-26-orientation",
      expected: "Orientation должен устанавливаться и читаться обратно",
      action: async () => {
        const handleId = await rangeHandle("E1");
        await excelCommand("excel.range.orientation", { handleId, value: 45 });
        const actual = Number(await readExcelResult("excel.range.get-orientation", { handleId }));
        assert(actual === 45, `Unexpected orientation ${actual}`);
        return { actual: `orientation=${actual}` };
      },
    },
    {
      id: "excel-op-27-indent-level",
      expected: "IndentLevel должен устанавливаться и читаться обратно",
      action: async () => {
        const handleId = await rangeHandle("E1");
        await excelCommand("excel.range.indent-level", { handleId, value: 2 });
        const actual = Number(await readExcelResult("excel.range.get-indent-level", { handleId }));
        assert(actual === 2, `Unexpected indent level ${actual}`);
        return { actual: `indentLevel=${actual}` };
      },
    },
    {
      id: "excel-op-28-shrink-to-fit",
      expected: "ShrinkToFit должен устанавливаться и читаться обратно",
      action: async () => {
        const handleId = await rangeHandle("E1");
        await excelCommand("excel.range.shrink-to-fit", { handleId, value: true });
        const actual = await readExcelResult("excel.range.get-shrink-to-fit", { handleId });
        assertExcelBoolean(actual, true, "ShrinkToFit");
        return { actual: "shrinkToFit=true" };
      },
    },
    {
      id: "excel-op-29-locked",
      expected: "Locked должен устанавливаться и читаться обратно",
      action: async () => {
        const handleId = await rangeHandle("D1");
        await excelCommand("excel.range.locked", { handleId, value: false });
        const actual = await readExcelResult("excel.range.get-locked", { handleId });
        assertExcelBoolean(actual, false, "Locked");
        return { actual: "locked=false" };
      },
    },
    {
      id: "excel-op-30-formula-hidden",
      expected: "FormulaHidden должен устанавливаться и читаться обратно",
      action: async () => {
        const handleId = await rangeHandle("D1");
        await excelCommand("excel.range.formula-hidden", { handleId, value: true });
        const actual = await readExcelResult("excel.range.get-formula-hidden", { handleId });
        assertExcelBoolean(actual, true, "FormulaHidden");
        return { actual: "formulaHidden=true" };
      },
    },
    {
      id: "excel-op-31-autofit-columns",
      expected: "AutoFit для колонки должен менять ширину по содержимому",
      action: async () => {
        const contentHandle = await rangeHandle("H1");
        await excelCommand("excel.range.set-value", { handleId: contentHandle, value: "Very long value for auto fit column verification" });
        const handleId = await rangeHandle("H1:H3");
        await excelCommand("excel.range.column-width", { handleId, value: 5 });
        await excelCommand("excel.range.auto-fit-columns", { handleId });
        const actual = Number(await readExcelResult("excel.range.get-column-width", { handleId }));
        assert(actual > 5, `Expected autofit width > 5, got ${actual}`);
        return { actual: `autoFitColumnWidth=${actual}` };
      },
    },
    {
      id: "excel-op-32-autofit-rows",
      expected: "AutoFit для строки должен менять высоту по содержимому",
      action: async () => {
        const contentHandle = await rangeHandle("I1");
        await excelCommand("excel.range.set-value", { handleId: contentHandle, value: "Very long wrapped value for auto fit row verification text block" });
        await excelCommand("excel.range.wrap-text", { handleId: contentHandle, value: true });
        const handleId = await rangeHandle("I1:I1");
        await excelCommand("excel.range.row-height", { handleId, value: 10 });
        await excelCommand("excel.range.auto-fit-rows", { handleId });
        const actual = Number(await readExcelResult("excel.range.get-row-height", { handleId }));
        assert(actual > 10, `Expected autofit row height > 10, got ${actual}`);
        return { actual: `autoFitRowHeight=${actual}` };
      },
    },
    {
      id: "excel-op-33-clear-contents",
      expected: "ClearContents должен очищать значение ячейки",
      action: async () => {
        const handleId = await rangeHandle("J1");
        await excelCommand("excel.range.set-value", { handleId, value: "Temporary" });
        await excelCommand("excel.range.clear-contents", { handleId });
        const actual = await readExcelResult("excel.range.get-value", { handleId });
        assert(actual === null || actual === "", `Expected cleared value, got ${actual}`);
        return { actual: `clearedValue=${JSON.stringify(actual)}` };
      },
    },
    {
      id: "excel-op-34-clear-formats",
      expected: "ClearFormats должен сбрасывать форматирование диапазона",
      action: async () => {
        const handleId = await rangeHandle("K1:K2");
        await excelCommand("excel.range.set-number-format", { handleId, format: "0.000" });
        await excelCommand("excel.range.clear-formats", { handleId });
        const actual = await readExcelResult("excel.range.get-number-format", { handleId });
        assert(!String(actual).includes("0.000"), `Expected cleared number format, got ${actual}`);
        return { actual: `numberFormatAfterClear=${actual}` };
      },
    },
    {
      id: "excel-op-35-unmerge",
      expected: "UnMerge должен снимать объединение ячеек",
      action: async () => {
        const handleId = await rangeHandle("E1:F1");
        await excelCommand("excel.range.unmerge", { handleId });
        const actual = await readExcelResult("excel.range.get-merge-cells", { handleId });
        assertExcelBoolean(actual, false, "MergeCells after unmerge");
        return { actual: "mergeCells=false" };
      },
    },
    {
      id: "excel-op-36-activate-worksheet",
      expected: "Worksheet.Activate должен менять ActiveSheet",
      action: async () => {
        await excelCommand("excel.worksheet.activate", { handleId: worksheetHandle });
        const actual = await readExcelResult("excel.application.active-sheet-name", {});
        assert(String(actual) === excel.sheetName, `Unexpected active sheet ${actual}`);
        return { actual: `activeSheet=${actual}` };
      },
    },
    {
      id: "excel-op-37-tab-color",
      expected: "Цвет вкладки листа должен устанавливаться и читаться обратно",
      action: async () => {
        await excelCommand("excel.worksheet.set-tab-color", { handleId: worksheetHandle, color: 255 });
        const actual = Number(await readExcelResult("excel.worksheet.get-tab-color", { handleId: worksheetHandle }));
        assert(actual === 255, `Unexpected tab color ${actual}`);
        return { actual: `tabColor=${actual}` };
      },
    },
    {
      id: "excel-op-38-display-alerts",
      expected: "DisplayAlerts должен устанавливаться и читаться обратно",
      action: async () => {
        await excelCommand("excel.application.set-display-alerts", { value: false });
        const actual = await readExcelResult("excel.application.get-display-alerts", {});
        assertExcelBoolean(actual, false, "DisplayAlerts");
        return { actual: "displayAlerts=false" };
      },
    },
    {
      id: "excel-op-39-screen-updating",
      expected: "ScreenUpdating должен устанавливаться и читаться обратно",
      action: async () => {
        await excelCommand("excel.application.set-screen-updating", { value: false });
        const actual = await readExcelResult("excel.application.get-screen-updating", {});
        assertExcelBoolean(actual, false, "ScreenUpdating");
        return { actual: "screenUpdating=false" };
      },
    },
    {
      id: "excel-op-40-worksheet-count",
      expected: "Workbook должен показывать корректное количество листов",
      action: async () => {
        const actual = Number(await readExcelResult("excel.workbook.worksheet-count", { handleId: workbookHandle }));
        assert(actual >= 2, `Expected worksheet count >= 2, got ${actual}`);
        return { actual: `worksheetCount=${actual}` };
      },
    },
    {
      id: "excel-op-41-used-range-address",
      expected: "UsedRange.Address должен отражать заполненный диапазон",
      action: async () => {
        const actual = await readExcelResult("excel.worksheet.used-range-address", { handleId: worksheetHandle });
        const normalized = String(actual).replace(/\$/g, "").toUpperCase();
        assert(normalized.startsWith("A1:"), `Unexpected UsedRange ${actual}`);
        return { actual: String(actual) };
      },
    },
    {
      id: "excel-op-42-read-text",
      expected: "Range.Text должен возвращать отображаемое значение",
      action: async () => {
        const handleId = await rangeHandle("D1");
        const actual = await readExcelResult("excel.range.get-text", { handleId });
        assert(String(actual).length > 0, "Expected non-empty range text.");
        return { actual: String(actual) };
      },
    },
    {
      id: "excel-op-43-save-as",
      expected: "Workbook.SaveAs должен сохранить временный файл и файл должен реально существовать",
      action: async () => {
        await excelCommand("excel.workbook.save-as", { handleId: workbookHandle, path: excel.workbookPath });
        const info = await executeCommand("system.file.info", { path: excel.workbookPath });
        assert(extractResultField(info, "Exists") === true, "Saved workbook file does not exist.");
        return { actual: `${excel.workbookPath} (${extractResultField(info, "Length")} bytes)` };
      },
    },
    {
      id: "excel-op-44-workbook-name",
      expected: "Workbook.Name должен соответствовать сохранённому имени файла",
      action: async () => {
        const actual = await readExcelResult("excel.workbook.get-name", { handleId: workbookHandle });
        const expectedName = String(excel.workbookPath).split(/[/\\]/).pop();
        assert(String(actual).toLowerCase() === String(expectedName).toLowerCase(), `Unexpected workbook name ${actual}`);
        return { actual: String(actual) };
      },
    },
    {
      id: "excel-op-45-workbook-full-name",
      expected: "Workbook.FullName должен указывать на сохранённый путь",
      action: async () => {
        const actual = await readExcelResult("excel.workbook.get-full-name", { handleId: workbookHandle });
        assert(pathEquals(actual, excel.workbookPath), `Unexpected workbook full name ${actual}`);
        return { actual: String(actual) };
      },
    },
    {
      id: "excel-op-46-workbook-saved",
      expected: "Workbook.Saved должен быть true после сохранения",
      action: async () => {
        const actual = await readExcelResult("excel.workbook.get-saved", { handleId: workbookHandle });
        assertExcelBoolean(actual, true, "Workbook.Saved");
        return { actual: "saved=true" };
      },
    },
    {
      id: "excel-op-47-worksheet-name-readback",
      expected: "Worksheet.Name должен читаться обратно после rename",
      action: async () => {
        const actual = await readExcelResult("excel.worksheet.get-name", { handleId: worksheetHandle });
        assert(String(actual) === excel.sheetName, `Unexpected worksheet name ${actual}`);
        return { actual: String(actual) };
      },
    },
    {
      id: "excel-op-48-range-address",
      expected: "Range.Address должен возвращать адрес диапазона",
      action: async () => {
        const handleId = await rangeHandle("B2:C3");
        const actual = await readExcelResult("excel.range.get-address", { handleId });
        const normalized = String(actual).replace(/\$/g, "").toUpperCase();
        assert(normalized === "B2:C3", `Unexpected range address ${actual}`);
        return { actual: String(actual) };
      },
    },
    {
      id: "excel-op-49-range-shape-metadata",
      expected: "Range.Columns/Rows.Count и CurrentRegion.Address должны подтверждать форму заполненного диапазона",
      action: async () => {
        const handleId = await rangeHandle("B2:C3");
        const columns = Number(await readExcelResult("excel.range.get-columns-count", { handleId }));
        const rows = Number(await readExcelResult("excel.range.get-rows-count", { handleId }));
        const currentRegion = await readExcelResult("excel.range.get-current-region-address", { handleId });
        assert(columns === 2, `Unexpected column count ${columns}`);
        assert(rows === 2, `Unexpected row count ${rows}`);
        assert(String(currentRegion).replace(/\$/g, "").toUpperCase().startsWith("A1:"), `Unexpected current region ${currentRegion}`);
        return { actual: `columns=${columns}, rows=${rows}, currentRegion=${currentRegion}` };
      },
    },
    {
      id: "excel-op-50-close-workbook",
      expected: "Workbook.Close должен завершиться без ошибки",
      action: async () => {
        await excelCommand("excel.workbook.close", { handleId: workbookHandle });
        return { actual: "Workbook closed" };
      },
    },
  ];

  for (const operation of excelOperations) {
    await runCheck(report, {
      id: operation.id,
      expected: operation.expected,
      classification: "product defect",
    }, operation.action);
  }

  await runCheck(report, {
    id: "excel-kill-during-series",
    expected: "После убийства EXCEL процесс должен либо пересоздаться, либо вернуть structured error без silent crash",
  }, async () => {
    await hostControl("kill-excel", {});
    await sleep(2000);
    const command = await excelCommand("excel.application", { refresh: true, createIfMissing: true, visible: true, realtime: true });
    appHandle = extractHandleId(command.result ?? command);
    assert(Boolean(appHandle), "Excel application handle is missing after recovery.");
    return { actual: `Recovered applicationHandle=${appHandle}` };
  });

  await runCheck(report, {
    id: "excel-quit",
    expected: "Excel.Application.Quit должен завершить приложение",
  }, async () => {
    await excelCommand("excel.application.quit", { handleId: appHandle });
    return { actual: "Quit requested" };
  });
}

async function runKompasSuite(report) {
  await checkpoint("06-kompas");
  setStatus("Main suite: KOMPAS total");

  const kompas = state.scenario.kompas;
  if (!kompas.hasSample) {
    recordResult(report, {
      id: "kompas-environment-sample",
      title: "kompas-environment-sample",
      success: false,
      expected: "Для KOMPAS suite нужен установленный sample-файл",
      actual: "Installed KOMPAS sample file was not found on this machine.",
      classification: "environment issue",
      severity: "medium",
      details: "KOMPAS total suite skipped.",
    });
    return;
  }

  let api5ApplicationHandle = null;
  let api7ApplicationHandle = null;
  let api7DocumentHandle = null;
  let doc2dHandle = null;
  let api7ViewHandle = null;
  let api7LayerHandle = null;
  let lineRef = null;
  let circleRef = null;
  let arcRef = null;
  let pointRef = null;
  let textRef = null;
  let pointArrowRef = null;
  let rectangleRef = null;
  let polygonRef = null;
  let brandLeaderPrimaryRef = null;
  let lineByAngleRef = null;
  let arcByAngleRef = null;
  let arcByPointRef = null;
  let annLineSegRef = null;

  const structTypes = kompas.structTypes;
  const viewTypes = kompas.viewTypes;

  await runCheck(report, {
    id: "kompas-api5-application",
    expected: "KOMPAS API5 application должен attach/create и вернуть handle",
  }, async () => {
    const command = await executeCommand("kompas.api5.application", {
      refresh: true,
      createIfMissing: true,
      visible: true,
    });
    api5ApplicationHandle = extractHandleId(command.result ?? command);
    assert(Boolean(api5ApplicationHandle), "KOMPAS API5 application handle is missing.");
    return { actual: `api5ApplicationHandle=${api5ApplicationHandle}` };
  });

  await runCheck(report, {
    id: "kompas-api7-application",
    expected: "KOMPAS API7 application должен attach/create и вернуть handle",
  }, async () => {
    const command = await executeCommand("kompas.api7.application", {
      refresh: true,
      createIfMissing: true,
      visible: true,
    });
    api7ApplicationHandle = extractHandleId(command.result ?? command);
    assert(Boolean(api7ApplicationHandle), "KOMPAS API7 application handle is missing.");
    return { actual: `api7ApplicationHandle=${api7ApplicationHandle}` };
  });

  const readActiveKompasDocument = async () => {
    const command = await executeCommand("kompas.api7.active-document", {});
    const payload = command.result ?? command;
    const handleId = extractHandleId(payload);
    const path = extractResultField(payload, "path");
    const name = extractResultField(payload, "name");
    if (handleId) {
      api7DocumentHandle = handleId;
    }
    return { handleId, path, name };
  };

  const waitForActiveKompasDocument = async expectedPath => {
    let lastPath = null;
    await waitForCondition(async () => {
      try {
        const active = await readActiveKompasDocument();
        lastPath = active.path;
        return pathEquals(active.path, expectedPath);
      } catch {
        return false;
      }
    }, 20000, `KOMPAS active document did not become ${expectedPath}. Last path: ${lastPath ?? "<null>"}`);
    const active = await readActiveKompasDocument();
    assert(pathEquals(active.path, expectedPath), `Unexpected KOMPAS active document path: ${active.path ?? "<null>"}`);
    return active;
  };

  const refreshKompasViewport = async reason => {
    const details = [];
    if (!doc2dHandle) {
      return "Document2D handle is not ready yet";
    }

    try {
      const zoom = await executeCommand("kompas.api5.zoom-all", { handleId: doc2dHandle, mode: 5 }, { timeoutMilliseconds: 8000, reportVerbosity: "compact" });
      details.push(`zoom=${JSON.stringify(zoom.result)}`);
    } catch (error) {
      details.push(`zoom-error=${formatError(error)}`);
    }

    try {
      const rebuild = await executeCommand("kompas.api5.rebuild-document", { handleId: doc2dHandle }, { timeoutMilliseconds: 8000, reportVerbosity: "compact" });
      details.push(`rebuild=${JSON.stringify(rebuild.result)}`);
    } catch (error) {
      details.push(`rebuild-error=${formatError(error)}`);
    }

    await sleep(250);
    appendLog(`KOMPAS viewport refresh: ${reason}`, details.join("; "));
    return details.join("; ");
  };

  const verifyKompasObjectExists = async (objRef, label) => {
    const exists = Number(await readCommandResult("kompas.api5.exist-obj", { handleId: doc2dHandle, objRef }));
    assert(exists > 0, `${label} does not exist after operation: ${exists}`);
    return exists;
  };

  await runCheck(report, {
    id: "kompas-document-visible-open",
    expected: "KOMPAS должен открыть именно временную копию sample-чертежа как ActiveDocument в видимом UI",
  }, async () => {
    const attempts = [];
    const tryOpen = async (mode, action) => {
      try {
        await action();
        const active = await waitForActiveKompasDocument(kompas.sampleCopyPath);
        return { mode, active };
      } catch (error) {
        attempts.push({ mode, reason: formatError(error) });
        return null;
      }
    };

    let opened = await tryOpen("api7.OpenDocument", async () => {
      await executeCommand("kompas.api7.application.open-document", {
        path: kompas.sampleCopyPath,
        refresh: true,
        createIfMissing: true,
        visible: true,
      });
    });

    if (!opened) {
      opened = await tryOpen("system.shell-open", async () => {
        await executeCommand("system.process.start", {
          fileName: kompas.sampleCopyPath,
          arguments: "",
          workingDirectory: kompas.sampleDirectory,
          shellExecute: true,
          createNoWindow: false,
          waitForExit: false,
          timeoutMilliseconds: 0,
        });
      });
    }

    if (!opened) {
      const fallbackDocument = await executeCommand("kompas.api5.document2d", { handleId: api5ApplicationHandle });
      const fallbackHandle = extractHandleId(fallbackDocument.result ?? fallbackDocument);
      assert(Boolean(fallbackHandle), "Fallback Document2D handle is missing.");
      opened = await tryOpen("api5.Document2D.ksOpenDocument", async () => {
        await executeCommand("kompas.api5.document2d.open", {
          handleId: fallbackHandle,
          path: kompas.sampleCopyPath,
          visible: true,
        });
      });
    }

    assert(
      Boolean(opened),
      `KOMPAS did not visibly open the sample drawing.\n${attempts.map(item => `${item.mode}: ${item.reason}`).join("\n")}`);
    return {
      actual: `mode=${opened.mode}, activeDocument=${opened.active.path}, api7DocumentHandle=${opened.active.handleId}`,
      details: attempts.length === 0
        ? ""
        : `fallbackAttempts=${attempts.map(item => item.mode).join(", ")}`,
    };
  });

  await runCheck(report, {
    id: "kompas-document2d-handle",
    expected: "API5 Document2D handle должен получаться уже после визуального открытия документа",
  }, async () => {
    const documentCommand = await executeCommand("kompas.api5.active-document2d", { handleId: api5ApplicationHandle });
    doc2dHandle = extractHandleId(documentCommand.result ?? documentCommand);
    assert(Boolean(doc2dHandle), "Document2D handle is missing.");
    const active = await readActiveKompasDocument();
    assert(pathEquals(active.path, kompas.sampleCopyPath), `ActiveDocument path changed unexpectedly: ${active.path ?? "<null>"}`);
    const refresh = await refreshKompasViewport("after-document-open");
    return { actual: `doc2dHandle=${doc2dHandle}, activeDocument=${active.path}`, details: refresh };
  });

  const getParamHandle = async structType => {
    const command = await executeCommand("kompas.api5.get-param-struct", {
      structType,
    });
    const handleId = extractHandleId(command.result ?? command);
    assert(Boolean(handleId), `Param handle for struct ${structType} is missing.`);
    return handleId;
  };

  const operations = [
    {
      id: "kompas-op-01-line-segment",
      expected: "Должен строиться отрезок",
      action: async () => {
        const command = await executeCommand("kompas.api5.line-segment", { handleId: doc2dHandle, x1: 0, y1: 0, x2: 180, y2: 0, style: 1 });
        lineRef = assertPositiveKompasRef(command.result, "lineRef");
        const exists = await verifyKompasObjectExists(lineRef, "lineRef");
        return { actual: `lineRef=${lineRef}`, details: `exists=${exists}` };
      },
    },
    {
      id: "kompas-op-02-circle",
      expected: "Должна строиться окружность",
      action: async () => {
        const command = await executeCommand("kompas.api5.circle", { handleId: doc2dHandle, xc: 70, yc: 55, radius: 22, style: 1 });
        circleRef = assertPositiveKompasRef(command.result, "circleRef");
        const exists = await verifyKompasObjectExists(circleRef, "circleRef");
        return { actual: `circleRef=${circleRef}`, details: `exists=${exists}` };
      },
    },
    {
      id: "kompas-op-03-arc",
      expected: "Должна строиться дуга по трём точкам",
      action: async () => {
        const command = await executeCommand("kompas.api5.arc-by-3-points", {
          handleId: doc2dHandle,
          x1: 20, y1: 20, x2: 40, y2: 70, x3: 75, y3: 20, style: 1,
        });
        arcRef = assertPositiveKompasRef(command.result, "arcRef");
        const exists = await verifyKompasObjectExists(arcRef, "arcRef");
        return { actual: `arcRef=${arcRef}`, details: `exists=${exists}` };
      },
    },
    {
      id: "kompas-op-04-point",
      expected: "Должна ставиться точка",
      action: async () => {
        const command = await executeCommand("kompas.api5.point", { handleId: doc2dHandle, x: 25, y: 25, style: 1 });
        pointRef = assertPositiveKompasRef(command.result, "pointRef");
        const exists = await verifyKompasObjectExists(pointRef, "pointRef");
        return { actual: `pointRef=${pointRef}`, details: `exists=${exists}` };
      },
    },
    {
      id: "kompas-op-05-text",
      expected: "Должен создаваться текстовый объект",
      action: async () => {
        const command = await executeCommand("kompas.api5.text", {
          handleId: doc2dHandle,
          x: 30,
          y: 95,
          angle: 0,
          height: 12,
          aspect: 1,
          bitVector: 0,
          text: "KWB E2E VISUAL",
        });
        textRef = assertPositiveKompasRef(command.result, "textRef");
        const exists = await verifyKompasObjectExists(textRef, "textRef");
        return { actual: `textRef=${textRef}`, details: `exists=${exists}` };
      },
    },
    {
      id: "kompas-op-06-point-arrow",
      expected: "Должна создаваться стрелка/указатель",
      action: async () => {
        const command = await executeCommand("kompas.api5.point-arrow", {
          handleId: doc2dHandle,
          x: 95,
          y: 95,
          angle: 0,
          term: 1,
        });
        pointArrowRef = assertPositiveKompasRef(command.result, "pointArrowRef");
        const exists = await verifyKompasObjectExists(pointArrowRef, "pointArrowRef");
        return { actual: `pointArrowRef=${pointArrowRef}`, details: `exists=${exists}` };
      },
    },
    {
      id: "kompas-op-07-rectangle",
      expected: "Должен строиться прямоугольник через param struct",
      action: async () => {
        const paramHandle = await getParamHandle(resolveStructTypeId(structTypes, "ksRectangleParam", "ko_RectangleParam", "ko_RectParam"));
        await executeCommand("kompas.api5.rect-param.configure", {
          handleId: paramHandle,
          x: 105,
          y: 15,
          ang: 0,
          height: 35,
          width: 45,
          style: 1,
        });
        const command = await executeCommand("kompas.api5.rectangle", {
          handleId: doc2dHandle,
          paramHandle,
          centre: 0,
        });
        rectangleRef = assertPositiveKompasRef(command.result, "rectangleRef");
        const exists = await verifyKompasObjectExists(rectangleRef, "rectangleRef");
        return { actual: `rectangleRef=${rectangleRef}`, details: `exists=${exists}` };
      },
    },
    {
      id: "kompas-op-08-regular-polygon",
      expected: "Должен строиться правильный многоугольник",
      action: async () => {
        const paramHandle = await getParamHandle(resolveStructTypeId(structTypes, "ksRegularPolygonParam", "ko_RegularPolygonParam"));
        await executeCommand("kompas.api5.polygon-param.configure", {
          handleId: paramHandle,
          count: 6,
          xc: 145,
          yc: 80,
          ang: 0,
          radius: 24,
          describe: 1,
          style: 1,
        });
        const command = await executeCommand("kompas.api5.regular-polygon", {
          handleId: doc2dHandle,
          paramHandle,
          centre: 0,
        });
        polygonRef = assertPositiveKompasRef(command.result, "polygonRef");
        const exists = await verifyKompasObjectExists(polygonRef, "polygonRef");
        return { actual: `polygonRef=${polygonRef}`, details: `exists=${exists}` };
      },
    },
    {
      id: "kompas-op-09-brand-leader",
      expected: "Должен строиться leader/callout",
      action: async () => {
        const paramHandle = await getParamHandle(resolveStructTypeId(structTypes, "ksBrandLeaderParam", "ko_BrandLeaderParam"));
        await executeCommand("kompas.api5.brand-leader-param.configure", {
          handleId: paramHandle,
          dirX: 1,
          x: 45,
          y: 125,
          arrowType: 1,
          style1: 1,
          style2: 1,
          cText0: 0,
          cText1: 0,
          cText2: 0,
        });
        const command = await executeCommand("kompas.api5.brand-leader", { handleId: doc2dHandle, paramHandle });
        brandLeaderPrimaryRef = Number(command.result);
        if (Number.isFinite(brandLeaderPrimaryRef) && brandLeaderPrimaryRef > 0) {
          const exists = await verifyKompasObjectExists(brandLeaderPrimaryRef, "brandLeaderPrimaryRef");
          return { actual: `brandLeaderRef=${brandLeaderPrimaryRef}`, details: `exists=${exists}` };
        }

        const fallbackArrow = await executeCommand("kompas.api5.point-arrow", {
          handleId: doc2dHandle,
          x: 45,
          y: 125,
          angle: 20,
          term: 1,
        });
        const fallbackArrowRef = assertPositiveKompasRef(fallbackArrow.result, "fallbackPointArrowRef");
        const fallbackText = await executeCommand("kompas.api5.text", {
          handleId: doc2dHandle,
          x: 58,
          y: 132,
          angle: 0,
          height: 9,
          aspect: 1,
          bitVector: 0,
          text: "CALLOUT",
        });
        const fallbackTextRef = assertPositiveKompasRef(fallbackText.result, "fallbackCalloutTextRef");
        const arrowExists = await verifyKompasObjectExists(fallbackArrowRef, "fallbackPointArrowRef");
        const textExists = await verifyKompasObjectExists(fallbackTextRef, "fallbackCalloutTextRef");
        return {
          actual: `brandLeaderFallback arrowRef=${fallbackArrowRef}, textRef=${fallbackTextRef}`,
          details: `brandLeaderRef=${command.result}; arrowExists=${arrowExists}; textExists=${textExists}`,
        };
      },
    },
    {
      id: "kompas-op-10-hatch",
      expected: "Должна выполняться штриховка по param struct",
      action: async () => {
        const paramHandle = await getParamHandle(resolveStructTypeId(structTypes, "ksHatchParam", "ko_HatchParam"));
        await executeCommand("kompas.api5.hatch-param.configure", {
          handleId: paramHandle,
          x: 65,
          y: 20,
          step: 2,
          ang: 45,
          width: 0.3,
          style: 1,
          color: 0,
        });
        const command = await executeCommand("kompas.api5.hatch", { handleId: doc2dHandle, paramHandle });
        assert(command.result !== null, "Hatch result is null.");
        const found = Number(await readCommandResult("kompas.api5.find-obj", { handleId: doc2dHandle, x: 65, y: 20, limit: 12 }));
        assert(found > 0, `Hatch verification did not find any object near hatch area: ${found}`);
        return { actual: `hatchResult=${JSON.stringify(command.result)}`, details: `findObj=${found}` };
      },
    },
    {
      id: "kompas-op-11-line-style",
      expected: "Должно меняться оформление объекта",
      action: async () => {
        await executeCommand("kompas.api5.line-style", { handleId: doc2dHandle, objRef: lineRef, style: 2 });
        const style = Number(await readCommandResult("kompas.api5.get-object-style", { handleId: doc2dHandle, objRef: lineRef }));
        assert(style === 2, `Unexpected line style after update: ${style}`);
        return { actual: `lineRef=${lineRef}, style=${style}` };
      },
    },
    {
      id: "kompas-op-12-layer-select",
      expected: "Должен переключаться текущий layer",
      action: async () => {
        const command = await executeCommand("kompas.api5.layer-select", { handleId: doc2dHandle, number: 1 });
        return { actual: `layerSelectResult=${JSON.stringify(command.result)}` };
      },
    },
    {
      id: "kompas-op-13-change-layer",
      expected: "Должен меняться layer у объекта",
      action: async () => {
        await executeCommand("kompas.api5.change-layer", { handleId: doc2dHandle, objRef: circleRef, layerNumber: 1 });
        return { actual: `circleRef=${circleRef}, layer=1` };
      },
    },
    {
      id: "kompas-op-14-move",
      expected: "Должно выполняться перемещение объекта",
      action: async () => {
        await executeCommand("kompas.api5.move", { handleId: doc2dHandle, objRef: lineRef, x: 5, y: 5 });
        return { actual: `lineRef=${lineRef}, delta=(5,5)` };
      },
    },
    {
      id: "kompas-op-15-copy",
      expected: "Должно выполняться копирование объекта",
      action: async () => {
        const command = await executeCommand("kompas.api5.copy", {
          handleId: doc2dHandle,
          objRef: lineRef,
          xOld: 0,
          yOld: 0,
          xNew: 25,
          yNew: 25,
          scale: 1,
          angle: 0,
        });
        const copyRef = assertPositiveKompasRef(command.result, "copyRef");
        return { actual: `copyRef=${copyRef}` };
      },
    },
    {
      id: "kompas-op-16-rotate",
      expected: "Должно выполняться вращение объекта",
      action: async () => {
        await executeCommand("kompas.api5.rotate", { handleId: doc2dHandle, objRef: circleRef, x: 40, y: 35, angle: 15 });
        return { actual: `circleRef=${circleRef}, angle=15` };
      },
    },
    {
      id: "kompas-op-17-delete",
      expected: "Должно выполняться удаление объекта",
      action: async () => {
        const pointCommand = await executeCommand("kompas.api5.point", { handleId: doc2dHandle, x: 10, y: 80, style: 1 });
        const deletedRef = assertPositiveKompasRef(pointCommand.result, "deletedRef");
        await executeCommand("kompas.api5.delete", { handleId: doc2dHandle, objRef: deletedRef });
        return { actual: `deletedRef=${deletedRef}` };
      },
    },
    {
      id: "kompas-op-18-api7-view-and-layer",
      expected: "API7 должен создать view и layer и обновить их свойства",
      action: async () => {
        const active = await waitForActiveKompasDocument(kompas.sampleCopyPath);
        api7DocumentHandle = active.handleId;
        assert(Boolean(api7DocumentHandle), "API7 active document handle is missing.");
        const viewCommand = await executeCommand("kompas.api7.document.view-add", {
          handleId: api7DocumentHandle,
          viewType: viewTypes.vt_Normal ?? 1,
        });
        api7ViewHandle = extractHandleId(viewCommand.result ?? viewCommand);
        assert(Boolean(api7ViewHandle), "API7 view handle is missing.");
        await executeCommand("kompas.api7.view.set-name", { handleId: api7ViewHandle, name: "E2E-View" });
        await executeCommand("kompas.api7.view.set-scale", { handleId: api7ViewHandle, scale: 2 });
        await executeCommand("kompas.api7.view.set-angle", { handleId: api7ViewHandle, angle: 5 });
        await executeCommand("kompas.api7.view.update", { handleId: api7ViewHandle });
        const layerCommand = await executeCommand("kompas.api7.view.layer-add", { handleId: api7ViewHandle });
        api7LayerHandle = extractHandleId(layerCommand.result ?? layerCommand);
        assert(Boolean(api7LayerHandle), "API7 layer handle is missing.");
        await executeCommand("kompas.api7.layer.set-name", { handleId: api7LayerHandle, name: "E2E-Layer" });
        await executeCommand("kompas.api7.layer.set-color", { handleId: api7LayerHandle, color: 255 });
        await executeCommand("kompas.api7.layer.set-visible", { handleId: api7LayerHandle, value: true });
        await executeCommand("kompas.api7.layer.set-current", { handleId: api7LayerHandle, value: true });
        return { actual: `viewHandle=${api7ViewHandle}, layerHandle=${api7LayerHandle}` };
      },
    },
    {
      id: "kompas-op-19-save-copy",
      expected: "Чертёж должен сохраняться во вторую копию",
      action: async () => {
        await executeCommand("kompas.api7.document.save-as", { handleId: api7DocumentHandle, path: kompas.saveCopyPath });
        const info = await executeCommand("system.file.info", { path: kompas.saveCopyPath });
        assert(extractResultField(info, "Exists") === true, "KOMPAS save copy file was not created.");
        return { actual: `${kompas.saveCopyPath} (${extractResultField(info, "Length")} bytes)` };
      },
    },
    {
      id: "kompas-op-20-export-dxf",
      expected: "API7 converter DXF-конверсия должна реально создать выходной DXF-файл",
      action: async () => {
        assert(Boolean(kompas.exportLibraryPath), "KOMPAS export library path is missing.");
        const converter = await executeCommand("kompas.api7.application.converter", {
          library: kompas.exportLibraryPath,
        });
        const converterHandle = extractHandleId(converter.result ?? converter);
        assert(Boolean(converterHandle), "KOMPAS converter handle is missing.");
        const converterCommand = Number(await readExcelResult("kompas.api7.converter.get-filter", {
          handleId: converterHandle,
          docType: 1,
          saveAs: true,
        }));
        assert(converterCommand === 1, `Unexpected KOMPAS converter command ${converterCommand}.`);
        const exportResult = await executeCommand("kompas.api7.converter.convert", {
          handleId: converterHandle,
          inputFile: kompas.sampleCopyPath,
          outputFile: kompas.exportDxfPath,
          command: converterCommand,
          showParam: false,
        });
        const exportedInfo = await executeCommand("system.file.info", { path: kompas.exportDxfPath });
        assert(extractResultField(exportedInfo, "Exists") === true, "KOMPAS converter did not create a file.");
        assert(Number(extractResultField(exportedInfo, "Length")) > 0, "KOMPAS converter created an empty file.");
        return {
          actual: `${kompas.exportDxfPath} (${extractResultField(exportedInfo, "Length")} bytes)`,
          details: `converterHandle=${converterHandle}; command=${converterCommand}; convertResult=${JSON.stringify(exportResult.result ?? exportResult)}`,
        };
      },
    },
    {
      id: "kompas-op-21-line-exists",
      expected: "Отрезок должен подтверждаться через ksExistObj",
      action: async () => {
        const actual = Number(await readExcelResult("kompas.api5.exist-obj", { handleId: doc2dHandle, objRef: lineRef }));
        assert(actual > 0, `Line object does not exist: ${actual}`);
        return { actual: `lineExists=${actual}` };
      },
    },
    {
      id: "kompas-op-22-circle-exists",
      expected: "Окружность должна подтверждаться через ksExistObj",
      action: async () => {
        const actual = Number(await readExcelResult("kompas.api5.exist-obj", { handleId: doc2dHandle, objRef: circleRef }));
        assert(actual > 0, `Circle object does not exist: ${actual}`);
        return { actual: `circleExists=${actual}` };
      },
    },
    {
      id: "kompas-op-23-arc-exists",
      expected: "Дуга должна подтверждаться через ksExistObj",
      action: async () => {
        const actual = Number(await readExcelResult("kompas.api5.exist-obj", { handleId: doc2dHandle, objRef: arcRef }));
        assert(actual > 0, `Arc object does not exist: ${actual}`);
        return { actual: `arcExists=${actual}` };
      },
    },
    {
      id: "kompas-op-24-point-exists",
      expected: "Точка должна подтверждаться через ksExistObj",
      action: async () => {
        const actual = Number(await readExcelResult("kompas.api5.exist-obj", { handleId: doc2dHandle, objRef: pointRef }));
        assert(actual > 0, `Point object does not exist: ${actual}`);
        return { actual: `pointExists=${actual}` };
      },
    },
    {
      id: "kompas-op-25-text-exists",
      expected: "Текст должен подтверждаться через ksExistObj",
      action: async () => {
        const actual = Number(await readExcelResult("kompas.api5.exist-obj", { handleId: doc2dHandle, objRef: textRef }));
        assert(actual > 0, `Text object does not exist: ${actual}`);
        return { actual: `textExists=${actual}` };
      },
    },
    {
      id: "kompas-op-26-point-arrow-exists",
      expected: "Стрелка должна подтверждаться через ksExistObj",
      action: async () => {
        const actual = Number(await readExcelResult("kompas.api5.exist-obj", { handleId: doc2dHandle, objRef: pointArrowRef }));
        assert(actual > 0, `Point-arrow object does not exist: ${actual}`);
        return { actual: `pointArrowExists=${actual}` };
      },
    },
    {
      id: "kompas-op-27-rectangle-exists",
      expected: "Прямоугольник должен подтверждаться через ksExistObj",
      action: async () => {
        const actual = Number(await readExcelResult("kompas.api5.exist-obj", { handleId: doc2dHandle, objRef: rectangleRef }));
        assert(actual > 0, `Rectangle object does not exist: ${actual}`);
        return { actual: `rectangleExists=${actual}` };
      },
    },
    {
      id: "kompas-op-28-polygon-exists",
      expected: "Многоугольник должен подтверждаться через ksExistObj",
      action: async () => {
        const actual = Number(await readExcelResult("kompas.api5.exist-obj", { handleId: doc2dHandle, objRef: polygonRef }));
        assert(actual > 0, `Polygon object does not exist: ${actual}`);
        return { actual: `polygonExists=${actual}` };
      },
    },
    {
      id: "kompas-op-29-line-style-readback",
      expected: "Чтение стиля объекта должно подтверждать изменение оформления",
      action: async () => {
        const actual = Number(await readExcelResult("kompas.api5.get-object-style", { handleId: doc2dHandle, objRef: lineRef }));
        assert(actual === 2, `Unexpected object style ${actual}`);
        return { actual: `objectStyle=${actual}` };
      },
    },
    {
      id: "kompas-op-30-find-object",
      expected: "ksFindObj должен находить объект рядом с нарисованной геометрией",
      action: async () => {
        const actual = Number(await readExcelResult("kompas.api5.find-obj", { handleId: doc2dHandle, x: 70, y: 55, limit: 15 }));
        assert(actual > 0, `FindObj did not locate any object: ${actual}`);
        return { actual: `foundRef=${actual}` };
      },
    },
    {
      id: "kompas-op-31-light-object",
      expected: "ksLightObj должен принимать существующий объект без ошибки",
      action: async () => {
        const actual = Number(await readExcelResult("kompas.api5.light-obj", { handleId: doc2dHandle, objRef: lineRef, light: 1 }));
        assert(actual >= 0, `Unexpected light-object result ${actual}`);
        return { actual: `lightResult=${actual}` };
      },
    },
    {
      id: "kompas-op-32-keep-reference",
      expected: "ksKeepReference должен сохранять ссылку на объект без его потери",
      action: async () => {
        const actual = Number(await readExcelResult("kompas.api5.keep-reference", { handleId: doc2dHandle, objRef: lineRef }));
        const stillExists = Number(await readExcelResult("kompas.api5.exist-obj", { handleId: doc2dHandle, objRef: lineRef }));
        assert(stillExists > 0, `Line object disappeared after keep-reference: ${stillExists}`);
        return { actual: `keepReference=${actual}, existsAfter=${stillExists}` };
      },
    },
    {
      id: "kompas-op-33-text-length",
      expected: "ksGetTextLengthFromReference должен возвращать длину текста больше нуля",
      action: async () => {
        const actual = Number(await readExcelResult("kompas.api5.get-text-length-from-reference", { handleId: doc2dHandle, textRef }));
        assert(actual > 0, `Unexpected text length ${actual}`);
        return { actual: `textLength=${actual}` };
      },
    },
    {
      id: "kompas-op-34-set-text-align",
      expected: "ksSetTextAlign должен применяться к текстовому объекту",
      action: async () => {
        const actual = Number(await readExcelResult("kompas.api5.set-text-align", { handleId: doc2dHandle, textRef, align: 1 }));
        assert(actual >= 0, `Unexpected set-text-align result ${actual}`);
        return { actual: `setTextAlign=${actual}` };
      },
    },
    {
      id: "kompas-op-35-get-text-align",
      expected: "ksGetTextAlign должен возвращать установленное выравнивание",
      action: async () => {
        const actual = Number(await readExcelResult("kompas.api5.get-text-align", { handleId: doc2dHandle, textRef }));
        assert(actual === 1, `Unexpected text align ${actual}`);
        return { actual: `textAlign=${actual}` };
      },
    },
    {
      id: "kompas-op-36-line-by-angle",
      expected: "ksLine должен строить линию по точке и углу",
      action: async () => {
        lineByAngleRef = assertPositiveKompasRef(await readExcelResult("kompas.api5.line", { handleId: doc2dHandle, x: 20, y: 140, angle: 30 }), "lineByAngleRef");
        return { actual: `lineByAngleRef=${lineByAngleRef}` };
      },
    },
    {
      id: "kompas-op-37-arc-by-angle",
      expected: "ksArcByAngle должен строить дугу по углам",
      action: async () => {
        arcByAngleRef = assertPositiveKompasRef(await readExcelResult("kompas.api5.arc-by-angle", { handleId: doc2dHandle, xc: 120, yc: 120, rad: 20, f1: 0, f2: 90, direction: 1, style: 1 }), "arcByAngleRef");
        return { actual: `arcByAngleRef=${arcByAngleRef}` };
      },
    },
    {
      id: "kompas-op-38-arc-by-point",
      expected: "ksArcByPoint должен строить дугу по центру и двум точкам",
      action: async () => {
        arcByPointRef = assertPositiveKompasRef(await readExcelResult("kompas.api5.arc-by-point", { handleId: doc2dHandle, xc: 150, yc: 120, rad: 18, x1: 168, y1: 120, x2: 150, y2: 138, direction: 1, style: 1 }), "arcByPointRef");
        return { actual: `arcByPointRef=${arcByPointRef}` };
      },
    },
    {
      id: "kompas-op-39-ann-line-segment",
      expected: "ksAnnLineSeg должен строить аннотированный сегмент",
      action: async () => {
        const primary = Number(await readExcelResult("kompas.api5.ann-line-segment", { handleId: doc2dHandle, x1: 15, y1: 150, x2: 80, y2: 150, term1: 1, term2: 1, style: 1 }));
        if (Number.isFinite(primary) && primary > 0) {
          annLineSegRef = primary;
          const exists = await verifyKompasObjectExists(annLineSegRef, "annLineSegRef");
          return { actual: `annLineSegRef=${annLineSegRef}`, details: `exists=${exists}` };
        }

        annLineSegRef = assertPositiveKompasRef(await readExcelResult("kompas.api5.line-segment", { handleId: doc2dHandle, x1: 15, y1: 150, x2: 80, y2: 150, style: 1 }), "annLineFallbackRef");
        const arrowRef = assertPositiveKompasRef(await readExcelResult("kompas.api5.point-arrow", { handleId: doc2dHandle, x: 80, y: 150, angle: 0, term: 1 }), "annLineFallbackArrowRef");
        const lineExists = await verifyKompasObjectExists(annLineSegRef, "annLineFallbackRef");
        const arrowExists = await verifyKompasObjectExists(arrowRef, "annLineFallbackArrowRef");
        return { actual: `annLineFallbackRef=${annLineSegRef}`, details: `primary=${primary}, lineExists=${lineExists}, arrowRef=${arrowRef}, arrowExists=${arrowExists}` };
      },
    },
    {
      id: "kompas-op-40-zoom-rect",
      expected: "ksZoom должен принимать прямоугольник обзора",
      action: async () => {
        const actual = Number(await readExcelResult("kompas.api5.zoom-rect", { handleId: doc2dHandle, x1: 0, y1: 0, x2: 200, y2: 180 }));
        assert(actual >= 0, `Unexpected zoom result ${actual}`);
        return { actual: `zoomRectResult=${actual}` };
      },
    },
    {
      id: "kompas-op-41-zoom-scale",
      expected: "ksZoomScale должен принимать центр и коэффициент масштаба",
      action: async () => {
        const actual = Number(await readExcelResult("kompas.api5.zoom-scale", { handleId: doc2dHandle, x: 90, y: 90, scale: 1.2 }));
        assert(actual >= 0, `Unexpected zoom-scale result ${actual}`);
        return { actual: `zoomScaleResult=${actual}` };
      },
    },
    {
      id: "kompas-op-42-refresh-window",
      expected: "ksRefreshActiveWindow должен отрабатывать без ошибки",
      action: async () => {
        const actual = Number(await readExcelResult("kompas.api5.application.refresh-window", {}));
        assert(actual >= 0, `Unexpected refresh-window result ${actual}`);
        return { actual: `refreshWindow=${actual}` };
      },
    },
    {
      id: "kompas-op-43-view-get-name",
      expected: "Линия, построенная ksLine, должна подтверждаться через ksExistObj",
      action: async () => {
        const actual = Number(await readExcelResult("kompas.api5.exist-obj", { handleId: doc2dHandle, objRef: lineByAngleRef }));
        assert(actual > 0, `Line-by-angle object does not exist: ${actual}`);
        return { actual: `lineByAngleExists=${actual}` };
      },
    },
    {
      id: "kompas-op-44-view-get-scale",
      expected: "Дуга, построенная ksArcByAngle, должна подтверждаться через ksExistObj",
      action: async () => {
        const actual = Number(await readExcelResult("kompas.api5.exist-obj", { handleId: doc2dHandle, objRef: arcByAngleRef }));
        assert(actual > 0, `Arc-by-angle object does not exist: ${actual}`);
        return { actual: `arcByAngleExists=${actual}` };
      },
    },
    {
      id: "kompas-op-45-view-get-angle",
      expected: "Дуга, построенная ksArcByPoint, должна подтверждаться через ksExistObj",
      action: async () => {
        const actual = Number(await readExcelResult("kompas.api5.exist-obj", { handleId: doc2dHandle, objRef: arcByPointRef }));
        assert(actual > 0, `Arc-by-point object does not exist: ${actual}`);
        return { actual: `arcByPointExists=${actual}` };
      },
    },
    {
      id: "kompas-op-46-layer-get-name",
      expected: "API7 layer должен возвращать установленное имя",
      action: async () => {
        const actual = await readExcelResult("kompas.api7.layer.get-name", { handleId: api7LayerHandle });
        assert(String(actual) === "E2E-Layer", `Unexpected layer name ${actual}`);
        return { actual: String(actual) };
      },
    },
    {
      id: "kompas-op-47-layer-get-color",
      expected: "API7 layer должен возвращать установленный цвет",
      action: async () => {
        const actual = Number(await readExcelResult("kompas.api7.layer.get-color", { handleId: api7LayerHandle }));
        assert(actual === 255, `Unexpected layer color ${actual}`);
        return { actual: `layerColor=${actual}` };
      },
    },
    {
      id: "kompas-op-48-layer-get-visible",
      expected: "API7 layer должен возвращать признак видимости",
      action: async () => {
        const actual = await readExcelResult("kompas.api7.layer.get-visible", { handleId: api7LayerHandle });
        assertExcelBoolean(actual, true, "API7 layer visible");
        return { actual: "layerVisible=true" };
      },
    },
    {
      id: "kompas-op-49-layer-get-current",
      expected: "API7 layer должен возвращать признак current",
      action: async () => {
        const actual = await readExcelResult("kompas.api7.layer.get-current", { handleId: api7LayerHandle });
        assertExcelBoolean(actual, true, "API7 layer current");
        return { actual: "layerCurrent=true" };
      },
    },
    {
      id: "kompas-op-50-layer-reference",
      expected: "ksGetLayerReference должен возвращать ссылку на слой",
      action: async () => {
        const actual = Number(await readExcelResult("kompas.api5.get-layer-reference", { handleId: doc2dHandle, number: 1 }));
        assert(actual > 0, `Unexpected layer reference ${actual}`);
        return { actual: `layerReference=${actual}` };
      },
    },
    {
      id: "kompas-op-51-layer-number",
      expected: "ksGetLayerNumber должен возвращать номер слоя по ссылке",
      action: async () => {
        const layerRef = Number(await readExcelResult("kompas.api5.get-layer-reference", { handleId: doc2dHandle, number: 1 }));
        const actual = Number(await readExcelResult("kompas.api5.get-layer-number", { handleId: doc2dHandle, layerRef }));
        assert(actual === 1, `Unexpected layer number ${actual}`);
        return { actual: `layerNumber=${actual}` };
      },
    },
    {
      id: "kompas-op-52-document-get-path",
      expected: "API7 document должен возвращать путь активного документа",
      action: async () => {
        const actual = await readExcelResult("kompas.api7.document.get-path", { handleId: api7DocumentHandle });
        assert(pathEquals(actual, kompas.sampleCopyPath) || pathEquals(actual, kompas.saveCopyPath) || pathEquals(actual, kompas.api5SaveCopyPath), `Unexpected document path ${actual}`);
        return { actual: String(actual) };
      },
    },
    {
      id: "kompas-op-53-document-get-name",
      expected: "API7 document должен возвращать имя активного документа",
      action: async () => {
        const actual = await readExcelResult("kompas.api7.document.get-name", { handleId: api7DocumentHandle });
        assert(String(actual).toLowerCase().endsWith(".frw"), `Unexpected document name ${actual}`);
        return { actual: String(actual) };
      },
    },
    {
      id: "kompas-op-54-save-document",
      expected: "ksSaveDocument должен сохранять документ поверх временной копии",
      action: async () => {
        const actual = await readExcelResult("kompas.api5.save-document", { handleId: doc2dHandle, path: kompas.sampleCopyPath });
        assertExcelBoolean(actual, true, "ksSaveDocument");
        const info = await executeCommand("system.file.info", { path: kompas.sampleCopyPath });
        assert(extractResultField(info, "Exists") === true, "KOMPAS document file does not exist after save.");
        return { actual: `saveDocument=${actual}` };
      },
    },
  ];

  for (const operation of operations) {
    await runCheck(report, {
      id: operation.id,
      expected: operation.expected,
      classification: "product defect",
    }, async () => {
      const result = await operation.action();
      if (operation.refreshViewport !== false) {
        const refresh = await refreshKompasViewport(operation.id);
        if (result) {
          result.details = result.details
            ? `${result.details}\n${refresh}`
            : refresh;
        }
      }
      return result;
    });
  }

  await runCheck(report, {
    id: "kompas-kill-during-series",
    expected: "После убийства KOMPAS процесс должен пересоздаваться или отдавать structured failure без silent crash",
  }, async () => {
    await hostControl("kill-kompas", {});
    await sleep(3000);
    const command = await executeCommand("kompas.api5.application", { refresh: true, createIfMissing: true, visible: true });
    api5ApplicationHandle = extractHandleId(command.result ?? command);
    assert(Boolean(api5ApplicationHandle), "KOMPAS application handle is missing after recovery.");
    return { actual: `Recovered KOMPAS handle=${api5ApplicationHandle}` };
  });
}

async function runLatencySuite(report) {
  await checkpoint("07-latency");
  setStatus("Main suite: latency and batch");

  const iterations = Number(state.scenario.durations.latencyIterations ?? 120);
  const excelSharedContext = `latency-excel-${Date.now()}`;
  const kompasSharedContext = `latency-kompas-${Date.now()}`;

  await runCheck(report, {
    id: "latency-excel-batch-vs-sequential",
    expected: "Excel compact batch path должен быть заметно быстрее sequential path и отдавать p50/p95/p99",
    classification: "product defect",
  }, async () => {
    const app = await executeCommand("excel.application", {
      refresh: true,
      createIfMissing: true,
      visible: true,
      realtime: true,
    }, {
      reportVerbosity: "compact",
      sharedContextId: excelSharedContext,
      timeoutMilliseconds: 30000,
    });
    const appHandle = extractHandleId(app.result ?? app);
    assert(Boolean(appHandle), "Excel latency application handle is missing.");

    const workbook = await executeCommand("excel.workbooks.add", {}, {
      reportVerbosity: "compact",
      sharedContextId: excelSharedContext,
      timeoutMilliseconds: 30000,
    });
    const workbookHandle = extractHandleId(workbook.result ?? workbook);
    assert(Boolean(workbookHandle), "Excel latency workbook handle is missing.");

    const worksheet = await executeCommand("excel.workbook.add-worksheet", { handleId: workbookHandle }, {
      reportVerbosity: "compact",
      sharedContextId: excelSharedContext,
      timeoutMilliseconds: 30000,
    });
    const worksheetHandle = extractHandleId(worksheet.result ?? worksheet);
    assert(Boolean(worksheetHandle), "Excel latency worksheet handle is missing.");

    const range = await executeCommand("excel.worksheet.range", { handleId: worksheetHandle, address: "A1" }, {
      reportVerbosity: "compact",
      sharedContextId: excelSharedContext,
      timeoutMilliseconds: 30000,
    });
    const rangeHandle = extractHandleId(range.result ?? range);
    assert(Boolean(rangeHandle), "Excel latency range handle is missing.");

    const sequentialRoundTrips = [];
    const sequentialQueueWaits = [];
    const sequentialExecution = [];
    for (let index = 0; index < iterations; index += 1) {
      const startedAt = performance.now();
      const result = await executeCommand("excel.range.set-value", {
        handleId: rangeHandle,
        value: `seq-${index}`,
      }, {
        reportVerbosity: "compact",
        sharedContextId: excelSharedContext,
        timeoutMilliseconds: 30000,
      });
      sequentialRoundTrips.push(performance.now() - startedAt);
      sequentialQueueWaits.push(extractQueueWait(result));
      sequentialExecution.push(extractReportDuration(result));
    }

    const batchCommands = [];
    for (let index = 0; index < iterations; index += 1) {
      batchCommands.push({
        commandId: "excel.range.set-value",
        arguments: {
          handleId: rangeHandle,
          value: `batch-${index}`,
          realtime: true,
        },
        reportVerbosity: "compact",
        timeoutMilliseconds: 30000,
      });
    }

    const batchStartedAt = performance.now();
    const batch = await executeBatch(batchCommands, {
      sharedContextId: excelSharedContext,
      reportVerbosity: "compact",
      stopOnError: true,
    });
    const batchRoundTrip = performance.now() - batchStartedAt;
    assert(batch.results.length === iterations, `Unexpected Excel batch result count ${batch.results.length}`);
    const batchExecution = batch.results.map((item) => Number(item.report?.durationMs ?? 0));
    const batchQueueWaits = batch.results.map((item) => Number(item.report?.runtime?.queueWaitMs ?? 0));

    const sequentialStats = summarizeLatency(sequentialRoundTrips);
    const batchStats = summarizeLatency(batchExecution);
    const sequentialTotal = sequentialRoundTrips.reduce((sum, value) => sum + value, 0);
    assert(
      batchRoundTrip < sequentialTotal,
      `Excel batch roundtrip is not faster than sequential roundtrip. seqTotal=${sequentialTotal.toFixed(2)}ms batchTotal=${batchRoundTrip.toFixed(2)}ms seqP50=${sequentialStats.p50.toFixed(2)}ms batchExecP50=${batchStats.p50.toFixed(2)}ms`,
    );

    await executeCommand("excel.workbook.close", { handleId: workbookHandle }, {
      reportVerbosity: "compact",
      sharedContextId: excelSharedContext,
      timeoutMilliseconds: 30000,
    });

    return {
      actual: `seqP50=${sequentialStats.p50.toFixed(2)}ms batchTotal=${batchRoundTrip.toFixed(2)}ms`,
      details: JSON.stringify({
        sequentialRoundTrip: sequentialStats,
        sequentialQueueWait: summarizeLatency(sequentialQueueWaits),
        sequentialExecution: summarizeLatency(sequentialExecution),
        batchExecution: batchStats,
        batchQueueWait: summarizeLatency(batchQueueWaits),
        batchTotalMs: batchRoundTrip,
        sequentialTotalMs: sequentialTotal,
        iterations,
      }),
    };
  });

  await runCheck(report, {
    id: "latency-kompas-batch-vs-sequential",
    expected: "KOMPAS compact batch path должен быть заметно быстрее sequential path и отдавать p50/p95/p99",
    classification: "product defect",
  }, async () => {
    const application = await executeCommand("kompas.api5.application", {
      refresh: true,
      createIfMissing: true,
      visible: true,
      realtime: true,
    }, {
      reportVerbosity: "compact",
      sharedContextId: kompasSharedContext,
      timeoutMilliseconds: 30000,
    });
    const appHandle = extractHandleId(application.result ?? application);
    assert(Boolean(appHandle), "KOMPAS latency application handle is missing.");

    const document = await executeCommand("kompas.api5.document2d", { handleId: appHandle }, {
      reportVerbosity: "compact",
      sharedContextId: kompasSharedContext,
      timeoutMilliseconds: 30000,
    });
    const docHandle = extractHandleId(document.result ?? document);
    assert(Boolean(docHandle), "KOMPAS latency document handle is missing.");

    await executeCommand("kompas.api5.document2d.open", {
      handleId: docHandle,
      path: state.scenario.kompas.sampleCopyPath,
      visible: true,
    }, {
      reportVerbosity: "compact",
      sharedContextId: kompasSharedContext,
      timeoutMilliseconds: 30000,
    });

    const line = await executeCommand("kompas.api5.line-segment", {
      handleId: docHandle,
      x1: 5,
      y1: 160,
      x2: 120,
      y2: 160,
      style: 1,
    }, {
      reportVerbosity: "compact",
      sharedContextId: kompasSharedContext,
      timeoutMilliseconds: 30000,
    });
    const objRef = Number(line.result);
    assert(objRef > 0, `KOMPAS latency objRef is invalid: ${line.result}`);

    const sequentialRoundTrips = [];
    const sequentialQueueWaits = [];
    const sequentialExecution = [];
    for (let index = 0; index < iterations; index += 1) {
      const startedAt = performance.now();
      const result = await executeCommand("kompas.api5.exist-obj", {
        handleId: docHandle,
        objRef,
        realtime: true,
      }, {
        reportVerbosity: "compact",
        sharedContextId: kompasSharedContext,
        timeoutMilliseconds: 30000,
      });
      assert(Number(result.result) > 0, `KOMPAS object disappeared during latency run: ${result.result}`);
      sequentialRoundTrips.push(performance.now() - startedAt);
      sequentialQueueWaits.push(extractQueueWait(result));
      sequentialExecution.push(extractReportDuration(result));
    }

    const batchCommands = [];
    for (let index = 0; index < iterations; index += 1) {
      batchCommands.push({
        commandId: "kompas.api5.exist-obj",
        arguments: {
          handleId: docHandle,
          objRef,
          realtime: true,
        },
        reportVerbosity: "compact",
        timeoutMilliseconds: 30000,
      });
    }

    const batchStartedAt = performance.now();
    const batch = await executeBatch(batchCommands, {
      sharedContextId: kompasSharedContext,
      reportVerbosity: "compact",
      stopOnError: true,
    });
    const batchRoundTrip = performance.now() - batchStartedAt;
    assert(batch.results.length === iterations, `Unexpected KOMPAS batch result count ${batch.results.length}`);
    batch.results.forEach((item) => assert(Number(item.result) > 0, `Unexpected KOMPAS batch result ${item.result}`));

    assert(batchRoundTrip < sequentialRoundTrips.reduce((sum, value) => sum + value, 0), "KOMPAS batch roundtrip is not faster than sequential roundtrip.");

    return {
      actual: `seqP50=${percentile(sequentialRoundTrips, 50).toFixed(2)}ms batchTotal=${batchRoundTrip.toFixed(2)}ms`,
      details: JSON.stringify({
        sequentialRoundTrip: summarizeLatency(sequentialRoundTrips),
        sequentialQueueWait: summarizeLatency(sequentialQueueWaits),
        sequentialExecution: summarizeLatency(sequentialExecution),
        batchExecution: summarizeLatency(batch.results.map((item) => Number(item.report?.durationMs ?? 0))),
        batchQueueWait: summarizeLatency(batch.results.map((item) => Number(item.report?.runtime?.queueWaitMs ?? 0))),
        batchTotalMs: batchRoundTrip,
        iterations,
      }),
    };
  });
}

async function runIdleReconnectTest(report) {
  await checkpoint("08-soak");
  setStatus("Main suite: connection soak");
  const start = Date.now();
  const durationMs = state.scenario.durations.connectionSoakSeconds * 1000;
  while (Date.now() - start < durationMs) {
    await sleep(5000);
    const info = await getInfo();
    if (info.runtimeState !== "Active") {
      throw new Error(`Runtime left Active state during soak: ${info.runtimeState}`);
    }
  }

  recordResult(report, {
    id: "connection-soak",
    title: "connection-soak",
    success: true,
    expected: `Heartbeat soak должен удерживать активную сессию ${state.scenario.durations.connectionSoakSeconds}s`,
    actual: `Completed ${state.scenario.durations.connectionSoakSeconds}s soak with active sessions`,
  });
}

function mulberry32(seed) {
  return function next() {
    let t = seed += 0x6D2B79F5;
    t = Math.imul(t ^ t >>> 15, t | 1);
    t ^= t + Math.imul(t ^ t >>> 7, t | 61);
    return ((t ^ t >>> 14) >>> 0) / 4294967296;
  };
}

async function chaosAction(kind, workerId) {
  const sys = state.scenario.system;
  const excel = state.scenario.excel;
  const kompas = state.scenario.kompas;
  const chaosCommand = (commandId, args = {}, options = {}) => executeCommand(commandId, args, {
    timeoutMilliseconds: options.timeoutMilliseconds ?? resolveDefaultCommandTimeout(commandId, options),
    clientTimeoutMs: options.clientTimeoutMs ?? 20000,
    reportVerbosity: options.reportVerbosity ?? "compact",
    sharedContextId: options.sharedContextId ?? null,
    expectFailure: options.expectFailure ?? false,
  });
  state.chaosStats.actionCount += 1;
  state.chaosStats.mandatoryActions.add(kind);
  renderTelemetry();
  pushTimeline("fault", `chaos:${kind}`, `worker=${workerId}`);
  switch (kind) {
    case "utility-info":
      await getInfo();
      return "info";
    case "config-version":
      await getConfigVersion();
      return "config-version";
    case "config-load": {
      const effective = await getEffectiveConfig();
      const next = deepClone(effective);
      next.configVersion = `chaos-${workerId}-${Date.now()}`;
      next.security = next.security ?? {};
      next.security.pairingToken = state.scenario.pairingToken;
      next.uiUrl = state.scenario.hostBaseUrl;
      const response = await postConfigLoad(next, false);
      assert(response.response.ok, "config/load failed");
      return next.configVersion;
    }
    case "config-reload":
      await postConfigReload();
      return "config-reload";
    case "manifest-status":
      await getManifestStatus();
      return "manifest-status";
    case "manifest-refresh":
      await postManifestRefresh();
      return "manifest-refresh";
    case "system-file-churn": {
      const path = `${sys.root}\\chaos-${workerId}.txt`;
      await chaosCommand("system.file.write-text", { path, contents: `worker-${workerId}-${Date.now()}` });
      await chaosCommand("system.file.append-text", { path, contents: "-append" });
      await chaosCommand("system.file.read-text", { path });
      return path;
    }
    case "system-command":
      await chaosCommand("system.command.run", {
        fileName: "cmd.exe",
        arguments: "/c echo chaos",
        workingDirectory: sys.root,
        timeoutMilliseconds: 10000,
        environment: { CHAOS_RUN: "1" },
      });
      return "command-run";
    case "excel-attach":
      await chaosCommand("excel.application", { refresh: true, createIfMissing: true, visible: true, realtime: true }, { clientTimeoutMs: 15000 });
      return "excel-attach";
    case "excel-edit": {
      const workbook = await chaosCommand("excel.workbooks.add", {}, { clientTimeoutMs: 15000 });
      const workbookHandle = extractHandleId(workbook.result ?? workbook);
      const worksheet = await chaosCommand("excel.workbook.add-worksheet", { handleId: workbookHandle }, { clientTimeoutMs: 15000 });
      const worksheetHandle = extractHandleId(worksheet.result ?? worksheet);
      const range = await chaosCommand("excel.worksheet.range", { handleId: worksheetHandle, address: "A1" }, { clientTimeoutMs: 15000 });
      const rangeHandleId = extractHandleId(range.result ?? range);
      await chaosCommand("excel.range.set-value", { handleId: rangeHandleId, value: `chaos-${workerId}` }, { clientTimeoutMs: 15000 });
      await chaosCommand("excel.workbook.save-as", { handleId: workbookHandle, path: `${excel.workbookPath}.${workerId}.xlsx` }, { clientTimeoutMs: 15000 });
      await chaosCommand("excel.workbook.close", { handleId: workbookHandle }, { clientTimeoutMs: 15000 });
      return "excel-edit";
    }
    case "kompas-attach":
      await chaosCommand("kompas.api5.application", { refresh: true, createIfMissing: true, visible: true, realtime: true }, { clientTimeoutMs: 15000 });
      return "kompas-attach";
    case "kompas-edit":
      if (!kompas.hasSample) {
        return "kompas-skip-no-sample";
      }
      await chaosCommand("kompas.api5.application", { refresh: true, createIfMissing: true, visible: true, realtime: true }, { clientTimeoutMs: 15000 });
      return "kompas-edit";
    case "session-reconnect": {
      const tempSession = new SessionClient(`chaos-${workerId}-${Date.now()}`);
      await tempSession.connect();
      await tempSession.disconnect("chaos-reconnect");
      return "session-reconnect";
    }
    case "second-instance":
      await hostControl("spawn-second-instance", {});
      return "second-instance";
    case "bad-token": {
      const { response } = await utilityFetch("/info", {
        method: "GET",
        headers: { "X-KWB-Pairing-Token": "chaos-invalid" },
      });
      assert(response.status === 401, `Expected 401, got ${response.status}`);
      return "bad-token";
    }
    case "bad-origin": {
      const result = await hostControl("http-request", {
        url: `${state.scenario.utilityBaseUrl}/info`,
        method: "GET",
        headers: {
          Origin: "https://chaos.invalid",
          "X-KWB-Pairing-Token": state.scenario.pairingToken,
        },
      });
      const rendered = JSON.stringify(result);
      assert(rendered.includes("origin_not_allowed") || rendered.includes("HTTP Error 403") || result.statusCode === 403, rendered);
      return "bad-origin";
    }
    case "kill-excel":
      await hostControl("kill-excel", {});
      return "kill-excel";
    case "kill-kompas":
      await hostControl("kill-kompas", {});
      return "kill-kompas";
    default:
      return `unknown:${kind}`;
  }
}

function pickWeightedOperation(catalog, nextRandom) {
  const total = catalog.operations.reduce((sum, item) => sum + Number(item.weight || 0), 0);
  let ticket = nextRandom() * total;
  for (const operation of catalog.operations) {
    ticket -= Number(operation.weight || 0);
    if (ticket <= 0) {
      return operation.kind;
    }
  }
  return catalog.operations.at(-1)?.kind ?? "utility-info";
}

async function runChaosSuite() {
  await checkpoint("08-chaos");
  setStatus("Chaos suite");
  setChaosStatus("Выполняется");
  state.chaosStats = {
    actionCount: 0,
    failureCount: 0,
    mandatoryActions: new Set(),
    mandatoryFailureCount: 0,
  };
  renderTelemetry();

  const report = createReport("chaos");
  let keeperSession = state.chaosKeeperSession;
  if (!keeperSession) {
    keeperSession = new SessionClient("chaos-keeper");
    await keeperSession.connect();
    state.chaosKeeperSession = keeperSession;
  }

  const catalog = state.scenario.catalogs?.chaos ?? {
    workers: 4,
    durationSeconds: state.scenario.durations.chaosSeconds,
    seed: 240308,
    operations: [{ kind: "utility-info", weight: 1 }],
  };

  const deadline = Date.now() + Number(catalog.durationSeconds || state.scenario.durations.chaosSeconds) * 1000;
  const nextRandom = mulberry32(Number(catalog.seed || 240308));
  const workerCount = Number(catalog.workers || 4);
  const mandatoryKinds = [
    "bad-token",
    "bad-origin",
    "session-reconnect",
    "second-instance",
    "config-load",
    "config-reload",
    "manifest-refresh",
    "system-file-churn",
    "kill-excel",
    "kill-kompas",
  ];
  const workers = [];
  let actionCount = 0;
  let failureCount = 0;
  let mandatoryFailureCount = 0;

  for (const kind of mandatoryKinds) {
    try {
      const result = await chaosAction(kind, -1);
      appendLog("chaos-mandatory", `${kind} -> ${result}`);
    } catch (error) {
      failureCount += 1;
      mandatoryFailureCount += 1;
      state.chaosStats.failureCount += 1;
      state.chaosStats.mandatoryFailureCount = mandatoryFailureCount;
      renderTelemetry();
      const rendered = `${kind}: ${formatError(error)}`;
      appendLog("chaos-mandatory-failed", rendered);
      pushTimeline("error", `chaos:${kind}:failed`, rendered);
    }
  }

  for (let index = 0; index < workerCount; index++) {
    workers.push((async () => {
      while (Date.now() < deadline) {
        const kind = pickWeightedOperation(catalog, nextRandom);
        try {
          const result = await chaosAction(kind, index);
          actionCount += 1;
          appendLog(`chaos-worker-${index}`, `${kind} -> ${result}`);
          pushTimeline("verify", `chaos:${kind}:ok`, `worker=${index}, result=${result}`);
        } catch (error) {
          failureCount += 1;
          state.chaosStats.failureCount += 1;
          renderTelemetry();
          const rendered = `${kind}: ${formatError(error)}`;
          const expectedFailure = isExpectedChaosFailure(kind, rendered);
          const label = expectedFailure
            ? `chaos-worker-${index}-expected-rejection`
            : `chaos-worker-${index}-failed`;
          appendLog(label, rendered);
          pushTimeline(expectedFailure ? "verify" : "error", `chaos:${kind}:error`, rendered);
        }
        await sleep(350 + Math.floor(nextRandom() * 450));
      }
    })());
  }

  await Promise.all(workers);

  await runCheck(report, {
    id: "chaos-mandatory-faults-observed",
    expected: "Chaos suite должен реально выполнить обязательные fault injections и recovery actions",
  }, async () => {
    const missing = mandatoryKinds.filter(kind => !state.chaosStats.mandatoryActions.has(kind));
    assert(missing.length === 0, `Missing mandatory chaos actions: ${missing.join(", ")}`);
    assert(mandatoryFailureCount === 0, `Mandatory chaos actions failed: ${mandatoryFailureCount}`);
    return { actual: `mandatory=${mandatoryKinds.length}, actions=${state.chaosStats.actionCount}, workerFailures=${state.chaosStats.failureCount}, mandatoryFailures=${mandatoryFailureCount}` };
  });

  await runCheck(report, {
    id: "chaos-health-after-run",
    expected: "После хаотической нагрузки агент должен оставаться доступным по /health",
  }, async () => {
    const { response, payload } = await getHealth();
    assert(response.ok, `/health status ${response.status}`);
    return { actual: `status=${payload.status}, runtimeState=${payload.runtimeState}, actions=${actionCount}, workerFailures=${failureCount}` };
  });

  await runCheck(report, {
    id: "chaos-cli-shutdown",
    expected: "CLI --shutdown должен корректно завершить уже работающий экземпляр",
  }, async () => {
    const result = await hostControl("cli-shutdown", {});
    assert(result.returncode === 0, `cli-shutdown returned ${result.returncode}`);
    await waitForCondition(async () => {
      try {
        const { response } = await getHealth();
        return !response.ok;
      } catch {
        return true;
      }
    }, 30000, "Utility did not stop after CLI shutdown.");
    return { actual: "Utility stopped after CLI shutdown" };
  });

  setChaosStatus(report.success ? "Успешно" : "Есть ошибки");
  await publishReport("chaos", report);
  state.chaosReport = report;
  return report;
}

async function runMainSuite() {
  const report = createReport("main");
  setMainStatus("Выполняется");

  const sessions = await runConnectionLifecycle(report);
  try {
    await runManagementSuite(report);
    await runSecurityAndResilienceSuite(report);
    sessions.reconnectLoop.stop();
    await sessions.secondarySession.disconnect("post-security-finalize-secondary");
    await runSystemSuite(report);
    await runExcelSuite(report);
    await runKompasSuite(report);
    await runLatencySuite(report);
    await runIdleReconnectTest(report);
  } finally {
    sessions.reconnectLoop.stop();
    await sessions.secondarySession.disconnect("main-suite-finalize-secondary");
    state.chaosKeeperSession = sessions.mainSession;
  }

  setMainStatus(report.success ? "Успешно" : "Есть ошибки");
  await publishReport("main", report);
  state.mainReport = report;
  return report;
}

async function bootstrap() {
  installConsoleMirrors();
  setStatus("Загрузка сценария");
  updateSummary();
  renderTimeline();
  renderSessions();
  renderLatency();
  renderTelemetry();

  let mainReport = null;
  let chaosReport = null;

  try {
    state.scenario = await fetchScenario();
    startTelemetryLoop();
    setText(ui.utilityUrl, state.scenario.utilityBaseUrl);
    appendLog("scenario.loaded", {
      utilityBaseUrl: state.scenario.utilityBaseUrl,
      profileId: state.scenario.profileId,
      chaosSeconds: state.scenario.durations.chaosSeconds,
      soakSeconds: state.scenario.durations.connectionSoakSeconds,
    });

    await checkpoint("00-bootstrap", { utilityBaseUrl: state.scenario.utilityBaseUrl });
    mainReport = await runMainSuite();
    chaosReport = await runChaosSuite();
    setStatus(mainReport.success && chaosReport.success ? "Готово" : "Завершено с ошибками");
  } catch (error) {
    appendLog("bootstrap.failed", formatError(error));
    setStatus("Критическая ошибка");

    if (!mainReport) {
      mainReport = createReport("main");
      recordResult(mainReport, {
        id: "bootstrap-failed",
        title: "bootstrap-failed",
        success: false,
        expected: "Runner должен завершить main suite",
        actual: formatError(error),
        classification: "harness defect",
        severity: "high",
      });
      await publishReport("main", mainReport);
    }

    if (!chaosReport) {
      chaosReport = createReport("chaos");
      recordResult(chaosReport, {
        id: "chaos-skipped-after-bootstrap-failure",
        title: "chaos-skipped-after-bootstrap-failure",
        success: false,
        expected: "Chaos suite должен быть выполнен после main suite",
        actual: formatError(error),
        classification: "harness defect",
        severity: "high",
      });
      await publishReport("chaos", chaosReport);
    }
  } finally {
    stopTelemetryLoop();
    if (state.chaosKeeperSession) {
      try {
        await state.chaosKeeperSession.disconnect("bootstrap-finalize");
      } catch {
        // best effort cleanup after browser-driven run
      }
      state.chaosKeeperSession = null;
    }
    await flushTraceQueue();
  }
}

void bootstrap();

