/* =========================================================================
   Daemons tab (Spec 1000 · D2)
   Status cards from GET /api/daemons and a live console polling
   GET /api/daemons/activity. Display only: nothing here evaluates KPIs or
   decides health - the backend already did (contracts-daemons §5).
   Depends on app.js globals: escapeHtml, getAntiforgeryToken, state.
   ========================================================================= */
'use strict';

const DaemonsTab = (() => {
  const CONSOLE_POLL_MS = 3000;
  const CARDS_REFRESH_MS = 15000;
  const FIRST_PAGE_TOP = 200;
  const POLL_TOP = 500;
  const MAX_LINES = 2000;
  /* Identity values can commit out of order under concurrent writers, so every poll re-reads the
     last OVERLAP_IDS ids and de-duplicates by activityId instead of trusting a strict afterId. */
  const OVERLAP_IDS = 100;

  const DAEMONS = [
    { key: 'kpi-ingestion', name: 'KPI Ingestion', short: 'ingest', color: 'ingest' },
    { key: 'application-health-monitor', name: 'Application Health Monitor', short: 'app-mon', color: 'app' },
    { key: 'business-health-monitor', name: 'Business Health Monitor', short: 'biz-mon', color: 'biz' },
    { key: 'infrastructure-health-monitor', name: 'Infrastructure Health Monitor', short: 'infra-mon', color: 'infra' },
    { key: 'triage-monkey', name: 'Triage Monkey', short: 'triage', color: 'triage' },
  ];
  const DAEMON_BY_KEY = Object.fromEntries(DAEMONS.map((d) => [d.key, d]));
  /* Triage Monkey runs through the Inspector runner (contracts-daemons §5 "API"). */
  const RUN_ID_BY_KEY = { 'triage-monkey': 'inspector-monkey' };
  const LEVEL_RANK = { info: 0, warning: 1, error: 2 };
  const HEALTH_LABELS = {
    ok: 'OK', running: 'Running', stale: 'Stale', error: 'Error', disabled: 'Disabled', 'never-run': 'Never run',
  };

  const s = {
    wired: false,
    consoleTimer: null,
    cardsTimer: null,
    /** Bumped whenever the server-side filters change, so a late response for old filters is dropped. */
    generation: 0,
    consoleInFlight: false,
    cardsInFlight: false,
    /** Lines held in memory, ascending by activityId, at most MAX_LINES. */
    entries: [],
    /** activityIds already received: entries plus the overlap window of anything cleared from the view. */
    seen: new Set(),
    lastSeenId: 0,
    firstLoadDone: false,
    cleared: false,
    expanded: new Set(),
    filters: { daemon: '', level: 'Info', search: '', runId: '' },
    paused: false,
    autoScroll: true,
    fullscreen: false,
    connection: 'idle',
    lastError: '',
    statuses: null,
    statusError: '',
    runBusy: {},
    runFeedback: {},
  };

  const el = (id) => document.getElementById(id);

  function isVisible() {
    const panel = el('tab-daemons');
    return document.visibilityState === 'visible' && !!panel && !panel.hidden;
  }

  function norm(value) {
    return String(value ?? '').toLowerCase();
  }

  function pad2(n) {
    return String(n).padStart(2, '0');
  }

  /* Backend messages quote UTC times (contracts-daemons §5), so the console and cards speak UTC too;
     the browser's local time is only offered as a tooltip. */
  function utcClock(iso) {
    const d = new Date(iso);
    if (Number.isNaN(d.getTime())) return '--:--:--';
    return `${pad2(d.getUTCHours())}:${pad2(d.getUTCMinutes())}:${pad2(d.getUTCSeconds())}`;
  }

  function utcDateTime(iso) {
    const d = new Date(iso);
    if (Number.isNaN(d.getTime())) return String(iso ?? '');
    return `${d.getUTCFullYear()}-${pad2(d.getUTCMonth() + 1)}-${pad2(d.getUTCDate())} ${utcClock(iso)} UTC`;
  }

  function localDateTime(iso) {
    const d = new Date(iso);
    return Number.isNaN(d.getTime()) ? String(iso ?? '') : `${d.toLocaleString()} (local)`;
  }

  function shortId(id) {
    return String(id ?? '').slice(0, 8);
  }

  function ago(iso) {
    const t = new Date(iso).getTime();
    if (Number.isNaN(t)) return '—';
    const secs = Math.max(0, Math.round((Date.now() - t) / 1000));
    if (secs < 60) return `${secs}s ago`;
    const mins = Math.round(secs / 60);
    if (mins < 60) return `${mins} min ago`;
    const hours = Math.round(mins / 60);
    if (hours < 48) return `${hours} h ago`;
    return `${Math.round(hours / 24)} d ago`;
  }

  function daemonInfo(key) {
    return DAEMON_BY_KEY[key] || { key, name: key, short: key, color: 'other' };
  }

  /* ---------------------------------------------------------------- data */

  function activityUrl(afterId, top) {
    const params = new URLSearchParams();
    params.set('afterId', String(afterId));
    params.set('top', String(top));
    if (s.filters.daemon) params.set('daemon', s.filters.daemon);
    if (s.filters.level && s.filters.level !== 'Info') params.set('level', s.filters.level);
    return withActiveCompany(`/api/daemons/activity?${params.toString()}`);
  }

  async function pollConsole(force) {
    if (s.consoleInFlight) return;
    if (!force && (!isVisible() || s.paused)) return;
    s.consoleInFlight = true;
    const generation = s.generation;
    const first = !s.firstLoadDone;
    const url = first
      ? activityUrl(0, FIRST_PAGE_TOP)
      : activityUrl(Math.max(0, s.lastSeenId - OVERLAP_IDS), POLL_TOP);
    try {
      const response = await fetch(url, { headers: { Accept: 'application/json' } });
      if (!response.ok) throw new Error(`HTTP ${response.status}`);
      const page = await response.json();
      if (generation !== s.generation) return;
      s.firstLoadDone = true;
      s.connection = s.paused ? 'paused' : 'live';
      s.lastError = '';
      const added = mergeEntries(Array.isArray(page && page.entries) ? page.entries : []);
      if (added || first) renderConsole();
      else renderConsoleStatus();
    } catch (err) {
      if (generation !== s.generation) return;
      s.connection = 'error';
      s.lastError = err && err.message ? err.message : 'request failed';
      renderConsoleStatus();
      if (!s.firstLoadDone) renderConsole();
    } finally {
      // A reload already released the flag for the new filters; do not release the new request's.
      if (generation === s.generation) s.consoleInFlight = false;
    }
  }

  /** Adds unseen entries, keeps them sorted by activityId and trims to MAX_LINES. Returns how many were new. */
  function mergeEntries(incoming) {
    let added = 0;
    for (const entry of incoming) {
      const id = Number(entry && entry.activityId);
      if (!Number.isFinite(id) || s.seen.has(id)) continue;
      s.seen.add(id);
      s.entries.push({ ...entry, activityId: id });
      if (id > s.lastSeenId) s.lastSeenId = id;
      added += 1;
    }
    if (!added) return 0;
    s.cleared = false;
    s.entries.sort((a, b) => a.activityId - b.activityId);
    if (s.entries.length > MAX_LINES) {
      const dropped = s.entries.splice(0, s.entries.length - MAX_LINES);
      for (const e of dropped) {
        s.seen.delete(e.activityId);
        s.expanded.delete(e.activityId);
      }
    }
    return added;
  }

  /** Server-side filters changed: start over from the latest page with the new filters. */
  function reloadConsole() {
    s.generation += 1;
    s.consoleInFlight = false;
    s.entries = [];
    s.seen = new Set();
    s.expanded = new Set();
    s.lastSeenId = 0;
    s.firstLoadDone = false;
    s.cleared = false;
    s.connection = 'loading';
    renderConsole();
    pollConsole(true);
  }

  /** Clears only the view. Ids inside the overlap window stay "seen" so the next poll cannot bring them back. */
  function clearView() {
    const floor = s.lastSeenId - OVERLAP_IDS;
    s.seen = new Set([...s.seen].filter((id) => id > floor));
    s.entries = [];
    s.expanded = new Set();
    s.cleared = true;
    renderConsole();
  }

  async function loadStatuses(force) {
    if (s.cardsInFlight) return;
    if (!force && !isVisible()) return;
    s.cardsInFlight = true;
    try {
      const response = await fetch(withActiveCompany('/api/daemons'), { headers: { Accept: 'application/json' } });
      if (!response.ok) throw new Error(`HTTP ${response.status}`);
      const body = await response.json();
      s.statuses = Array.isArray(body) ? body : [];
      s.statusError = '';
    } catch (err) {
      s.statusError = err && err.message ? err.message : 'request failed';
    } finally {
      s.cardsInFlight = false;
    }
    renderCards();
    if (!s.entries.length) renderConsole();
  }

  async function runNow(daemonKey) {
    if (s.runBusy[daemonKey]) return;
    const runId = RUN_ID_BY_KEY[daemonKey] || daemonKey;
    s.runBusy[daemonKey] = true;
    s.runFeedback[daemonKey] = { kind: 'info', text: 'Running…' };
    renderCards();
    try {
      const token = typeof getAntiforgeryToken === 'function' ? await getAntiforgeryToken() : '';
      const response = await fetch(`/api/monkeys/${encodeURIComponent(runId)}/run`, {
        method: 'POST',
        headers: { 'X-CSRF-TOKEN': token },
      });
      const body = await response.json().catch(() => ({}));
      if (!response.ok) throw new Error(body.error || `Request failed: ${response.status}`);
      const when = `${utcClock(body.ranAt || new Date().toISOString())} UTC`;
      const counts = [];
      if (body.diagnosed != null) counts.push(`${body.diagnosed} processed`);
      if (body.newFindings != null) counts.push(`${body.newFindings} new`);
      s.runFeedback[daemonKey] = { kind: 'ok', text: `Ran at ${when}${counts.length ? ` — ${counts.join(', ')}` : ''}` };
    } catch (err) {
      s.runFeedback[daemonKey] = { kind: 'error', text: `Run failed — ${err && err.message ? err.message : 'request failed'}` };
    } finally {
      s.runBusy[daemonKey] = false;
    }
    renderCards();
    if (isVisible()) {
      loadStatuses(true);
      if (!s.paused) pollConsole(true);
    }
  }

  /* -------------------------------------------------------------- render */

  function renderCards() {
    const host = el('daemons-cards');
    if (!host) return;
    if (!s.statuses) {
      host.innerHTML = s.statusError
        ? `<p class="daemon-cards__msg daemon-cards__msg--error">Could not load daemon status (${escapeHtml(s.statusError)}). Retrying every 15 s.</p>`
        : '<p class="daemon-cards__msg">Loading daemon status…</p>';
      return;
    }
    const warning = s.statusError
      ? `<p class="daemon-cards__msg daemon-cards__msg--error">Last refresh failed (${escapeHtml(s.statusError)}); showing the previous status.</p>`
      : '';
    if (!s.statuses.length) {
      host.innerHTML = `${warning}<p class="daemon-cards__msg">No daemons reported.</p>`;
      return;
    }
    host.innerHTML = warning + s.statuses.map(renderCard).join('');
  }

  function renderCard(st) {
    const key = String(st.daemonKey ?? '');
    const info = daemonInfo(key);
    const health = norm(st.health) || 'never-run';
    const healthLabel = HEALTH_LABELS[health] || st.health;
    const disabled = health === 'disabled' || st.enabled === false;
    const busy = !!s.runBusy[key];
    const feedback = s.runFeedback[key];
    const lastRun = st.lastRunAt
      ? `<span title="${escapeHtml(utcDateTime(st.lastRunAt))}">${escapeHtml(ago(st.lastRunAt))}</span>`
      : '<span>never</span>';
    const summary = st.lastRunSummary
      ? `<div class="daemon-card__summary">${escapeHtml(st.lastRunSummary)}</div>`
      : '';
    const lastError = st.lastErrorMessage
      ? `<div class="daemon-card__error"><span class="daemon-card__error-time">${escapeHtml(st.lastErrorAt ? `${utcDateTime(st.lastErrorAt)} (${ago(st.lastErrorAt)})` : '')}</span> ${escapeHtml(st.lastErrorMessage)}</div>`
      : '';
    const mode = st.mode ? `<div><dt>Mode</dt><dd>${escapeHtml(st.mode)}</dd></div>` : '';
    return `
      <article class="daemon-card daemon-card--${escapeHtml(health)}" data-daemon-card="${escapeHtml(key)}">
        <header class="daemon-card__head">
          <span class="daemon-card__name"><span class="daemon-dot daemon-dot--${escapeHtml(info.color)}" aria-hidden="true"></span>${escapeHtml(st.displayName || info.name)}</span>
          <span class="daemon-health daemon-health--${escapeHtml(health)}">${escapeHtml(healthLabel)}</span>
        </header>
        <dl class="daemon-card__meta">
          <div><dt>Schedule</dt><dd><code>${escapeHtml(st.schedule || '—')}</code></dd></div>
          ${mode}
          <div><dt>Last run</dt><dd>${lastRun}</dd></div>
        </dl>
        ${summary}
        ${lastError}
        <div class="daemon-card__actions">
          <button type="button" class="btn btn--sm btn--ghost" data-daemon-run="${escapeHtml(key)}" ${disabled || busy ? 'disabled' : ''}
            title="${disabled ? 'Daemon is disabled' : 'Trigger a run now'}">${busy ? 'Running…' : 'Run now'}</button>
          ${feedback ? `<span class="daemon-card__feedback daemon-card__feedback--${escapeHtml(feedback.kind)}" role="status">${escapeHtml(feedback.text)}</span>` : ''}
        </div>
      </article>`;
  }

  function lineMatches(entry) {
    if (s.filters.runId && norm(entry.runId) !== norm(s.filters.runId)) return false;
    const needle = norm(s.filters.search).trim();
    if (!needle) return true;
    const info = daemonInfo(entry.daemonKey);
    return [entry.message, entry.kind, entry.daemonKey, info.short, entry.level, entry.runId]
      .some((v) => norm(v).includes(needle));
  }

  function renderLine(entry) {
    const level = norm(entry.level) || 'info';
    const info = daemonInfo(entry.daemonKey);
    const id = entry.activityId;
    const open = s.expanded.has(id);
    let detail = '';
    if (open) {
      let data = entry.dataJson;
      if (data) {
        try { data = JSON.stringify(JSON.parse(data), null, 2); } catch { /* raw text is still useful */ }
      }
      detail = `
        <div class="dconsole__detail">
          <div><span class="dconsole__detail-key">activityId</span> ${escapeHtml(id)}</div>
          <div><span class="dconsole__detail-key">at</span> ${escapeHtml(utcDateTime(entry.at))} · ${escapeHtml(localDateTime(entry.at))}</div>
          <div><span class="dconsole__detail-key">runId</span> ${escapeHtml(entry.runId)}
            <button type="button" class="dconsole__run-btn" data-run-filter="${escapeHtml(entry.runId)}">Filter by this run</button></div>
          <div><span class="dconsole__detail-key">dataJson</span></div>
          <pre class="dconsole__json">${data ? escapeHtml(data) : '(none)'}</pre>
        </div>`;
    }
    return `<div class="dconsole__line dconsole__line--${escapeHtml(level)}${open ? ' is-open' : ''}" data-line-id="${escapeHtml(id)}" role="button" tabindex="0" aria-expanded="${open}">`
      + `<span class="dconsole__time" title="${escapeHtml(localDateTime(entry.at))}">${escapeHtml(utcClock(entry.at))}</span>`
      + `<span class="dconsole__level dconsole__level--${escapeHtml(level)}">${escapeHtml(level.toUpperCase())}</span>`
      + `<span class="dconsole__daemon daemon-fg--${escapeHtml(info.color)}" title="${escapeHtml(info.name)}">${escapeHtml(info.short)}</span>`
      + `<span class="dconsole__kind">${escapeHtml(entry.kind)}</span>`
      + `<span class="dconsole__msg">${escapeHtml(entry.message)}</span>`
      + `${detail}</div>`;
  }

  function emptyMessage() {
    if (!s.firstLoadDone) {
      return s.connection === 'error'
        ? `Could not query the activity log (${escapeHtml(s.lastError)}). Retrying…`
        : 'Loading activity…';
    }
    if (s.cleared) return 'View cleared. New activity will appear here.';
    if (s.entries.length) return 'No lines match the current search or run filter.';
    const statuses = s.statuses || [];
    if (statuses.length && statuses.every((st) => norm(st.health) === 'disabled' || st.enabled === false)) {
      return 'Los daemons están apagados (HealthMonitoring:Enabled=false)';
    }
    if (s.filters.daemon || s.filters.level !== 'Info') return 'No activity for these filters yet.';
    return 'No daemon activity yet. Lines appear here as soon as a daemon runs.';
  }

  function renderConsole() {
    const body = el('daemon-console-body');
    if (!body) return;
    const prevScrollTop = body.scrollTop;
    const visible = s.entries.filter(lineMatches);
    body.innerHTML = visible.length
      ? visible.map(renderLine).join('')
      : `<div class="dconsole__empty">${emptyMessage()}</div>`;
    if (s.autoScroll) body.scrollTop = body.scrollHeight;
    else body.scrollTop = prevScrollTop;
    renderConsoleStatus(visible.length);
  }

  function renderConsoleStatus(shownCount) {
    const shown = typeof shownCount === 'number' ? shownCount : s.entries.filter(lineMatches).length;
    const counter = el('daemon-console-count');
    if (counter) {
      counter.textContent = shown === s.entries.length
        ? `${s.entries.length} line${s.entries.length === 1 ? '' : 's'}`
        : `${shown} of ${s.entries.length} lines`;
    }
    const conn = el('daemon-console-conn');
    if (conn) {
      let state = s.connection;
      if (s.paused) state = 'paused';
      const labels = {
        live: 'Live', paused: 'Paused', error: 'Query error — retrying', loading: 'Loading…', idle: 'Idle',
      };
      conn.className = `daemon-conn daemon-conn--${state}`;
      conn.textContent = labels[state] || state;
      conn.title = state === 'error' ? s.lastError : '';
    }
    const retry = el('daemon-console-retry');
    if (retry) retry.hidden = !(s.connection === 'error' && !s.paused);
    const pause = el('daemon-console-pause');
    if (pause) {
      pause.textContent = s.paused ? 'Resume' : 'Pause';
      pause.setAttribute('aria-pressed', String(s.paused));
    }
    const runChip = el('daemon-console-run-chip');
    if (runChip) {
      runChip.hidden = !s.filters.runId;
      const label = el('daemon-console-run-label');
      if (label) label.textContent = s.filters.runId ? `run ${shortId(s.filters.runId)}` : '';
      runChip.title = s.filters.runId ? `Only lines of run ${s.filters.runId}` : '';
    }
    const auto = el('daemon-console-autoscroll');
    if (auto) auto.checked = s.autoScroll;
    const fs = el('daemon-console-fullscreen');
    if (fs) {
      fs.textContent = s.fullscreen ? 'Exit full screen' : 'Full screen';
      fs.setAttribute('aria-pressed', String(s.fullscreen));
    }
  }

  function setFullscreen(on) {
    s.fullscreen = on;
    const panel = el('daemon-console');
    if (panel) panel.classList.toggle('dconsole--fullscreen', on);
    document.body.classList.toggle('has-dconsole-fullscreen', on);
    renderConsoleStatus();
    if (s.autoScroll) {
      const body = el('daemon-console-body');
      if (body) body.scrollTop = body.scrollHeight;
    }
  }

  /* ---------------------------------------------------------------- wire */

  function buildDaemonOptions() {
    const select = el('daemon-console-daemon');
    if (!select) return;
    select.innerHTML = '<option value="">All daemons</option>'
      + DAEMONS.map((d) => `<option value="${escapeHtml(d.key)}">${escapeHtml(d.name)}</option>`).join('');
  }

  function wire() {
    if (s.wired) return;
    if (!el('tab-daemons')) return;
    s.wired = true;
    buildDaemonOptions();

    el('daemons-cards')?.addEventListener('click', (event) => {
      const button = event.target.closest('[data-daemon-run]');
      if (button && !button.disabled) runNow(button.getAttribute('data-daemon-run'));
    });

    el('daemon-console-daemon')?.addEventListener('change', (event) => {
      s.filters.daemon = event.target.value;
      reloadConsole();
    });
    el('daemon-console-level')?.addEventListener('change', (event) => {
      s.filters.level = event.target.value || 'Info';
      reloadConsole();
    });
    el('daemon-console-search')?.addEventListener('input', (event) => {
      s.filters.search = event.target.value;
      renderConsole();
    });
    el('daemon-console-pause')?.addEventListener('click', () => {
      s.paused = !s.paused;
      s.connection = s.paused ? 'paused' : (s.firstLoadDone ? 'live' : 'loading');
      renderConsoleStatus();
      if (!s.paused) pollConsole(true);
    });
    el('daemon-console-clear')?.addEventListener('click', clearView);
    el('daemon-console-retry')?.addEventListener('click', () => pollConsole(true));
    el('daemon-console-autoscroll')?.addEventListener('change', (event) => {
      s.autoScroll = event.target.checked;
      if (s.autoScroll) {
        const body = el('daemon-console-body');
        if (body) body.scrollTop = body.scrollHeight;
      }
    });
    el('daemon-console-fullscreen')?.addEventListener('click', () => setFullscreen(!s.fullscreen));
    el('daemon-console-run-clear')?.addEventListener('click', () => {
      s.filters.runId = '';
      renderConsole();
    });

    const body = el('daemon-console-body');
    if (body) {
      body.addEventListener('scroll', () => {
        const atBottom = body.scrollHeight - body.scrollTop - body.clientHeight < 24;
        if (atBottom === s.autoScroll) return;
        s.autoScroll = atBottom;
        renderConsoleStatus();
      });
      body.addEventListener('click', (event) => {
        const runButton = event.target.closest('[data-run-filter]');
        if (runButton) {
          event.stopPropagation();
          s.filters.runId = runButton.getAttribute('data-run-filter') || '';
          renderConsole();
          return;
        }
        if (event.target.closest('.dconsole__detail')) return;
        const line = event.target.closest('[data-line-id]');
        if (line) toggleLine(Number(line.getAttribute('data-line-id')));
      });
      body.addEventListener('keydown', (event) => {
        if (event.key !== 'Enter' && event.key !== ' ') return;
        const line = event.target.closest('[data-line-id]');
        if (!line || event.target !== line) return;
        event.preventDefault();
        toggleLine(Number(line.getAttribute('data-line-id')));
      });
    }

    document.addEventListener('keydown', (event) => {
      if (event.key === 'Escape' && s.fullscreen) setFullscreen(false);
    });
    document.addEventListener('visibilitychange', () => {
      if (!isVisible()) return;
      loadStatuses();
      pollConsole();
    });

    s.consoleTimer = setInterval(() => pollConsole(), CONSOLE_POLL_MS);
    s.cardsTimer = setInterval(() => loadStatuses(), CARDS_REFRESH_MS);
  }

  function toggleLine(id) {
    if (!Number.isFinite(id)) return;
    if (s.expanded.has(id)) s.expanded.delete(id);
    else s.expanded.add(id);
    const keepAuto = s.autoScroll;
    s.autoScroll = false;
    renderConsole();
    s.autoScroll = keepAuto;
    renderConsoleStatus();
  }

  /** Called by activateTab('daemons'): the timers skip every request while the tab is hidden, so re-read on entry. */
  function onActivate() {
    wire();
    renderCards();
    loadStatuses(true);
    if (!s.firstLoadDone) renderConsole();
    if (!s.paused) pollConsole(true);
  }

  /** Called on a company switch: what was read belongs to the previous company. Re-reads now only
   *  when the tab is on screen; otherwise the next onActivate does. */
  function onCompanyChange() {
    s.statuses = [];
    s.statusError = '';
    s.generation += 1;
    s.consoleInFlight = false;
    s.entries = [];
    s.seen = new Set();
    s.expanded = new Set();
    s.lastSeenId = 0;
    s.firstLoadDone = false;
    s.cleared = false;
    if (isVisible()) {
      reloadConsole();
      loadStatuses(true);
    }
  }

  return { onActivate, onCompanyChange };
})();

window.DaemonsTab = DaemonsTab;
