// Перевод самодельных состояний на модель MD3 (sharedui-md3-05, [UI-64]) — проход кодмода md3:migrate
// ПЕРЕД переводом имён: правила здесь — о классе целиком, с вариантом (`hover:bg-brand-hover` снимается в
// пользу слоя, а `text-brand-hover` без варианта уходит дальше, в `names` карты). Строки — раздел `states`
// карты md3-class-map.json.
//
// Список классов — строка кода (`scanCode`); группа — строка вместе со строками внутри её `${…}`: там
// живёт наведение условной ветки (`${active ? 'bg-primary-container' : 'hover:bg-surface-container'}`), а
// рамка и заливка, по которым выбирается вид отключённого, — в постоянной части.

import { scanCode } from './class-scan.mjs';
import classMap from './md3-class-map.json' with { type: 'json' };

const LAYER = 'state-layer';
// Переход цвета спорил бы со слоем за transition-property: слой объявляет свой (`--_s`), а заливки под
// курсором больше нет.
const TRANSITION = new Set(['transition-colors', 'transition']);
// Заливка контейнера без варианта — отключённое рисует disabled-container; рамка — disabled-outlined.
const FILLED = /^bg-(?:(?:primary|secondary|tertiary|error|success|warning)(?:-container)?|surface-container(?:-[\w-]+)?)(?:\/\S+)?$/;
const OUTLINED = /^border(?:-(?:outline|outline-variant|primary|error|warning|success))?$/;

/** Класс без вариантов: `hover:bg-x/5` → `bg-x/5`; двоеточие в скобках варианта класс не делит. */
function utility(token) {
  let depth = 0;
  let start = 0;
  for (let k = 0; k < token.length; k++) {
    if (token[k] === '[' || token[k] === '(') depth++;
    else if (token[k] === ']' || token[k] === ')') depth--;
    else if (token[k] === ':' && depth === 0) start = k + 1;
  }
  return token.slice(start);
}

/** Правила раздела `states`: класс → `{ to }` (`''` — снять), а `disabled` — регулярным выражением. */
export function compileStates(map = classMap) {
  const s = map.states ?? {};
  const rules = new Map([
    ...(s.layer ?? []).map(t => [t, LAYER]),
    ...(s.drop ?? []).map(t => [t, '']),
    ...Object.entries(s.to ?? {}).filter(([k]) => !k.startsWith('//')),
  ]);
  return { rules, disabled: s.disabled ? new RegExp(s.disabled) : null };
}

/** Строки кода группами: строка и строки внутри её `${…}` (по положению в тексте). */
function groups(text) {
  const strings = scanCode(text).strings.map(spans => ({
    spans, from: spans[0][0], to: spans[spans.length - 1][1],
  })).sort((a, b) => a.from - b.from);
  const out = [];
  for (const s of strings) {
    const outer = out.find(g => g.from <= s.from && s.to <= g.to);
    if (outer) outer.strings.push(s);
    else out.push({ from: s.from, to: s.to, strings: [s] });
  }
  return out;
}

/**
 * Перевод состояний в тексте файла. `skip(at)` — место не трогать (отрицательное ожидание теста), его
 * класс называется отчётом. Возвращает текст, замены (`{ line, from, to, pair }`) и нетронутое.
 */
export function migrateStates(text, { rules = compileStates(), skip = () => false } = {}) {
  const edits = [];
  const replaced = [];
  const untouched = [];
  const lineAt = at => text.slice(0, at).split('\n').length;
  const edit = (tok, to, pair) => {
    edits.push({ at: tok.at, len: tok.raw.length, to });
    replaced.push({ line: lineAt(tok.at), from: tok.raw, to: to || '(снят)', pair, comment: false });
  };

  for (const group of groups(text)) {
    const lists = group.strings.map(s => s.spans.flatMap(([from, to]) =>
      [...text.slice(from, to).matchAll(/\S+/g)].map(m => ({ raw: m[0], at: from + m.index, cls: utility(m[0]) }))));
    const all = lists.flat();
    const filled = all.some(t => t.cls === t.raw && FILLED.test(t.cls));
    const outlined = all.some(t => t.cls === t.raw && OUTLINED.test(t.cls));
    const disabledTo = filled ? 'disabled-container' : outlined ? 'disabled-outlined' : 'disabled-content';
    let layered = false;

    for (const list of lists) {
      let layer = list.some(t => t.raw === LAYER);
      let disabled = list.some(t => /^disabled-(?:container|outlined|content)$/.test(t.raw));
      for (const tok of list) {
        let to = rules.rules.get(tok.raw);
        let pair = to === LAYER ? `${tok.raw} → ${LAYER}` : to === '' ? `${tok.raw} → снят` : `${tok.raw} → ${to}`;
        if (to === undefined && rules.disabled?.test(tok.raw)) {
          to = disabled ? '' : disabledTo;
          pair = `${tok.raw.replace(/\d+$/, 'N')} → ${disabledTo}`;
          disabled = true;
        }
        if (to === undefined) continue;
        if (skip(tok.at)) {
          untouched.push({ line: lineAt(tok.at), token: tok.raw, reason: 'отрицательное ожидание теста — переписать руками, чтобы снова могло упасть' });
          continue;
        }
        if (to === LAYER) {
          if (layer) to = '';
          layer = true;
        }
        edit(tok, to, pair);
      }
      layered ||= layer;
    }
    if (layered)
      for (const tok of all)
        if (TRANSITION.has(tok.raw) && !skip(tok.at)) edit(tok, '', `${tok.raw} у слоя состояния → снят`);
  }

  // С конца: положения раньше стоящих правок не сдвигаются. Снятый класс уносит один пробел рядом.
  let out = text;
  for (const { at, len, to } of edits.sort((a, b) => b.at - a.at)) {
    let from = at;
    let end = at + len;
    if (!to) {
      if (out[end] === ' ') end++;
      else if (out[from - 1] === ' ') from--;
    }
    out = out.slice(0, from) + to + out.slice(end);
  }
  return { text: out, replaced: replaced.sort((a, b) => a.line - b.line), untouched };
}
