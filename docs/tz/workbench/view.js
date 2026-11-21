(function () {
  const SPEC = window.SPEC || { files: [], total: 0 };
  const KINDS = { answer: 'Ответ', question: 'Вопрос', change: 'Правка', ok: 'Согласовано', note: 'Заметка' };
  const isQ = id => /-Q\d+$/.test(id);            // пункт-вопрос: [COST-Q3]
  const answered = list => (list || []).some(c => c.kind === 'answer' || c.kind === 'ok');
  const doc = document.getElementById('doc');
  const rail = document.getElementById('rail');
  const results = document.getElementById('results');
  const general = document.getElementById('general');
  const countsEl = document.getElementById('counts');
  const dbnote = document.getElementById('dbnote');

  let db = null;                       // хранилище страницы, появляется позже
  // Кто смотрит. Замечания владельца и Claude лежат в `comments` (писать туда может только
  // владелец), замечания остальных — каждое в своём поддереве `people/<id>/items`: правила
  // хранилища дают человеку писать только туда, а закрывать и удалять чужое — только владельцу.
  let who = null;                      // пространство `user`
  let myId = null, isOwner = false, ownerId = null;
  const names = new Map();             // id -> имя, как его видит этот зритель
  const sources = new Map();           // путь коллекции -> [замечания]
  const peopleSubs = new Map();        // id участника -> отписка
  let byItem = new Map();              // пункт -> [комментарии]
  let current = null;                  // текущий файл
  let filter = 'all';
  const nodes = new Map();             // пункт -> элемент на экране
  const openPanels = new Set();
  const drafts = new Map();            // пункт -> недописанный текст
  let focused = null;                  // в каком пункте сейчас курсор

  marked.setOptions({ gfm: true, breaks: false });

  // ── навигация ───────────────────────────────────────
  SPEC.files.forEach(f => {
    const b = document.createElement('button');
    b.className = 'navitem';
    b.dataset.file = f.id;
    b.innerHTML = '<span class="n"></span><span class="t"></span><span class="c" data-zero="1">0</span>';
    b.querySelector('.n').textContent = f.id.slice(0, 2);
    b.querySelector('.t').textContent = f.title.replace(/^\d+\.\s*/, '');
    b.onclick = () => show(f.id);
    rail.appendChild(b);
  });

  function show(id) {
    current = id;
    try { localStorage.setItem('spec.file', id); } catch (e) { /* приватное окно */ }
    const f = SPEC.files.find(x => x.id === id) || SPEC.files[0];
    results.hidden = true;
    doc.hidden = false;
    general.hidden = false;
    doc.innerHTML = marked.parse(f.md);
    decorate(f);
    generalBlocks(f);
    rail.querySelectorAll('.navitem').forEach(b =>
      b.setAttribute('aria-current', String(b.dataset.file === id)));
    window.scrollTo({ top: 0 });
  }

  // Разметка пунктов: элемент, начинающийся с метки [CODE-N], становится блоком с комментариями.
  function decorate(f) {
    nodes.clear();
    doc.querySelectorAll('table').forEach(t => {
      const w = document.createElement('div'); w.className = 'tablewrap';
      t.parentNode.insertBefore(w, t); w.appendChild(t);
    });
    [...doc.children].forEach(el => {
      const m = (el.textContent || '').trim().match(/^\[([A-Z]+-Q?[\d.]+)\]/);
      if (!m) return;
      const id = m[1];
      const box = document.createElement('section');
      box.className = 'item';
      box.id = 'i-' + id;
      box.dataset.item = id;
      const line = document.createElement('div');
      line.className = 'tagline';
      line.innerHTML = '<span class="tag"></span><button class="act">' +
        (isQ(id) ? 'Ответить' : 'Комментировать') + '</button>' +
        '<span class="badge" data-zero="1">0</span><span class="need" hidden>нет ответа</span>';
      line.querySelector('.tag').textContent = id;
      line.querySelector('.act').onclick = () => togglePanel(id);
      el.parentNode.insertBefore(box, el);
      box.appendChild(line);
      box.appendChild(el);
      // всё до следующей метки, заголовка или черты — часть того же пункта: у разборов и решений бывает несколько абзацев
      let next = box.nextElementSibling;
      while (next && !/^\[([A-Z]+-Q?[\d.]+)\]/.test((next.textContent || '').trim())
        && !/^H[1-4]$/.test(next.tagName) && next.tagName !== 'HR'
        && (next.tagName === 'UL' || next.tagName === 'OL' || next.tagName === 'BLOCKQUOTE'
          || next.classList.contains('tablewrap') || next.tagName === 'PRE'
          || next.tagName === 'P')) {
        const move = next; next = next.nextElementSibling; box.appendChild(move);
      }
      nodes.set(id, box);
      paint(id);
    });
    applyFilter();
  }

  // Общие замечания — не к пункту, а к разделу целиком и ко всему ТЗ.
  // Такие замечания никуда не денутся при перенумерации пунктов: их адрес — файл, а не пункт.
  function generalBlocks(f) {
    general.innerHTML = '';
    [{ item: 'SPEC-ALL', file: '*', label: 'Ко всему ТЗ' },
     { item: 'FILE-' + f.id, file: f.id, label: 'К разделу «' + f.title.replace(/^\d+\.\s*/, '') + '»' }]
      .forEach(g => {
        const box = document.createElement('section');
        box.className = 'item general';
        box.id = 'i-' + g.item;
        box.dataset.item = g.item;
        box.dataset.file = g.file;
        const line = document.createElement('div');
        line.className = 'tagline';
        line.innerHTML = '<span class="tag"></span><button class="act">Комментировать</button>' +
          '<span class="badge" data-zero="1">0</span>';
        line.querySelector('.tag').textContent = g.label;
        line.querySelector('.act').onclick = () => togglePanel(g.item);
        box.appendChild(line);
        general.appendChild(box);
        nodes.set(g.item, box);
        paint(g.item);
      });
  }

  // ── отрисовка состояния пункта ──────────────────────
  function paint(id) {
    const box = nodes.get(id); if (!box) return;
    const list = byItem.get(id) || [];
    const live = list.filter(c => !c.resolved);
    box.classList.toggle('has', list.length > 0);
    box.classList.remove('k-question', 'k-change', 'k-ok');
    const lead = live[0] || list[0];
    if (lead && KINDS[lead.kind]) box.classList.add('k-' + lead.kind);
    const badge = box.querySelector('.badge');
    badge.textContent = String(list.length);
    badge.dataset.zero = list.length ? '0' : '1';
    const need = box.querySelector('.need');
    if (need) need.hidden = !(isQ(id) && !answered(list));
    if (openPanels.has(id)) renderPanel(id);
  }

  function togglePanel(id) {
    if (openPanels.has(id)) {
      openPanels.delete(id);
      const p = nodes.get(id).querySelector('.panel'); if (p) p.remove();
      nodes.get(id).classList.remove('open');
    } else { openPanels.add(id); renderPanel(id); nodes.get(id).classList.add('open'); }
  }

  function renderPanel(id) {
    const box = nodes.get(id); if (!box) return;
    let p = box.querySelector('.panel');
    if (!p) { p = document.createElement('div'); p.className = 'panel'; box.appendChild(p); }
    p.innerHTML = '';
    (byItem.get(id) || []).forEach(c => p.appendChild(commentEl(id, c)));

    const form = document.createElement('div');
    form.className = 'form';
    form.innerHTML =
      '<textarea placeholder="' + (isQ(id) ? 'Ваш ответ на вопрос ' + id : 'Что не так с пунктом ' + id + '?') + '"></textarea>' +
      '<div class="row"><div class="kinds">' +
      Object.entries(KINDS).map(([k, t], i) =>
        '<button data-k="' + k + '" aria-pressed="' + (i === 0) + '">' + t + '</button>').join('') +
      '</div><button class="send">Записать</button>' +
      '<span class="hint"></span></div>';
    const ta = form.querySelector('textarea');
    // Панель перерисовывается на каждое изменение в хранилище — недописанный текст сохраняем,
    // иначе чужая (или своя вторая) запись стирала бы черновик на полуслове.
    if (drafts.has(id)) ta.value = drafts.get(id);
    ta.addEventListener('input', () => drafts.set(id, ta.value));
    if (focused === id) requestAnimationFrame(() => {
      ta.focus(); ta.setSelectionRange(ta.value.length, ta.value.length);
    });
    ta.addEventListener('focus', () => { focused = id; });
    ta.addEventListener('blur', () => { if (focused === id) focused = null; });
    const kinds = form.querySelectorAll('.kinds button');
    let kind = isQ(id) ? 'answer' : 'question';   // на вопрос по умолчанию отвечают, а не спрашивают
    kinds.forEach(b => b.setAttribute('aria-pressed', String(b.dataset.k === kind)));
    kinds.forEach(b => b.onclick = () => {
      kind = b.dataset.k;
      kinds.forEach(x => x.setAttribute('aria-pressed', String(x === b)));
    });
    const send = form.querySelector('.send');
    const hint = form.querySelector('.hint');
    if (!db) { send.disabled = true; hint.textContent = 'Хранилище страницы недоступно в этом просмотре'; }
    else if (!myId) { send.disabled = true; hint.textContent = 'Писать замечания может только вошедший участник'; }
    send.onclick = async () => {
      const text = ta.value.trim(); if (!text || !db || !myId) return;
      send.disabled = true; hint.textContent = 'Записываю…';
      try {
        const body = {
          item: id, file: (box.dataset.file || current), kind, text, authorId: myId,
          createdAt: new Date().toISOString(), resolved: false,
        };
        if (isOwner) await db.collection('comments').add(body);
        else {
          const mark = db.doc('people/' + myId);
          if (!(await mark.get()).exists) await mark.set({ since: body.createdAt });
          await mark.collection('items').add(body);
        }
        ta.value = ''; drafts.delete(id); hint.textContent = '';
      } catch (e) {
        hint.textContent = e && e.code === 'quota_exceeded'
          ? 'Хранилище заполнено — удалите старые замечания'
          : 'Не записалось, попробуйте ещё раз';
      } finally { send.disabled = false; }
    };
    ta.addEventListener('keydown', e => {
      if ((e.metaKey || e.ctrlKey) && e.key === 'Enter') send.click();
    });
    p.appendChild(form);
  }

  function commentEl(id, c) {
    const el = document.createElement('div');
    el.className = 'cmt k-' + c.kind + (c.resolved ? ' done' : '');
    const when = (c.createdAt || '').slice(0, 16).replace('T', ' ');
    el.innerHTML = '<div class="meta"><span class="kind k-' + c.kind + '">' + (KINDS[c.kind] || c.kind) +
      '</span><span class="who"></span><span>' + when + '</span></div><div class="body"></div>';
    el.querySelector('.body').textContent = c.text;
    const whoEl = el.querySelector('.who');
    whoEl.textContent = authorName(c);
    const uid = authorIdOf(c);
    if (uid) whoEl.dataset.uid = uid;
    const meta = el.querySelector('.meta');
    if (db && isOwner) {
      const say = document.createElement('span');
      say.className = 'said';
      const res = document.createElement('button');
      res.textContent = c.resolved ? 'вернуть' : 'закрыть';
      res.onclick = async () => {
        say.textContent = '';
        try { await db.doc(c._path).update({ resolved: !c.resolved }); }
        catch (e) { say.textContent = 'не вышло: ' + ((e && e.code) || 'ошибка'); }
      };
      // Без window.confirm: в песочнице артефакта браузерные диалоги не показываются,
      // и нажатие выглядело бы как «кнопка не работает». Подтверждение — второй клик.
      const del = document.createElement('button');
      del.textContent = 'удалить';
      let armed = 0;
      del.onclick = async () => {
        if (!armed) {
          armed = setTimeout(() => { armed = 0; del.textContent = 'удалить'; del.classList.remove('armed'); }, 4000);
          del.textContent = 'точно удалить?';
          del.classList.add('armed');
          return;
        }
        clearTimeout(armed); armed = 0;
        del.textContent = 'удаляю…'; del.disabled = true; say.textContent = '';
        try {
          await db.doc(c._path).delete();
        } catch (e) {
          del.textContent = 'удалить'; del.disabled = false; del.classList.remove('armed');
          say.textContent = 'не удалилось: ' + ((e && e.code) || 'ошибка');
        }
      };
      meta.append(res, del, say);
    }
    return el;
  }

  // ── фильтр и поиск ──────────────────────────────────
  function applyFilter() {
    nodes.forEach((box, id) => {
      if (box.classList.contains('general')) return;   // общие замечания видны при любом фильтре
      const list = byItem.get(id) || [];
      const live = list.filter(c => !c.resolved);
      const keep = filter === 'all' ? true
        : filter === 'q' ? isQ(id)
        : filter === 'cmt' ? list.length > 0 : live.length > 0;
      box.hidden = !keep;
    });
    const shown = [...nodes.values()].filter(b => !b.hidden && !b.classList.contains('general')).length;
    if (filter !== 'all' && shown === 0 && !doc.querySelector('.empty')) {
      const e = document.createElement('p'); e.className = 'empty';
      e.textContent = filter === 'q' ? 'Открытых вопросов в этом разделе нет.'
        : filter === 'cmt' ? 'В этом разделе замечаний нет.' : 'Незакрытых замечаний в этом разделе нет.';
      doc.prepend(e);
    } else { const e = doc.querySelector('.empty'); if (e) e.remove(); }
  }

  const fbtn = {
    all: document.getElementById('f-all'), q: document.getElementById('f-q'),
    cmt: document.getElementById('f-cmt'), open: document.getElementById('f-open'),
  };
  Object.entries(fbtn).forEach(([k, b]) => b.onclick = () => {
    filter = k;
    Object.values(fbtn).forEach(x => x.setAttribute('aria-pressed', String(x === b)));
    applyFilter();
  });

  const q = document.getElementById('q');
  q.addEventListener('input', () => {
    const v = q.value.trim().toLowerCase();
    if (v.length < 2) { results.hidden = true; doc.hidden = false; general.hidden = false; return; }
    const hits = [];
    SPEC.files.forEach(f => {
      const re = /\*\*\[([A-Z]+-Q?[\d.]+)\]\*\*([\s\S]*?)(?=\n\*\*\[[A-Z]+-Q?[\d.]+\]\*\*|\n## |\n$)/g;
      let m;
      while ((m = re.exec(f.md))) {
        const text = m[2].replace(/[*`>|#]/g, ' ').replace(/\s+/g, ' ').trim();
        if (text.toLowerCase().includes(v) || m[1].toLowerCase().includes(v))
          hits.push({ id: m[1], file: f.id, title: f.title, text });
        if (hits.length > 60) break;
      }
    });
    results.innerHTML = '';
    const head = document.createElement('p'); head.className = 'hint';
    head.textContent = hits.length ? 'Найдено пунктов: ' + hits.length : 'Ничего не найдено';
    results.appendChild(head);
    hits.forEach(h => {
      const b = document.createElement('button');
      b.className = 'result';
      b.innerHTML = '<span class="tag"></span><span class="src"></span><div class="snip"></div>';
      b.querySelector('.tag').textContent = h.id;
      b.querySelector('.src').textContent = h.title;
      b.querySelector('.snip').textContent = h.text.slice(0, 180) + (h.text.length > 180 ? '…' : '');
      b.onclick = () => {
        q.value = '';
        show(h.file);
        const t = document.getElementById('i-' + h.id);
        if (t) { t.scrollIntoView({ block: 'center' }); t.classList.add('has'); }
      };
      results.appendChild(b);
    });
    results.hidden = false; doc.hidden = true; general.hidden = true;
  });

  // ── сводка ──────────────────────────────────────────
  const dlg = document.getElementById('dlg');
  document.getElementById('summary').onclick = () => {
    const lines = [];
    const all = (byItem.get('SPEC-ALL') || []);
    if (all.length) {
      lines.push('## Ко всему ТЗ');
      all.forEach(c => lines.push((c.resolved ? '[закрыто] ' : '') +
        (KINDS[c.kind] || c.kind) + ' (' + authorName(c) + '): ' + c.text));
      lines.push('');
    }
    SPEC.files.forEach(f => {
      const items = [...byItem.entries()].filter(([, l]) => l[0] && l[0].file === f.id);
      if (!items.length) return;
      lines.push('## ' + f.title);
      items.sort((a, b) => a[0].localeCompare(b[0], 'ru', { numeric: true }))
        .forEach(([id, list]) => list.forEach(c => lines.push(
          (c.resolved ? '[закрыто] ' : '') + id + ' — ' + (KINDS[c.kind] || c.kind) +
          ' (' + authorName(c) + '): ' + c.text)));
      lines.push('');
    });
    document.getElementById('dlgtext').textContent =
      lines.length ? lines.join('\n') : 'Замечаний пока нет.';
    dlg.showModal();
  };
  document.getElementById('close').onclick = () => dlg.close();
  document.getElementById('copy').onclick = async () => {
    try { await navigator.clipboard.writeText(document.getElementById('dlgtext').textContent); }
    catch (e) { /* буфер недоступен */ }
  };

  // ── счётчики ────────────────────────────────────────
  function counts() {
    let all = 0, open = 0;
    byItem.forEach(list => { all += list.length; open += list.filter(c => !c.resolved).length; });
    const qs = SPEC.files.flatMap(f => f.items).filter(isQ);
    const noAns = qs.filter(id => !answered(byItem.get(id))).length;
    countsEl.textContent = SPEC.total + ' пунктов · ' + qs.length + ' вопросов'
      + (noAns ? ' (' + noAns + ' без ответа)' : '')
      + (all ? ' · ' + all + ' замечаний' : '');
    rail.querySelectorAll('.navitem').forEach(b => {
      const n = [...byItem.values()].flat().filter(c => c.file === b.dataset.file && !c.resolved).length;
      const c = b.querySelector('.c'); c.textContent = String(n); c.dataset.zero = n ? '0' : '1';
    });
  }

  // ── старт ───────────────────────────────────────────
  let saved = null;
  try { saved = localStorage.getItem('spec.file'); } catch (e) { /* приватное окно */ }
  show(SPEC.files.some(f => f.id === saved) ? saved : SPEC.files[0].id);
  counts();

  // ── авторы ──────────────────────────────────────────
  // Хранится только id автора; имя разрешается при показе — у каждого зрителя своё и всегда свежее.
  // Замечания без authorId написаны до появления авторов: их писал владелец (или Claude).
  function authorIdOf(c) {
    if (c.author === 'codex') return null;
    if (c.author === 'claude' || String(c.id).startsWith('claude-')) return null;
    return c.authorId || ownerId;
  }
  function authorName(c) {
    if (c.author === 'codex') return 'Codex';
    if (c.author === 'claude' || String(c.id).startsWith('claude-')) return 'Claude';
    const uid = authorIdOf(c);
    if (!uid) return 'Владелец';
    return (uid === myId ? 'вы' : names.get(uid)) || (uid === ownerId ? 'Владелец' : 'Участник');
  }
  async function refreshNames() {
    if (!who) return;
    const ids = [...new Set([...byItem.values()].flat().map(authorIdOf).filter(Boolean))];
    if (!ids.length) return;
    const ps = await who.profiles(ids);
    ids.forEach(id => { if (ps[id] && ps[id].name) names.set(id, ps[id].name); });
    document.querySelectorAll('.who[data-uid]').forEach(el => {
      const uid = el.dataset.uid;
      el.textContent = (uid === myId ? 'вы' : names.get(uid)) || (uid === ownerId ? 'Владелец' : 'Участник');
    });
  }

  // ── данные ──────────────────────────────────────────
  function rebuild() {
    byItem = new Map();
    [...sources.values()].flat()
      .sort((a, b) => String(a.createdAt || '').localeCompare(String(b.createdAt || '')))
      .forEach(c => {
        if (!byItem.has(c.item)) byItem.set(c.item, []);
        byItem.get(c.item).push(c);
      });
    nodes.forEach((_, id) => paint(id));
    counts(); applyFilter();
    refreshNames();
  }
  function loadError(err) {
    dbnote.hidden = false;
    dbnote.textContent = err && err.code === 'revoked'
      ? 'Доступ к замечаниям закрыт. Текст ТЗ остаётся доступен.'
      : 'Замечания сейчас не загружаются. Текст ТЗ доступен, попробуйте перезагрузить страницу.';
  }
  function watch(path) {
    return db.collection(path).onSnapshot(snap => {
      sources.set(path, snap.docs.map(d => Object.assign({ id: d.id, _path: path + '/' + d.id }, d.data())));
      rebuild();
    }, loadError);
  }

  const use = name => window.claude && window.claude.use ? window.claude.use(name) : Promise.resolve(null);
  Promise.all([use('db'), use('user')]).then(async ([store, u]) => {
    if (!store) {
      dbnote.hidden = false;
      dbnote.textContent = 'Замечания в этом просмотре не сохраняются: страница открыта без доступа к своему хранилищу. Читать ТЗ можно, комментировать — нет.';
      return;
    }
    db = store; who = u;
    if (who) {
      const me = await who.me();
      myId = me.id; isOwner = me.isOwner;
    }
    const ownerDoc = db.doc('meta/owner');
    const known = await ownerDoc.get();
    ownerId = known.exists ? known.data().id : null;
    // Один раз: владелец оставляет свой id, чтобы замечания, написанные до появления авторов,
    // подписывались его именем и у остальных зрителей.
    if (isOwner && myId && ownerId !== myId) {
      ownerId = myId;
      try { await ownerDoc.set({ id: myId }); } catch (e) { /* подпишется «Владелец» */ }
    }
    watch('comments');
    db.collection('people').onSnapshot(snap => {
      const ids = new Set(snap.docs.map(d => d.id));
      ids.forEach(uid => { if (!peopleSubs.has(uid)) peopleSubs.set(uid, watch('people/' + uid + '/items')); });
      [...peopleSubs.keys()].forEach(uid => {
        if (!ids.has(uid)) { peopleSubs.get(uid)(); peopleSubs.delete(uid); sources.delete('people/' + uid + '/items'); }
      });
      rebuild();
    }, loadError);
    openPanels.forEach(id => renderPanel(id));
  });
})();
