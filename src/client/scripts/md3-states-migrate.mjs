// Перевод самодельных состояний на модель MD3 (sharedui-md3-05, [UI-64]) — проход кодмода md3:migrate
// ПЕРЕД переводом имён: правила здесь — о классе целиком, с вариантом (`hover:bg-brand-hover` снимается в
// пользу слоя, а `text-brand-hover` без варианта уходит дальше, в `names` карты). Строки — раздел `states`
// карты md3-class-map.json.
//
// Список классов — строка кода (`scanCode`); группа — строка вместе со строками внутри её `${…}`: там
// живёт наведение условной ветки (`${active ? 'bg-primary-container' : 'hover:bg-surface-container'}`), а
// рамка и заливка, по которым выбирается вид отключённого, — в постоянной части.

import { scanCode, splitVariants } from './class-scan.mjs';
import classMap from './md3-class-map.json' with { type: 'json' };

const LAYER = 'state-layer';
// Переход цвета спорил бы со слоем за transition-property: слой объявляет свой (`--_s`), а заливки под
// курсором больше нет.
const TRANSITION = new Set(['transition-colors', 'transition']);
// Заливка контейнера без варианта — отключённое рисует disabled-container; рамка — disabled-outlined.
// Рамка — по ширине (`border`, `border-2`, `border-b`), а не по цвету: `border-outline` без ширины рамки не
// рисует (флажок с цветом обводки). Прозрачная рамка — не обводка (вкладка без черты).
const FILLED = /^bg-(?:(?:primary|secondary|tertiary|error|success|warning)(?:-container)?|surface-container(?:-[\w-]+)?)(?:\/\S+)?$/;
const BORDER_WIDTH = /^border(?:-[xytblrse])?(?:-\d+)?$/;
const HOVER = /hover|highlighted/;
// Ветка со своим наведением заливкой (не переведённым в слой) или видом недоступного слой не делит: он
// остаётся в своей ветке.
const OWN_BRANCH = t => !t.edited && (t.variants.some(v => HOVER.test(v)) && t.cls.startsWith('bg-')) || t.raw === 'cursor-not-allowed';
const plain = t => t.variants.length === 0;
// Заливка — по имени после перевода (`resolve`): проход идёт до `names`, и выбранная ветка ещё зовётся
// старым именем (`bg-brand-subtle`).
let filled = t => plain(t) && FILLED.test(t.cls);

/**
 * Правила раздела `states`: класс → `{ to }` (`''` — снять), `feedback` — смена цвета под курсором (слой,
 * если другого отклика наведения у элемента нет), `disabled` — регулярным выражением.
 */
export function compileStates(map = classMap) {
  const s = map.states ?? {};
  const rules = new Map([
    ...(s.layer ?? []).map(t => [t, LAYER]),
    ...(s.drop ?? []).map(t => [t, '']),
    ...Object.entries(s.to ?? {}).filter(([k]) => !k.startsWith('//')),
  ]);
  return { rules, feedback: new Set(s.feedback ?? []), disabled: s.disabled ? new RegExp(s.disabled) : null };
}

/** Строки кода группами: строка и строки внутри её `${…}` (по положению в тексте). Первая — внешняя. */
function groups(text) {
  const strings = scanCode(text).strings.map(spans => ({
    spans, from: spans[0][0], to: spans[spans.length - 1][1],
  })).sort((a, b) => a.from - b.from);
  const out = [];
  for (const s of strings) {
    // Группы не пересекаются и идут по порядку: вложенная строка может быть только в последней.
    const last = out[out.length - 1];
    if (last && last.from <= s.from && s.to <= last.to) last.strings.push(s);
    else out.push({ from: s.from, to: s.to, strings: [s] });
  }
  return out;
}

/** Вид отключённого по заливке и рамке (без варианта) в своей строке и в постоянной части. */
function disabledKind(tokens) {
  if (tokens.some(filled)) return 'disabled-container';
  if (tokens.some(t => plain(t) && BORDER_WIDTH.test(t.cls)) && !tokens.some(t => t.raw === 'border-transparent'))
    return 'disabled-outlined';
  return 'disabled-content';
}

/**
 * Перевод состояний в тексте файла. `skip(at)` — место не трогать (отрицательное ожидание теста), его
 * класс называется отчётом. Возвращает текст, замены (`{ line, from, to, pair }`) и нетронутое.
 */
export function migrateStates(text, { rules = compileStates(), skip = () => false, resolve = c => c } = {}) {
  filled = t => plain(t) && FILLED.test(resolve(t.cls));
  const edits = [];
  const replaced = [];
  const untouched = [];
  const breaks = [...text.matchAll(/\n/g)].map(m => m.index);
  const lineAt = at => {
    let lo = 0;
    for (let hi = breaks.length; lo < hi;) {
      const mid = (lo + hi) >> 1;
      if (breaks[mid] < at) lo = mid + 1; else hi = mid;
    }
    return lo + 1;
  };
  const edit = (tok, to, pair) => {
    tok.edited = true;
    const rec = { line: lineAt(tok.at), from: tok.raw, to: to || '(снят)', pair, comment: false };
    edits.push({ at: tok.at, len: tok.raw.length, to, rec });
    replaced.push(rec);
  };

  const done = [];
  for (const group of groups(text)) {
    const lists = group.strings.map(s => s.spans.flatMap(([from, to]) =>
      [...text.slice(from, to).matchAll(/\S+/g)].map(m => ({ raw: m[0], at: from + m.index, ...splitVariants(m[0]) }))));
    const outer = lists[0];
    const all = lists.flat();
    // Смена цвета под курсором — отклик, пока другого нет: своего наведения (не правила карты) в своей
    // строке и постоянной части.
    const ownHover = scope => scope.some(t => t.variants.some(v => HOVER.test(v))
      && !rules.rules.has(t.raw) && !rules.feedback.has(t.raw) && !rules.disabled?.test(t.raw));
    const layers = lists.map(list => list.some(t => t.raw === LAYER));
    let dropped = false;

    lists.forEach((list, i) => {
      const scope = i === 0 ? outer : [...list, ...outer];
      let disabled = list.some(t => /^disabled-(?:container|outlined|content)$/.test(t.raw));
      for (const tok of list) {
        let to = rules.rules.get(tok.raw);
        let pair = to === LAYER ? `${tok.raw} → ${LAYER}` : to === '' ? `${tok.raw} → снят` : `${tok.raw} → ${to}`;
        if (to === undefined && rules.feedback.has(tok.raw)) {
          to = ownHover(scope) ? '' : LAYER;
          pair = to ? `${tok.raw} → ${LAYER}` : `${tok.raw} при своём наведении → снят`;
        }
        if (to === undefined && rules.disabled?.test(tok.raw)) {
          const kind = disabledKind(scope);
          to = disabled ? '' : kind;
          pair = `${tok.raw.replace(/\d+$/, 'N')} → ${kind}`;
          disabled = true;
        }
        if (to === undefined) continue;
        if (skip(tok.at)) {
          untouched.push({ line: lineAt(tok.at), token: tok.raw, reason: 'отрицательное ожидание теста — переписать руками, чтобы снова могло упасть' });
          continue;
        }
        if (to === LAYER) {
          if (layers[i]) to = '';
          layers[i] = true;
        }
        if (to === '') dropped = true;
        edit(tok, to, pair);
      }
    });

    // Слой условной ветки — в постоянную часть, если ветки — выбранное и невыбранное (у соседней своя
    // заливка или тот же слой): MD3 зажигает наведение и у выбранного пункта. Ветка со своим наведением
    // или видом недоступного слой оставляет себе; условие без пары (`${onClick && '…'}`) — тоже.
    const branches = lists.slice(1);
    const hoist = branches.length > 0 && branches.every((list, k) => layers[k + 1] || list.some(filled))
      && layers.slice(1).some(Boolean) && !branches.some(list => list.some(OWN_BRANCH));
    if (hoist) {
      for (const list of branches)
        for (const tok of list)
          if (tok.raw === LAYER && !tok.edited && !skip(tok.at)) edit(tok, '', `${LAYER} ветки → в постоянную часть`);
      for (const e of edits)
        if (e.to === LAYER && branches.some(list => list.some(t => t.at === e.at))) {
          e.to = '';
          e.rec.to = '(снят)';
          e.rec.pair += ' (слой — в постоянной части)';
        }
      if (!layers[0]) {
        const at = group.strings[0].from;
        edits.push({ at, len: 0, to: /\s/.test(text[at]) ? LAYER : `${LAYER} ` });
        replaced.push({ line: lineAt(at), from: '(ветка)', to: LAYER, pair: `${LAYER} ветки → в постоянную часть`, comment: false });
        layers[0] = true;
      }
    }

    done.push({ group, all, layered: layers.some(Boolean), dropped });
  }

  // Переход цвета без дела: у слоя свой, а снятое наведение оставило его анимировать нечего. Строки,
  // склеенные `+` (`'… transition-colors ' + `${active ? …}``), — один список классов.
  for (let i = 0; i < done.length;) {
    let j = i + 1;
    while (j < done.length && /^\s*\+\s*$/.test(text.slice(done[j - 1].group.to + 1, done[j].group.from - 1))) j++;
    const chain = done.slice(i, j);
    const all = chain.flatMap(d => d.all);
    const variantLeft = all.some(t => !t.edited && !plain(t));
    if (chain.some(d => d.layered) || (chain.some(d => d.dropped) && !variantLeft))
      for (const tok of all)
        if (TRANSITION.has(tok.raw) && !tok.edited && !skip(tok.at)) edit(tok, '', `${tok.raw} без наведения цветом → снят`);
    i = j;
  }

  // С конца: положения раньше стоящих правок не сдвигаются. Снятый класс уносит один пробел рядом —
  // предшествующий, если за ним конец строки: хвостовой пробел у склеенной `+` строки разделяет классы.
  let out = text;
  for (const { at, len, to } of edits.sort((a, b) => b.at - a.at || b.len - a.len)) {
    let from = at;
    let end = at + len;
    if (!to && len) {
      if (out[end] === ' ' && !/['"`]/.test(out[end + 1] ?? '')) end++;
      else if (out[from - 1] === ' ') from--;
    }
    out = out.slice(0, from) + to + out.slice(end);
  }
  return { text: out, replaced: replaced.sort((a, b) => a.line - b.line), untouched };
}
