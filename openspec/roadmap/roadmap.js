(function () {
  const { changes: CH, claudeMarks } = JSON.parse(document.getElementById('data').textContent);
  const CHANGE_IDS = Object.keys(CH);
  const cap = s => s.charAt(0).toUpperCase() + s.slice(1);
  const titleOf = id => cap(CH[id].title);
  const tasksOf = id => CH[id].sections.flatMap(s => s.items);
  const partTasks = (id, part) => tasksOf(id).filter(t => part.tasks.includes(t.n));
  const stats = tasks => ({ done: tasks.filter(t => t.done).length, total: tasks.length });
  const taskDone = task => task.done || task.completedBy?.done;
  const available = part => ['ready', 'partial'].includes(part.state);
  const STATE = {
    done: ['ok', '✓ Задачи выполнены'], ready: ['ready', 'Можно начать'],
    partial: ['ready', 'Доступна часть'], waiting: ['warn', '△ Ожидает условий'],
    unknown: ['off', 'Условия не описаны'],
  };
  const esc = value => String(value).replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
  const domId = key => 's-' + key.replace(/[^a-z0-9-]/gi, '_');
  const tasksLink = id => '../../../' + CH[id].path + '/tasks.md';
  function inline(text, source) {
    const codes = [];
    let h = esc(text).replace(/`([^`]+)`/g, (_, c) => { codes.push(c); return '\u0000' + (codes.length - 1) + '\u0000'; });
    h = h.replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>')
      .replace(/\[([A-Z]{2,5}-[A-Z0-9.]+)\]/g, '<span class="tag">$1</span>');
    if (source) h = h.replace(/\[([^\]]+)\]\((https:\/\/[^\s()]+|(?:\.\.?\/)*[\w./-]+\.md(?:#[^\s()]+)?)\)/g,
      (_, label, href) => '<a class="linkbtn" href="' + (href.startsWith('https://') ? href : '../../../' + CH[source].path + '/' + href) + '">' + label + '</a>');
    return h.replace(/\u0000(\d+)\u0000/g, (_, i) => '<code>' + codes[+i] + '</code>');
  }
  function refsOf(text, own) {
    const refs = [];
    (text.match(/`((?:plan|work)-\d\d(?:-[a-z0-9-]+)?|core-attachment-owner)`/g) || []).forEach(t => {
      const id = t.slice(1, -1);
      const full = CH[id] ? id : CHANGE_IDS.find(k => k.startsWith(id + '-'));
      if (full && full !== own && !/-00-epic$/.test(full) && !refs.includes(full)) refs.push(full);
    });
    return refs;
  }
  function groups(epic, prefix, filter) {
    return CH[epic].sections.filter(s => s.items.length && filter(s)).map(section => ({
      title: section.title, steps: section.items.map(item => {
        const m = item.text.match(/^`((?:plan|work)-\d\d-[a-z0-9-]+)`(?:\s*\(\d+\)(?:\s*—\s*|,\s*)?|\s*—\s*)/);
        const change = m && CH[m[1]] ? m[1] : null;
        const text = change ? item.text.slice(m[0].length) : item.text;
        return { key: epic + ':' + item.n, num: prefix + item.n, text, done: item.done,
          change, source: epic, refs: refsOf(text, change), withdrawn: /\*\*снят\*\*/.test(item.text) };
      }),
    }));
  }
  function tail(epic, prefix) {
    const listed = new Set(groups(epic, '', () => true).flatMap(g => g.steps.map(s => s.change)));
    const steps = CHANGE_IDS.filter(id => id.startsWith(prefix + '-') && id !== epic && !listed.has(id))
      .map((id, i) => ({ key: id + ':change', num: prefix === 'plan' ? '3·+' + (i + 1) : '4·+' + (i + 1),
        change: id, source: id, text: CH[id].why || titleOf(id), refs: [], withdrawn: false, done: CH[id].archived }));
    return (steps.length ? [{ title: 'Вне нарезки', steps }] : []).concat([{
      title: 'Закрытие', steps: [{ key: epic + ':archive', num: prefix === 'plan' ? '3·∎' : '4·∎',
        text: 'Зонтик `' + epic + '`' + (CH[epic].archived ? '.' : ' архивируется после всех дочерних change.'),
        done: CH[epic].archived, archive: epic, change: null, refs: [], withdrawn: false }],
    }]);
  }
  const core = CH['core-attachment-owner'] ? [{ title: 'Ядро', steps: [{
    key: 'core-attachment-owner:change', num: 'Я·1', change: 'core-attachment-owner',
    text: 'Владелец в реестре и порт IModuleBlobs. Опора для Work-17.', refs: [], withdrawn: false,
    done: CH['core-attachment-owner'].archived,
  }] }] : [];
  const TRACKS = [
    { id: 'C', title: 'Этап 4 · Учёт работ', short: 'Work · приоритет',
      sub: 'Один change Work в отдельной дочерней сессии. Под change — вехи его PR и условия старта.',
      groups: groups('work-00-epic', '4·', s => !/^1\./.test(s.title)).concat(tail('work-00-epic', 'work')) },
    { id: 'A', title: 'Этап 3 · Планирование', short: 'Plan · параллельно',
      sub: 'Один change Plan параллельно Work. Порядок зонтика сохранён; доступность определяется условиями вехи.',
      groups: groups('plan-00-epic', '3·', () => true).concat(tail('plan-00-epic', 'plan')) },
    { id: 'B', title: 'Ранние работы и опоры', short: 'Ранние работы',
      sub: 'Ранние change Work и самостоятельная опора ядра.',
      groups: groups('work-00-epic', '4·', s => /^1\./.test(s.title)).concat(core) },
  ];
  const ALL_STEPS = TRACKS.flatMap(t => t.groups.flatMap(g => g.steps.map(s => ({ ...s, track: t.id }))));
  TRACKS.forEach(t => t.groups.forEach(g => { g.steps = g.steps.map(s => ALL_STEPS.find(x => x.key === s.key)); }));
  const STEP_BY_CHANGE = new Map(ALL_STEPS.filter(s => s.change).map(s => [s.change, s]));
  const PARTS = new Map(CHANGE_IDS.flatMap(id => (CH[id].milestones || []).map(p => [p.id, { ...p, change: id }])));
  const ui = { filter: 'all', q: '', open: new Set() };
  try {
    const saved = JSON.parse(localStorage.getItem('roadmap-ui') || '{}');
    if (['all', 'open', 'ready', 'waiting'].includes(saved.filter)) ui.filter = saved.filter;
    if (Array.isArray(saved.open)) saved.open.forEach(k => ui.open.add(k));
  } catch (_) { /* File previews can disable browser storage. */ }
  const saveUi = () => {
    try { localStorage.setItem('roadmap-ui', JSON.stringify({ filter: ui.filter, open: [...ui.open] })); } catch (_) { /* Optional preference only. */ }
  };
  const matches = text => !ui.q || String(text).toLowerCase().includes(ui.q);
  const filterPart = p => ui.filter === 'all' || ui.filter === 'open' && p.state !== 'done'
    || ui.filter === 'ready' && available(p) || ui.filter === 'waiting' && ['waiting', 'unknown', 'partial'].includes(p.state);
  const partMatches = (id, p) => matches(p.title) || matches(id) || p.conditions.some(c => matches(c.label))
    || partTasks(id, p).some(t => matches(t.text) || matches(t.n));
  function visibleParts(step) {
    const parentHit = matches(step.text) || matches(step.num) || matches(step.change) || matches(titleOf(step.change));
    return CH[step.change].milestones.filter(p => filterPart(p) && (parentHit || partMatches(step.change, p)));
  }
  function visible(step) {
    if (step.change && !step.withdrawn) {
      if (visibleParts(step).length) return true;
      return ['all', 'open'].includes(ui.filter) && (ui.filter === 'all' || !step.done)
        && (matches(step.text) || matches(step.change) || tasksOf(step.change).some(t => matches(t.text)));
    }
    return ['all', 'open'].includes(ui.filter) && (ui.filter === 'all' || !step.done && !step.withdrawn)
      && (matches(step.text) || matches(step.num));
  }
  const historyFormat = new Intl.DateTimeFormat('ru-RU', { dateStyle: 'short', timeStyle: 'short' });
  function stamp(key) {
    const at = claudeMarks[key];
    return at ? '<time class="who" datetime="' + esc(at) + '">Claude · ' + historyFormat.format(new Date(at)) + '</time>' : '';
  }
  const badge = p => '<span class="pill ' + STATE[p.state][0] + '">' + STATE[p.state][1] + '</span>';
  const checkbox = (key, done, label) => '<input type="checkbox" data-key="' + esc(key) + '"'
    + (done ? ' checked' : '') + ' disabled aria-label="' + esc(label) + '" title="Статус из OpenSpec">';
  const goto = (key, label) => '<button type="button" class="linkbtn" data-goto="' + esc(key) + '">' + esc(label) + '</button>';
  function renderCompletion(task) {
    const source = task.completedBy;
    if (task.done || !source) return '';
    return '<span class="sub">' + (source.done ? 'Выполнено в отдельном change ' : 'Выполняется в отдельном change ')
      + goto(source.change + ':' + source.tasks[0], source.label) + ' · задачи ' + esc(source.tasks.join(', ')) + '</span>';
  }
  function renderTasks(id, ids, key, label) {
    const tasks = tasksOf(id).filter(t => ids.includes(t.n));
    const st = stats(tasks);
    const open = ui.open.has(key) || ui.open.has(STEP_BY_CHANGE.get(id)?.key)
      || !!ui.q && !matches(id) && tasks.some(t => matches(t.text));
    return '<details class="task-list" data-open-key="' + esc(key) + '"' + (open ? ' open' : '') + '><summary>'
      + esc(label) + ' (' + st.done + '/' + st.total + ')</summary>' + tasks.filter(t => ui.filter !== 'open' || !taskDone(t)).map(t => {
        const taskKey = id + ':' + t.n;
        return '<div class="task' + (t.done ? ' is-done' : '') + '" id="' + domId(taskKey) + '">'
          + checkbox(taskKey, t.done, id + ' ' + t.n) + '<span class="tn">' + esc(t.n) + '</span><div class="tt">'
          + inline(t.text, id) + stamp(taskKey) + renderCompletion(t) + '</div></div>';
      }).join('') + '</details>';
  }
  function renderConditions(id, part) {
    let html = '<div class="conditions"><h4>Условия старта</h4>';
    if (!part.documented && part.state !== 'done') html += '<p class="sub">Условия этой вехи ещё не описаны. Готовность к старту не подтверждена.</p>';
    else if (!part.conditions.length) html += '<p class="sub">' + (part.state === 'done' ? 'Задачи вехи выполнены.' : 'В описанном объёме можно начать.') + '</p>';
    const renderCondition = c => {
      const target = c.target || (c.tasks.length ? c.change + ':' + c.tasks[0] : STEP_BY_CHANGE.get(c.change)?.key);
      const applies = c.for ? ' · Для задач ' + c.applies.join(', ') : '';
      return '<li><div class="condition-head"><span class="condition-state ' + (c.done ? 'is-ok' : c.optional ? '' : 'is-waiting') + '">'
        + (c.done ? '✓ Выполнено' : c.optional ? 'Дополнение' : '△ Ожидает') + '</span><span>' + inline(c.label) + '</span></div>'
        + '<div class="condition-source">' + esc(c.source) + (c.tasks.length ? ' · ' + esc(c.tasks.join(', ')) : '') + esc(applies) + '</div>'
        + (target ? '<button type="button" class="linkbtn" data-goto="' + esc(target) + '">Открыть условие</button>' : '') + '</li>';
    };
    html += '<ul>' + part.conditions.filter(c => !c.done).map(renderCondition).join('') + '</ul>';
    const complete = part.conditions.filter(c => c.done);
    if (complete.length) html += '<details class="completed-conditions"><summary>Выполненные условия (' + complete.length + ')</summary><ul>'
      + complete.map(renderCondition).join('') + '</ul></details>';
    if (part.state === 'partial') html += '<p class="milestone-note">Сейчас доступны задачи: ' + esc(part.startable.join(', ')) + '. Остальные ждут условий выше.</p>';
    if (part.note) html += '<p class="milestone-note">' + inline(part.note) + '</p>';
    return html + '</div>';
  }
  function renderMilestone(id, part) {
    const tasks = partTasks(id, part);
    const st = stats(tasks);
    const transferred = tasks.some(t => !t.done && t.completedBy);
    return '<article class="milestone" data-milestone="' + esc(part.id) + '" data-state="' + part.state + '" id="' + domId(part.id) + '">'
      + '<div class="milestone-head"><div><span class="pr-number">PR ' + part.number + '</span><h3>' + inline(cap(part.title)) + '</h3></div>'
      + badge(part) + '</div><div class="meta"><span>' + st.done + '/' + st.total + ' задач'
      + (transferred ? ' по исходным галочкам' : '') + '</span><span class="muted">'
      + (CH[id].archived ? 'Change архивирован' : 'Change открыт') + '</span></div>'
      + renderConditions(id, part) + renderTasks(id, part.tasks, part.id, 'Задачи PR') + '</article>';
  }
  function nextOf(track) {
    for (const step of ALL_STEPS.filter(s => s.track === track && s.change && !s.withdrawn)) {
      const part = CH[step.change].milestones.find(available);
      if (part) return { ...part, change: step.change };
    }
    return null;
  }
  function renderSummary() {
    document.getElementById('summary').innerHTML = TRACKS.map(track => {
      const parts = ALL_STEPS.filter(s => s.track === track.id && s.change && !s.withdrawn).flatMap(s => CH[s.change].milestones);
      const done = parts.filter(p => p.state === 'done').length;
      const next = nextOf(track.id);
      const pending = parts.filter(p => p.state !== 'done').length;
      const hint = next ? '<div class="next"><span class="lbl">' + STATE[next.state][1] + ' · PR ' + next.number + '</span><p><code>'
        + esc(next.change) + '</code></p><p>' + inline(cap(next.title)) + '</p><button type="button" class="linkbtn" data-goto="'
        + esc(next.id) + '">Перейти к вехе</button></div>' : '<p class="sub">' + (pending ? 'Доступных вех пока нет. У ' + pending + ' вех проверьте условия старта.' : 'Задачи всех вех выполнены. Архивы change и зонтика — отдельные шаги.') + '</p>';
      return '<div class="track-card"><div class="head"><h3>' + esc(track.short) + '</h3><span class="count">' + done + '/' + parts.length + ' вех</span></div>'
        + '<div class="bar" role="progressbar" aria-valuemin="0" aria-valuemax="' + parts.length + '" aria-valuenow="' + done + '" aria-label="' + esc(track.short)
        + '"><i style="width:' + (parts.length ? done / parts.length * 100 : 0) + '%"></i></div>' + hint + '</div>';
    }).join('');
  }
  function renderStep(step) {
    let html = '<div class="step' + (step.done ? ' is-done' : '') + (step.withdrawn ? ' withdrawn' : '') + '" id="' + domId(step.key)
      + '"><div class="num">' + esc(step.num) + '</div><div class="chk">' + checkbox(step.key, step.done, 'Шаг ' + step.num)
      + '</div><div class="body">' + (step.change ? '<div class="cname"><code class="cid">' + esc(step.change) + '</code><span class="ctitle">' + inline(titleOf(step.change)) + '</span></div>' : '')
      + '<div class="main">' + inline(step.text, step.source) + '</div><div class="meta">' + stamp(step.key) + (step.withdrawn ? '<span class="pill off">Снят</span>' : '') + '</div>';
    if (step.archive && step.done) html += archiveStatus(step.archive, 'Зонтик архивирован');
    if (step.change && !step.withdrawn) {
      const change = CH[step.change];
      const common = change.commonTasks;
      const tasks = tasksOf(step.change);
      const transferred = tasks.filter(t => t.completedBy && !t.done);
      if (change.archived && (transferred.length || step.key.endsWith(':change'))) html += archiveStatus(step.change, 'Change архивирован');
      if (transferred.length) {
        const source = transferred[0].completedBy;
        const closed = transferred.filter(taskDone).length;
        html += '<div class="meta"><span>' + (closed === transferred.length ? 'Остаток закрыт' : 'Остаток передан')
          + ' (' + closed + '/' + transferred.length + ') в ' + goto(source.change + ':' + source.tasks[0], source.label) + '</span></div>';
      }
      if (change.why) html += '<details class="why"><summary>Зачем change</summary><p>' + inline(change.why, step.change) + '</p></details>';
      html += '<div class="milestones">' + visibleParts(step).map(p => renderMilestone(step.change, p)).join('') + '</div>';
      if (['all', 'open'].includes(ui.filter) && common.length) html += renderTasks(step.change, common, step.change + ':common', 'Общие условия и проверка change');
      if (!change.archived && tasksOf(step.change).every(t => t.done)) html += '<p class="sub">Все задачи change выполнены. Осталось архивировать change и отметить зонтик.</p>';
    }
    return html + '</div></div>';
  }
  function archiveStatus(id, label) {
    const {done, total} = stats(tasksOf(id));
    return '<div class="meta"><span>' + label + ' · ' + done + '/' + total
      + ' задач при архивации</span><a class="linkbtn" href="' + esc(tasksLink(id)) + '">Открыть архив</a></div>';
  }
  function render() {
    renderSummary();
    const html = TRACKS.map(track => {
      const content = track.groups.map(group => {
        const steps = group.steps.filter(visible);
        return steps.length ? '<div class="group"><div class="group-h"><h3>' + esc(group.title) + '</h3></div>' + steps.map(renderStep).join('') + '</div>' : '';
      }).join('');
      return content ? '<section class="track"><div class="th"><h2>' + esc(track.title) + '</h2><p>' + esc(track.sub) + '</p></div><div class="sheet">' + content + '</div></section>' : '';
    }).join('');
    document.getElementById('tracks').innerHTML = html || '<p class="empty">Нет вех под условия отбора. Измените поиск или фильтр.</p>';
  }
  function setFilter(filter) {
    ui.filter = filter;
    ['all', 'open', 'ready', 'waiting'].forEach(f => document.getElementById('f-' + f).setAttribute('aria-pressed', f === filter));
    saveUi(); render();
  }
  document.addEventListener('toggle', event => {
    const details = event.target;
    if (!details.matches?.('details[data-open-key]') || !document.contains(details)) return;
    if (details.open) ui.open.add(details.dataset.openKey); else ui.open.delete(details.dataset.openKey);
    saveUi();
  }, true);
  document.addEventListener('click', event => {
    const summary = event.target.closest('summary');
    if (summary?.parentElement.matches('details[data-open-key]')) {
      const details = summary.parentElement;
      if (!details.open) ui.open.add(details.dataset.openKey); else ui.open.delete(details.dataset.openKey);
      saveUi();
      return;
    }
    const button = event.target.closest('[data-goto]');
    if (!button) return;
    const key = button.dataset.goto;
    const part = PARTS.get(key);
    ui.q = ''; document.getElementById('q').value = '';
    setFilter('all');
    let element = document.getElementById(domId(key));
    if (!element && part) element = document.getElementById(domId(STEP_BY_CHANGE.get(part.change)?.key));
    if (element) {
      for (let parent = element.parentElement; parent; parent = parent.parentElement) if (parent.tagName === 'DETAILS') parent.open = true;
      element.scrollIntoView({ behavior: matchMedia('(prefers-reduced-motion: reduce)').matches ? 'auto' : 'smooth', block: 'center' });
      element.classList.remove('flash'); void element.offsetWidth; element.classList.add('flash');
      element.tabIndex = -1; element.focus({ preventScroll: true });
    }
  });
  ['all', 'open', 'ready', 'waiting'].forEach(f => document.getElementById('f-' + f).addEventListener('click', () => setFilter(f)));
  document.getElementById('collapse').addEventListener('click', () => { ui.open.clear(); saveUi(); render(); });
  document.getElementById('q').addEventListener('input', event => { ui.q = event.target.value.trim().toLowerCase(); render(); });
  document.getElementById('foot').textContent = 'Снимок OpenSpec: ' + CHANGE_IDS.length + ' change, ' + PARTS.size + ' вех PR, '
    + CHANGE_IDS.reduce((sum, id) => sum + tasksOf(id).length, 0) + ' задач. Сохранено ' + Object.keys(claudeMarks).length
    + ' дат Claude. После изменения задач или условий пересоберите карту. «Задачи выполнены» не означает слияние PR; архив change показан отдельно.';
  setFilter(ui.filter);
})();
