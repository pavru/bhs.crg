import ts from 'typescript';
import { describe, expect, it } from 'vitest';
import { clientSources } from '@/shared/testing/clientSources';

/**
 * Сторож [UI-75] (sharedui-md3-13, задача 1б.5): действия окна — только кнопки с ролью. Во весь экран они
 * переносят себя в шапку; произвольный элемент туда не переносится и молча пропал бы с узкого экрана, а кнопка
 * с ролью вне ряда осталась бы на месте рядом с крестиком. Флажок или поле живут в теле окна.
 *
 * <p>Два правила:</p>
 * <ul>
 *   <li>в `footer` окна (`Modal`) и шага (`WindowStep`) и в ряду `ModalButtons` — только роли;</li>
 *   <li>роль стоит только там: в подвале, в ряду или в компоненте-группе, который сам ставится в ряд
 *       (`RecordWriteGate`: его кнопки проверяются там, где он стоит).</li>
 * </ul>
 *
 * <p>По дереву разбора, а не по строкам: подвал — выражение на десяток строк с условиями, и важно, что
 * стоит на его верхнем уровне, а не что написано внутри кнопки.</p>
 */

const ROLES = new Set(['ModalExit', 'ModalPrimary', 'ModalAction', 'ModalNote']);
const ROW = 'ModalButtons';
const WINDOWS = new Set(['Modal', 'WindowStep']);
/** Сами окна и роли передают подвал дальше как значение — это не подвал, а проводка. */
const WIRING = [/\/shared\/ui\/(Modal|WindowStep|ModalRoles)\.tsx$/];

type Jsx = ts.JsxOpeningElement | ts.JsxSelfClosingElement;
const tagName = (node: Jsx) => node.tagName.getText();
const opening = (node: ts.JsxElement | ts.JsxSelfClosingElement) =>
  ts.isJsxElement(node) ? node.openingElement : node;

/** Связывает ли объявление (`x`, `{ x }`, `[x]`) это имя. */
function binds(name: ts.BindingName, text: string): boolean {
  if (ts.isIdentifier(name)) return name.text === text;
  return name.elements.some(e => !ts.isOmittedExpression(e) && binds(e.name, text));
}

/** Объявление имени, видимое с места `use`: ближайшая охватывающая область, а не любое в файле. */
function declared(use: ts.Identifier): ts.Expression | 'param' | undefined {
  for (let scope: ts.Node | undefined = use.parent; scope; scope = scope.parent) {
    if (ts.isFunctionLike(scope) && scope.parameters.some(p => binds(p.name, use.text))) return 'param';
    const statements = ts.isBlock(scope) || ts.isSourceFile(scope) ? scope.statements : undefined;
    for (const st of statements ?? []) {
      if (!ts.isVariableStatement(st)) continue;
      for (const d of st.declarationList.declarations)
        if (ts.isIdentifier(d.name) && d.name.text === use.text) return d.initializer;
    }
  }
  return undefined;
}

interface Ctx { file: ts.SourceFile; groups: Set<string> }

/** Что на верхнем уровне действий не роль: имена найденного, пусто — чисто. */
function offenders(expr: ts.Node, ctx: Ctx): string[] {
  if (ts.isParenthesizedExpression(expr) || ts.isJsxExpression(expr))
    return expr.expression ? offenders(expr.expression, ctx) : [];
  if (ts.isJsxFragment(expr)) return expr.children.flatMap(c => offenders(c, ctx));
  if (ts.isJsxText(expr)) return expr.text.trim() ? [`текст «${expr.text.trim().slice(0, 20)}»`] : [];
  if (ts.isJsxElement(expr) || ts.isJsxSelfClosingElement(expr)) {
    const tag = tagName(opening(expr));
    if (ROLES.has(tag)) return [];
    // Ряд и группа — их содержимое проверяется так же: это всё ещё верхний уровень действий.
    if (tag === ROW || ctx.groups.has(tag))
      return ts.isJsxElement(expr) ? expr.children.flatMap(c => offenders(c, ctx)) : [];
    return [`<${tag}>`];
  }
  if (ts.isConditionalExpression(expr)) return [...offenders(expr.whenTrue, ctx), ...offenders(expr.whenFalse, ctx)];
  if (ts.isBinaryExpression(expr)) {
    // `cond && <X/>` — левое условие; `a ?? <X/>` и `a || <X/>` — значением бывают обе стороны.
    const both = expr.operatorToken.kind !== ts.SyntaxKind.AmpersandAmpersandToken;
    return [...(both ? offenders(expr.left, ctx) : []), ...offenders(expr.right, ctx)];
  }
  if (ts.isArrowFunction(expr)) return offenders(expr.body, ctx);
  if (expr.kind === ts.SyntaxKind.NullKeyword || expr.kind === ts.SyntaxKind.FalseKeyword) return [];
  if (ts.isIdentifier(expr)) {
    if (expr.text === 'undefined') return [];
    const found = declared(expr);
    // Параметр — содержимое группы (`children`): его проверяют там, где группу ставят.
    if (found === 'param') return [];
    return found ? offenders(found, ctx) : [`значение ${expr.text} не найдено`];
  }
  return [`выражение ${ts.SyntaxKind[expr.kind]}`];
}

/** Имя компонента, внутри которого стоит узел, — функция или стрелка с именем с заглавной. */
function componentOf(node: ts.Node): { name: string; body: ts.Node } | null {
  for (let n: ts.Node | undefined = node.parent; n; n = n.parent) {
    if (ts.isFunctionDeclaration(n) && n.name && n.body) return { name: n.name.text, body: n.body };
    if (ts.isArrowFunction(n) && ts.isVariableDeclaration(n.parent) && ts.isIdentifier(n.parent.name))
      return { name: n.parent.name.text, body: n.body };
  }
  return null;
}

/** Где стоит роль: подвал, ряд, группа — иначе нарушение (`null` — место законное). */
function misplaced(role: Jsx, groups: Set<string>): string | null {
  let n: ts.Node = ts.isJsxOpeningElement(role) ? role.parent : role;
  for (let up = n.parent; up; n = up, up = up.parent) {
    if (ts.isJsxAttribute(up)) return up.name.getText() === 'footer' ? null : `в атрибуте ${up.name.getText()}`;
    if (ts.isJsxElement(up) && up.openingElement !== n) {
      const tag = tagName(up.openingElement);
      return tag === ROW || groups.has(tag) || ROLES.has(tag) ? null : `внутри <${tag}>`;
    }
    // Подвал, собранный в переменную, проверяется там, где стоит (`footer={footer}`).
    if (ts.isVariableDeclaration(up)) return null;
    // Верхний уровень возвращаемого компонентом — законно только у группы.
    if (ts.isReturnStatement(up) || (ts.isArrowFunction(up) && up.body === n)) {
      const owner = componentOf(n);
      return owner && groups.has(owner.name) ? null : `вне ряда в ${owner?.name ?? 'функции'}`;
    }
  }
  return 'вне ряда';
}

const parse = (path: string, code: string) => ts.createSourceFile(path, code, ts.ScriptTarget.Latest, true, ts.ScriptKind.TSX);

/** Компоненты-группы: всё, что они возвращают, — роли и ряд, и среди этого есть роль или ряд. */
function findGroups(files: ts.SourceFile[]): Set<string> {
  const groups = new Set<string>();
  for (const file of files) {
    const visit = (n: ts.Node) => {
      if (ts.isReturnStatement(n) && n.expression) {
        const owner = componentOf(n);
        const e = n.expression;
        const top = ts.isParenthesizedExpression(e) ? e.expression : e;
        const jsx = ts.isJsxFragment(top) || ts.isJsxElement(top) || ts.isJsxSelfClosingElement(top);
        if (owner && /^[A-Z]/.test(owner.name) && jsx && /<(ModalExit|ModalPrimary|ModalAction|ModalNote|ModalButtons)\b/.test(top.getText())
          && offenders(top, { file, groups: new Set() }).length === 0)
          groups.add(owner.name);
      }
      n.forEachChild(visit);
    };
    visit(file);
  }
  return groups;
}

function roleViolations(sources: Record<string, string>): string[] {
  const files = Object.entries(sources)
    .filter(([path, code]) => path.endsWith('.tsx') && !/\.test\.tsx$/.test(path) && !WIRING.some(re => re.test(path))
      && /<(Modal|WindowStep|ModalButtons|ModalExit|ModalPrimary|ModalAction|ModalNote)\b/.test(code))
    .map(([path, code]) => parse(path, code));
  const groups = findGroups(files);
  const found: string[] = [];
  for (const file of files) {
    const at = (n: ts.Node) => `${file.fileName}:${file.getLineAndCharacterOfPosition(n.getStart()).line + 1}`;
    const visit = (n: ts.Node) => {
      if (ts.isJsxOpeningElement(n) || ts.isJsxSelfClosingElement(n)) {
        const tag = tagName(n);
        if (WINDOWS.has(tag)) {
          for (const attr of n.attributes.properties) {
            if (!ts.isJsxAttribute(attr) || attr.name.getText() !== 'footer' || !attr.initializer) continue;
            for (const bad of offenders(attr.initializer, { file, groups })) found.push(`${at(attr)} — ${bad}`);
          }
        }
        // Ряд и группа, где бы ни стояли: группа вне ряда (`RecordWriteGate` рисует ряд сам) — тоже ряд.
        if ((tag === ROW || groups.has(tag)) && ts.isJsxOpeningElement(n))
          for (const bad of n.parent.children.flatMap(c => offenders(c, { file, groups }))) found.push(`${at(n)} — ${bad}`);
        if (ROLES.has(tag)) {
          const where = misplaced(n, groups);
          if (where) found.push(`${at(n)} — <${tag}> ${where}`);
        }
      }
      n.forEachChild(visit);
    };
    visit(file);
  }
  // Кнопку в группе внутри ряда видят и ряд, и группа — одна находка, а не две.
  return [...new Set(found)];
}

describe('действия окна и шага — только кнопки с ролью ([UI-75])', () => {
  it('во всём клиенте', () => {
    expect(roleViolations(clientSources)).toEqual([]);
  });

  it('сторож видит чужой элемент и пропускает роли (самопроверка)', () => {
    const fake = {
      '/src/a.tsx': `const f = <><ModalExit /><ModalPrimary>Да</ModalPrimary></>;
        export const A = () => <Modal footer={<>{x && <ModalNote>п</ModalNote>}<Button>Ещё</Button></>} />;
        export const B = () => <WindowStep footer={f} />;
        export const C = () => <Modal footer={<div className="flex"><ModalExit /></div>} />;
        export function D() { const f = <Button />; return <Modal footer={f} />; }
        export const E = () => <Modal footer={custom ?? <ModalExit />} />;
        export const F = () => <form><ModalButtons><ModalExit /><Button>Да</Button></ModalButtons></form>;
        export const G = () => <form><div><ModalExit /><ModalPrimary>Да</ModalPrimary></div></form>;
        function Gate({ children }) { return <>{ok && <ModalNote>н</ModalNote>}<ModalExit />{children}</>; }
        export const H = () => <ModalButtons><Gate><ModalPrimary>Да</ModalPrimary></Gate></ModalButtons>;
        export const I = () => <ModalButtons><Gate><Button>Да</Button></Gate></ModalButtons>;`,
    };
    expect(roleViolations(fake)).toEqual([
      '/src/a.tsx:2 — <Button>',
      '/src/a.tsx:4 — <div>',
      '/src/a.tsx:4 — <ModalExit> внутри <div>',
      '/src/a.tsx:5 — <Button>',
      '/src/a.tsx:6 — значение custom не найдено',
      '/src/a.tsx:7 — <Button>',
      '/src/a.tsx:8 — <ModalExit> внутри <div>',
      '/src/a.tsx:8 — <ModalPrimary> внутри <div>',
      '/src/a.tsx:11 — <Button>',
    ]);
  });
});
