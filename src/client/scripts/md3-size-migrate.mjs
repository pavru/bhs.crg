#!/usr/bin/env node
// Одна высота кнопки и знак кнопки-иконки ([UI-68], sharedui-md3-10) — перезапускаемой командой, как
// перевод классов (md3-migrate.mjs, D3 зонтика): ветка, начатая до перехода, после слияния master падает
// на `tsc -b`, и та же команда переводит её механически. Зовётся из `npm run md3:migrate`.
//
// Что переводит:
//   <Button size="sm" …>, <IconButton size="lg" …>, <ModalExit size="sm" …>, <PrimaryActionButton size=…>
//                                                    → атрибут снят: размера у кнопки больше нет
//   <IconButton …><Icon icon={X} size={18} /></IconButton>
//                                                    → <IconButton … icon={X} />  (знак рисует кнопка)
//   <Icon … className="text-error" />               → iconClassName="text-error"  (цвет знака ролью)
//   {c ? <Icon icon={A} … /> : <Icon icon={B} … />}  → icon={c ? A : B}  (если прочие свойства равны)
//   {busy ? <CircularProgress … /> : <Icon icon={X} />} → loading={busy} icon={X}
//
// Кнопка узнаётся по импорту, а не по имени тега: `Button` из shared/ui/Button, `ModalExit` из
// shared/ui/Modal, `PrimaryActionButton` из shared/ui/PrimaryActionButton. Чужой `Button` с тем же именем
// (и `size` у `Checkbox`) не трогается. Ребёнок другого вида — отчёт с файлом и строкой: угаданная замена
// хуже отказа, а `tsc -b` такое место назовёт сам.

import ts from 'typescript';

/** Модуль → имена кнопок из него. Путь — хвост спецификатора импорта (`@/shared/ui/Button`, `./Button`). */
const OWNERS = [
  [/(?:^|\/)Button$/, ['Button', 'IconButton']],
  [/(?:^|\/)Modal$/, ['ModalExit']],
  [/(?:^|\/)PrimaryActionButton$/, ['PrimaryActionButton']],
];

/** Локальные имена кнопок файла: `import { Button as B }` даёт `B → Button`. */
function buttonNames(sf) {
  const names = new Map();
  for (const st of sf.statements) {
    if (!ts.isImportDeclaration(st) || !st.importClause?.namedBindings || !ts.isNamedImports(st.importClause.namedBindings)) continue;
    const spec = st.moduleSpecifier.text;
    const own = OWNERS.find(([re]) => re.test(spec))?.[1];
    if (!own) continue;
    for (const el of st.importClause.namedBindings.elements) {
      const imported = (el.propertyName ?? el.name).text;
      if (own.includes(imported)) names.set(el.name.text, imported);
    }
  }
  return names;
}

const attrName = (a, sf) => ts.isJsxAttribute(a) ? a.name.getText(sf) : null;
const attrOf = (open, name, sf) => open.attributes.properties.find(a => attrName(a, sf) === name);

/** `<Icon icon={X} size={N} …/>` → `{ icon: 'X', rest: { className: '"…"' } }` или null. */
function iconParts(node, sf) {
  if (!ts.isJsxSelfClosingElement(node) || node.tagName.getText(sf) !== 'Icon') return null;
  let icon = null;
  const rest = [];
  for (const a of node.attributes.properties) {
    const name = attrName(a, sf);
    if (name === 'icon' && a.initializer && ts.isJsxExpression(a.initializer)) icon = a.initializer.expression.getText(sf);
    else if (name === 'size' || name === 'aria-hidden') continue;
    else if (name === 'className' && a.initializer) rest.push(`iconClassName=${a.initializer.getText(sf)}`);
    else return null; // `filled`, разворот, своё имя — смысл места, руками
  }
  return icon ? { icon, rest } : null;
}

/** Ребёнок кнопки-иконки → атрибуты, которыми его заменить, или null (отчёт). */
function glyphAttrs(child, sf) {
  const direct = iconParts(child, sf);
  if (direct) return [`icon={${direct.icon}}`, ...direct.rest];
  if (!ts.isJsxExpression(child) || !child.expression || !ts.isConditionalExpression(child.expression)) return null;
  const { condition, whenTrue, whenFalse } = child.expression;
  const cond = condition.getText(sf);
  const a = iconParts(whenTrue, sf);
  const b = iconParts(whenFalse, sf);
  if (a && b && a.rest.join(' ') === b.rest.join(' ')) return [`icon={${cond} ? ${a.icon} : ${b.icon}}`, ...a.rest];
  const spinner = n => ts.isJsxSelfClosingElement(n) && n.tagName.getText(sf) === 'CircularProgress';
  if (spinner(whenTrue) && b) return [`loading={${cond}}`, `icon={${b.icon}}`, ...b.rest];
  return null;
}

const line = (sf, pos) => sf.getLineAndCharacterOfPosition(pos).line + 1;

/**
 * Перевод одного файла. Возвращает `{ text, replaced, untouched }`: снятые атрибуты и переведённые знаки
 * — `{ line, what }`, нетронутое — `{ line, token, reason }`.
 */
export function migrateSizes(text, file = 'file.tsx') {
  const replaced = [];
  const untouched = [];
  if (!/\.[jt]sx$/.test(file) || !/\bsize=|<IconButton\b/.test(text))
    return { text, replaced, untouched };
  const sf = ts.createSourceFile(file, text, ts.ScriptTarget.Latest, true, ts.ScriptKind.TSX);
  const names = buttonNames(sf);
  if (!names.size) return { text, replaced, untouched };
  const edits = [];

  /** Открывающий тег без `size` — текстом, с хвостом `>` или `/>` как был. */
  const withoutSize = open => {
    const size = attrOf(open, 'size', sf);
    const start = open.getStart(sf);
    const src = text.slice(start, open.end);
    if (!size) return { src, cut: false };
    return { src: src.slice(0, size.pos - start) + src.slice(size.end - start), cut: true };
  };

  const visit = node => {
    if (ts.isJsxElement(node) && names.get(node.openingElement.tagName.getText(sf)) === 'IconButton') {
      const open = node.openingElement;
      const kids = node.children.filter(c => !(ts.isJsxText(c) && !c.text.trim()));
      const glyph = kids.length === 1 ? glyphAttrs(kids[0], sf) : null;
      if (!glyph) {
        untouched.push({ line: line(sf, node.getStart(sf)), token: '<IconButton>…</IconButton>',
          reason: 'ребёнок кнопки-иконки — не знак `Icon`: перенести в `icon`, `loading` или `iconClassName` руками' });
      } else {
        const { src, cut } = withoutSize(open);
        const head = src.replace(/\s*>$/, '');
        edits.push([node.getStart(sf), node.end, `${head} ${glyph.join(' ')} />`]);
        if (cut) replaced.push({ line: line(sf, open.getStart(sf)), what: 'IconButton size' });
        replaced.push({ line: line(sf, open.getStart(sf)), what: 'IconButton знак → icon' });
        return; // внутри знака кнопок нет
      }
    }
    const open = ts.isJsxElement(node) ? node.openingElement : ts.isJsxSelfClosingElement(node) ? node : null;
    if (open && names.has(open.tagName.getText(sf))) {
      const size = attrOf(open, 'size', sf);
      if (size) {
        edits.push([size.pos, size.end, '']);
        replaced.push({ line: line(sf, size.getStart(sf)), what: `${names.get(open.tagName.getText(sf))} size` });
      }
    }
    ts.forEachChild(node, visit);
  };
  visit(sf);

  if (!edits.length) return { text, replaced, untouched };
  edits.sort((a, b) => b[0] - a[0]);
  let out = text;
  for (const [from, to, by] of edits) out = out.slice(0, from) + by + out.slice(to);
  return { text: out, replaced, untouched };
}

/** Отчёт текстом: что снято по видам, затем нетронутое с `файл:строка`. */
export function formatSizeReport(report, { check = false } = {}) {
  const all = report.flatMap(r => r.replaced);
  const out = [`md3:migrate (размер кнопок): замен ${all.length}${check ? ' (не записано, --check)' : ''} в ${
    report.filter(r => r.replaced.length).length} файлах.`];
  const kinds = new Map();
  for (const r of all) kinds.set(r.what, (kinds.get(r.what) ?? 0) + 1);
  for (const [what, n] of kinds) out.push(`  ${what}  ${n}`);
  const left = report.flatMap(r => r.untouched.map(u => `    ${r.file}:${u.line} ${u.token} — ${u.reason}`));
  if (left.length) out.push('', `Не тронуто — руками (${left.length}):`, ...left);
  return out.join('\n');
}
