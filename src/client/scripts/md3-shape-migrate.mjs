// Шкала формы и уровни возвышения MD3 (sharedui-md3-06, [UI-66]) — шаги команды md3:migrate.
//
// Радиусы переводятся цепью по значению: прежние 4/6/10/16/28 → ступени MD3 4/8/12/16/28, то есть
// `rounded-md` (6) → `rounded-sm` (8), `rounded-lg` (10) → `rounded-md` (12). Цепь сама по себе не
// идемпотентна: второй проход сдвинул бы `rounded-sm` (8) в `rounded-xs` (4). Поэтому шаг трогает
// только строки, написанные до перехода: автора строки называет `git blame`, а строка считается
// старой, если в дереве её коммита нет метки шкалы (`--md-sys-shape-corner-small` в src/theme/).
// Строки после перехода и ещё не закоммиченные не сдвигаются. Ветка, начатая до перехода, обновляется
// так: принять master в конфликтах, закоммитить слияние, затем npm run md3:migrate (docs/UI.md).
//
// Тени идемпотентны (цели не совпадают с источниками): `shadow-[var(--md-sys-elevation-levelN)]` →
// `shadow-elevation-N`, `style={{ boxShadow: 'var(--md-sys-elevation-levelN)' }}` → тот же класс в
// className элемента. Тень, которую команда не переводит, называется отчётом.

import { execFileSync } from 'node:child_process';
import { CLASS_AFTER, CLASS_BEFORE, scanCode, stripComments } from './class-scan.mjs';
import classMap from './md3-class-map.json' with { type: 'json' };

const SIDE = '(?:t|r|b|l|s|e|tl|tr|br|bl|ss|se|es|ee)';
// Группы: 1 — `!` впереди; 2 — сторона или угол; 3 — ступень (нет — `rounded` без ступени); 4 — `!` в конце.
const RADIUS = new RegExp(
  `${CLASS_BEFORE}(!?)rounded(?:-(${SIDE}))?(?:-([a-z0-9]+|\\[[^\\]\\s]+\\]|\\([^)\\s]+\\)))?(!?)${CLASS_AFTER}`, 'gm');

/** Цепь из карты: прежняя ступень (`""` — `rounded` без ступени) → новая; `report` — вне шкалы, в отчёт. */
export function compileShape(map = classMap) {
  const { chain, keep, report } = map.radius;
  return { chain, keep: new Set(keep), report };
}

/**
 * Радиусы текста по цепи. `isOld(line)` — строка написана до перехода (сдвигать); `skip(index)` — место
 * не трогать (отрицательное ожидание теста). Возвращает текст, замены и нетронутое — как migrateText.
 * Номера строк — исходного текста: шаг идёт первым, пока строки совпадают с `git blame`.
 */
export function migrateRadii(text, { isOld, skip = () => false, rules = compileShape() }) {
  const replaced = [];
  const untouched = [];
  const stripped = stripComments(text);
  let spans;
  const inString = index => (spans ??= scanCode(text).strings.flat()).some(([from, to]) => from <= index && index < to);
  let line = 1;
  let at = 0;
  const out = text.replace(RADIUS, (whole, lead, side, step = '', tail, index) => {
    for (; at < index; at++) if (text[at] === '\n') line++;
    if (rules.keep.has(step)) return whole;
    // `rounded` без ступени бывает и именем в коде: проп `{ rounded = 'md' }`, `return rounded;`,
    // `opts.rounded.trim()`, ссылка `rounded` в комментарии, селектор `.rounded`. Поэтому он класс только
    // там, где пишутся классы: в строке или шаблоне кода и после `@apply` — белым списком, а не
    // перечнем знаков после имени (тот пропустил бы `;`, `&&`, `.`).
    if (!side && !step && !inString(index) && !/@apply\b[^;\n{}]*$/.test(text.slice(text.lastIndexOf('\n', index) + 1, index)))
      return whole;
    const token = `rounded${side ? `-${side}` : ''}${step ? `-${step}` : ''}`;
    const to = rules.chain[step];
    if (to === undefined) {
      untouched.push({ line, token, reason: `${token} — ${rules.report}` });
      return whole;
    }
    if (!isOld(line)) return whole;
    if (skip(index)) {
      untouched.push({ line, token, reason: 'отрицательное ожидание теста — переписать руками, чтобы снова могло упасть' });
      return whole;
    }
    const next = `rounded${side ? `-${side}` : ''}-${to}`;
    replaced.push({ line, from: token, to: next, pair: `rounded${step ? `-${step}` : ''} → rounded-${to}`,
      comment: stripped[index] !== text[index] });
    return `${lead}${next}${tail}`;
  });
  return { text: out, replaced, untouched };
}

const LEVEL_CLASS = new RegExp(`${CLASS_BEFORE}(!?)shadow-\\[var\\(--md-sys-elevation-level([1-5])\\)\\](!?)${CLASS_AFTER}`, 'gm');
const LEVEL_STYLE = /\s*style=\{\{\s*boxShadow:\s*'var\(--md-sys-elevation-level([1-5])\)'\s*\}\}/g;
const SHADOW_REPORT = /boxShadow/g;

/** Конец строкового значения className, начатого кавычкой на `open`, или -1 (шаблон с `${…}` — по глубине скобок). */
function closing(text, open) {
  const quote = text[open];
  let depth = 0;
  for (let i = open + 1; i < text.length; i++) {
    const c = text[i];
    if (quote === '`' && c === '$' && text[i + 1] === '{') { depth++; i++; continue; }
    if (depth && c === '{') depth++;
    else if (depth && c === '}') depth--;
    else if (!depth && c === quote) return i;
    else if (!depth && c === '\n' && quote !== '`') return -1;
  }
  return -1;
}

/**
 * Тени уровней: класс с переменной → `shadow-elevation-N`; стиль с переменной → класс в className того же
 * элемента (className до стиля в том же открывающем теге). Стиль без className рядом, условный стиль и
 * свои значения тени — в отчёт: перевести их можно только по месту.
 */
export function migrateShadows(text, { skip = () => false } = {}) {
  const replaced = [];
  const untouched = [];
  // Номера строк — исходного текста. Замена класса переводов строк не трогает, и до переноса стилей номер
  // места в `out` тот же; перенос стиля убирает его строку — всё, что нужно отчёту, снято до переноса.
  const lineIn = (source, index) => source.slice(0, index).split('\n').length;
  let out = text.replace(LEVEL_CLASS, (whole, lead, n, tail, index) => {
    if (skip(index)) return whole;
    replaced.push({ line: lineIn(text, index), from: `shadow-[var(--md-sys-elevation-level${n})]`, to: `shadow-elevation-${n}`,
      pair: `shadow-[var(--md-sys-elevation-levelN)] → shadow-elevation-N`, comment: false });
    return `${lead}shadow-elevation-${n}${tail}`;
  });

  const unmoved = out;
  const moved = [];
  // Стили — с конца, чтобы правка не сдвигала ещё не обработанные места.
  const styles = [...out.matchAll(LEVEL_STYLE)].reverse();
  for (const m of styles) {
    const tag = out.lastIndexOf('<', m.index);
    const name = out.slice(tag, m.index).lastIndexOf('className=');
    const valueAt = name < 0 ? -1 : tag + name + 'className='.length;
    const open = valueAt < 0 ? -1 : out[valueAt] === '{' && out[valueAt + 1] === '`' ? valueAt + 1 : out[valueAt] === '"' ? valueAt : -1;
    const close = open < 0 ? -1 : closing(out, open);
    if (close < 0 || close > m.index) continue;
    const cls = `shadow-elevation-${m[1]}`;
    out = out.slice(0, close) + ` ${cls}` + out.slice(close, m.index) + out.slice(m.index + m[0].length);
    moved.push([m.index, m.index + m[0].length]);
    // Совпадение начинается с пробелов перед `style` — номер строки берётся у самого `style`.
    replaced.push({ line: lineIn(unmoved, m.index + m[0].indexOf('style')), from: `style boxShadow level${m[1]}`, to: cls,
      pair: 'style={{ boxShadow: var(--md-sys-elevation-levelN) }} → shadow-elevation-N', comment: false });
  }

  const stripped = stripComments(unmoved);
  for (const m of unmoved.matchAll(SHADOW_REPORT))
    if (stripped[m.index] === unmoved[m.index] && !moved.some(([from, to]) => from <= m.index && m.index < to))
      untouched.push({ line: lineIn(unmoved, m.index), token: m[0],
        reason: 'тень в стиле — перевести по месту в shadow-elevation-N по роли слоя (docs/UI.md, «Форма и возвышение»)' });
  return { text: out, replaced, untouched };
}

const ZERO = '0'.repeat(40);

/**
 * Коммиты, в дереве которых есть метка шкалы: коммит, принёсший её, и всё, что от него происходит.
 * Зовётся раз на прогон; без git (или вне репозитория) — пусто, и старыми считаются все закоммиченные строки.
 */
export function commitsAfterTransition(cwd) {
  const after = new Set();
  try {
    const run = args => execFileSync('git', args, { cwd, encoding: 'utf8', maxBuffer: 1 << 28, stdio: ['ignore', 'pipe', 'ignore'] })
      .split('\n').filter(Boolean);
    for (const t of run(['log', '--format=%H', '-S--md-sys-shape-corner-small', 'HEAD', '--', 'src/theme/'])) {
      after.add(t);
      for (const c of run(['rev-list', '--ancestry-path', `${t}..HEAD`])) after.add(c);
    }
  } catch { /* не репозиторий: меток нет */ }
  return after;
}

/**
 * Есть ли в истории HEAD коммиты до перехода, кроме истории master до него: коммиты чужой ветки, начатой
 * раньше. Без них старых строк с радиусом цепи нет — переход перевёл их все, — и `git blame` по каждому
 * файлу (десятки секунд) не нужен. Перехода нет в истории (или нет git) — старые все закоммиченные, да.
 */
export function hasCommitsBeforeTransition(cwd, after) {
  if (!after.size) return true;
  try {
    const transitions = execFileSync('git', ['log', '--format=%H', '-S--md-sys-shape-corner-small', 'HEAD', '--', 'src/theme/'],
      { cwd, encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'] }).split('\n').filter(Boolean);
    const outside = execFileSync('git', ['rev-list', 'HEAD', '--not', ...transitions],
      { cwd, encoding: 'utf8', maxBuffer: 1 << 28, stdio: ['ignore', 'pipe', 'ignore'] }).split('\n').filter(Boolean);
    return outside.some(c => !after.has(c));
  } catch {
    return true;
  }
}

/**
 * Строки файла, написанные до перехода: `line => boolean` по `git blame`. `after` — коммиты после
 * перехода (`commitsAfterTransition`). Файл вне git — ни одной.
 */
export function gitOldLines(path, cwd, after = commitsAfterTransition(cwd)) {
  let porcelain;
  try {
    porcelain = execFileSync('git', ['blame', '--porcelain', '--', path], { cwd, encoding: 'utf8', maxBuffer: 1 << 28, stdio: ['ignore', 'pipe', 'ignore'] });
  } catch {
    return () => false;
  }
  const old = new Set();
  for (const [, hash, final] of porcelain.matchAll(/^([0-9a-f]{40}) \d+ (\d+)/gm))
    if (hash !== ZERO && !after.has(hash)) old.add(Number(final));
  return line => old.has(line);
}
