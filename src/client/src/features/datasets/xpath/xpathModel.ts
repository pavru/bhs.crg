/**
 * Буквенный поднабор XPath 1.0, который умеет строить/разбирать визуальный builder.
 * Всё, что выходит за рамки этой грамматики (функции кроме contains()/last(), объединения `|`,
 * оси кроме child/attribute, произвольная вложенность булевой логики) не парсится —
 * parseXPath возвращает null, и вызывающий код должен показать выражение как сырой текст.
 *
 * Грамматика шага: (@)?(Имя|*) ([предикат])*
 * Предикат: N | last() | путь(=|!=)'значение' | contains(путь,'значение') | путь (просто "существует")
 * "путь" внутри предиката — простой относительный путь: Имя(/Имя)*(/@Имя)? | @Имя
 */

export type XPathAxis = 'child' | 'attribute';

export type XPathPredicate =
  | { kind: 'position'; op: 'last' }
  | { kind: 'position'; op: 'index'; index: number }
  | { kind: 'equals'; path: string; op: '=' | '!='; value: string }
  | { kind: 'contains'; path: string; value: string }
  | { kind: 'exists'; path: string };

export interface XPathStep {
  axis: XPathAxis;
  name: string; // имя элемента/атрибута или '*'
  predicates: XPathPredicate[];
}

export interface XPathModel {
  absolute: boolean; // начинается с '/'
  steps: XPathStep[];
}

const NAME_RE = /[\p{L}_][\p{L}\p{N}_.\-]*/u;
const SIMPLE_PATH_RE = new RegExp(
  `^(?:@${NAME_RE.source}|${NAME_RE.source}(?:\\/${NAME_RE.source})*(?:\\/@${NAME_RE.source})?)$`,
  'u',
);

// ─── Serialize: model → text ────────────────────────────────────────────────────

export function toXPath(model: XPathModel): string {
  const body = model.steps.map(stepToXPath).join('/');
  return model.absolute ? `/${body}` : body;
}

function stepToXPath(step: XPathStep): string {
  const head = step.axis === 'attribute' ? `@${step.name}` : step.name;
  const preds = step.predicates.map(p => `[${predicateToXPath(p)}]`).join('');
  return head + preds;
}

function predicateToXPath(p: XPathPredicate): string {
  switch (p.kind) {
    case 'position':
      return p.op === 'last' ? 'last()' : String(p.index);
    case 'equals':
      return `${p.path}${p.op}${quote(p.value)}`;
    case 'contains':
      return `contains(${p.path}, ${quote(p.value)})`;
    case 'exists':
      return p.path;
  }
}

/** XPath 1.0 не умеет экранировать кавычки — если значение содержит оба вида, вернуть null. */
export function quote(value: string): string {
  if (!value.includes("'")) return `'${value}'`;
  if (!value.includes('"')) return `"${value}"`;
  throw new Error('Значение содержит и одинарные, и двойные кавычки — не может быть представлено в XPath 1.0');
}

export function isSimplePath(path: string): boolean {
  return SIMPLE_PATH_RE.test(path.trim());
}

// ─── Parse: text → model | null ─────────────────────────────────────────────────

export function parseXPath(text: string): XPathModel | null {
  const trimmed = text.trim();
  if (!trimmed) return null;

  const absolute = trimmed.startsWith('/');
  const body = absolute ? trimmed.slice(1) : trimmed;
  if (!body) return null;

  const rawSteps = splitTopLevel(body, '/');
  const steps: XPathStep[] = [];
  for (let i = 0; i < rawSteps.length; i++) {
    const step = parseStep(rawSteps[i]);
    if (!step) return null;
    // Атрибут — только в последнем шаге пути (у атрибута нет потомков).
    if (step.axis === 'attribute' && i !== rawSteps.length - 1) return null;
    steps.push(step);
  }
  return { absolute, steps };
}

/** Разбивает строку по разделителю, игнорируя вхождения внутри '...'/"..." и [...]. */
function splitTopLevel(s: string, sep: string): string[] {
  const parts: string[] = [];
  let depth = 0;
  let quoteChar: string | null = null;
  let cur = '';
  for (const ch of s) {
    if (quoteChar) {
      cur += ch;
      if (ch === quoteChar) quoteChar = null;
      continue;
    }
    if (ch === "'" || ch === '"') { quoteChar = ch; cur += ch; continue; }
    if (ch === '[') { depth++; cur += ch; continue; }
    if (ch === ']') { depth--; cur += ch; continue; }
    if (ch === sep && depth === 0) { parts.push(cur); cur = ''; continue; }
    cur += ch;
  }
  parts.push(cur);
  return parts;
}

function parseStep(raw: string): XPathStep | null {
  let i = 0;
  let axis: XPathAxis = 'child';
  if (raw[i] === '@') { axis = 'attribute'; i++; }

  let name: string;
  if (raw[i] === '*') { name = '*'; i++; }
  else {
    const m = NAME_RE.exec(raw.slice(i));
    if (!m || m.index !== 0) return null;
    name = m[0];
    i += name.length;
  }

  const predicates: XPathPredicate[] = [];
  while (i < raw.length) {
    if (raw[i] !== '[') return null; // мусор после имени
    const end = findMatchingBracket(raw, i);
    if (end < 0) return null;
    const predicate = parsePredicate(raw.slice(i + 1, end));
    if (!predicate) return null;
    predicates.push(predicate);
    i = end + 1;
  }

  return { axis, name, predicates };
}

/** Находит индекс закрывающей ']' для '[' на позиции start, учитывая вложенность и кавычки. */
function findMatchingBracket(s: string, start: number): number {
  let depth = 1;
  let quoteChar: string | null = null;
  for (let j = start + 1; j < s.length; j++) {
    const ch = s[j];
    if (quoteChar) { if (ch === quoteChar) quoteChar = null; continue; }
    if (ch === "'" || ch === '"') { quoteChar = ch; continue; }
    if (ch === '[') depth++;
    else if (ch === ']') { depth--; if (depth === 0) return j; }
  }
  return -1;
}

const QUOTED_RE = /^(['"])((?:(?!\1).)*)\1$/;

function parsePredicate(raw: string): XPathPredicate | null {
  const t = raw.trim();
  if (!t) return null;

  if (t === 'last()') return { kind: 'position', op: 'last' };
  if (/^\d+$/.test(t)) return { kind: 'position', op: 'index', index: Number(t) };

  const containsMatch = /^contains\(\s*([^,]+?)\s*,\s*(.+)\)$/.exec(t);
  if (containsMatch) {
    const path = containsMatch[1].trim();
    const quoted = QUOTED_RE.exec(containsMatch[2].trim());
    if (isSimplePath(path) && quoted) return { kind: 'contains', path, value: quoted[2] };
    return null;
  }

  const cmpMatch = /^(.+?)\s*(!=|=)\s*(['"].*)$/.exec(t);
  if (cmpMatch) {
    const path = cmpMatch[1].trim();
    const quoted = QUOTED_RE.exec(cmpMatch[3].trim());
    if (isSimplePath(path) && quoted) {
      return { kind: 'equals', path, op: cmpMatch[2] as '=' | '!=', value: quoted[2] };
    }
    return null;
  }

  if (isSimplePath(t)) return { kind: 'exists', path: t };
  return null;
}
