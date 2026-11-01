import ts from 'typescript';
import { describe, expect, it } from 'vitest';
import { clientSources } from '@/shared/testing/clientSources';

/**
 * Сторож [UI-75] (sharedui-md3-13, задача 1б.5): в `footer` окна (`Modal`) и шага (`WindowStep`) — только
 * кнопки с ролью. Во весь экран подвал переносит себя в шапку по ролям; произвольный элемент туда не
 * переносится и молча пропал бы с узкого экрана. Флажок или поле подвала живут в теле окна.
 *
 * <p>По дереву разбора, а не по строкам: подвал — выражение на десяток строк с условиями, и важно, что
 * стоит на его верхнем уровне, а не что написано внутри кнопки.</p>
 */

const ROLES = new Set(['ModalExit', 'ModalPrimary', 'ModalAction', 'ModalNote']);
const WINDOWS = new Set(['Modal', 'WindowStep']);
/** Сами окна передают подвал дальше как значение — это не подвал, а проводка. */
const WIRING = [/\/shared\/ui\/(Modal|WindowStep)\.tsx$/];

function tagName(node: ts.JsxOpeningElement | ts.JsxSelfClosingElement): string {
  return node.tagName.getText();
}

/** Что на верхнем уровне подвала не роль: имена найденного, пусто — подвал чистый. */
function offenders(expr: ts.Node, file: ts.SourceFile): string[] {
  if (ts.isParenthesizedExpression(expr) || ts.isJsxExpression(expr))
    return expr.expression ? offenders(expr.expression, file) : [];
  if (ts.isJsxFragment(expr)) return expr.children.flatMap(c => offenders(c, file));
  if (ts.isJsxText(expr)) return expr.text.trim() ? [`текст «${expr.text.trim().slice(0, 20)}»`] : [];
  if (ts.isJsxElement(expr)) return ROLES.has(tagName(expr.openingElement)) ? [] : [`<${tagName(expr.openingElement)}>`];
  if (ts.isJsxSelfClosingElement(expr)) return ROLES.has(tagName(expr)) ? [] : [`<${tagName(expr)}>`];
  if (ts.isConditionalExpression(expr)) return [...offenders(expr.whenTrue, file), ...offenders(expr.whenFalse, file)];
  if (ts.isBinaryExpression(expr)) return offenders(expr.right, file);
  if (ts.isArrowFunction(expr)) return offenders(expr.body, file);
  if (expr.kind === ts.SyntaxKind.NullKeyword || expr.kind === ts.SyntaxKind.FalseKeyword) return [];
  if (ts.isIdentifier(expr)) {
    if (expr.text === 'undefined') return [];
    // Подвал, собранный заранее в переменную, — проверяется её значение.
    let found: ts.Expression | undefined;
    const visit = (n: ts.Node) => {
      if (ts.isVariableDeclaration(n) && ts.isIdentifier(n.name) && n.name.text === expr.text) found = n.initializer;
      n.forEachChild(visit);
    };
    visit(file);
    return found ? offenders(found, file) : [`значение ${expr.text} не найдено`];
  }
  return [`выражение ${ts.SyntaxKind[expr.kind]}`];
}

function footerViolations(sources: Record<string, string>): string[] {
  const found: string[] = [];
  for (const [path, code] of Object.entries(sources)) {
    if (!path.endsWith('.tsx') || /\.test\.tsx$/.test(path) || WIRING.some(re => re.test(path))) continue;
    if (!code.includes('footer=')) continue;
    const file = ts.createSourceFile(path, code, ts.ScriptTarget.Latest, true, ts.ScriptKind.TSX);
    const visit = (n: ts.Node) => {
      if ((ts.isJsxOpeningElement(n) || ts.isJsxSelfClosingElement(n)) && WINDOWS.has(tagName(n))) {
        for (const attr of n.attributes.properties) {
          if (!ts.isJsxAttribute(attr) || attr.name.getText() !== 'footer' || !attr.initializer) continue;
          const line = file.getLineAndCharacterOfPosition(attr.getStart()).line + 1;
          for (const bad of offenders(attr.initializer, file)) found.push(`${path}:${line} — ${bad}`);
        }
      }
      n.forEachChild(visit);
    };
    visit(file);
  }
  return found;
}

describe('подвал окна и шага — только кнопки с ролью ([UI-75])', () => {
  it('во всём клиенте', () => {
    expect(footerViolations(clientSources)).toEqual([]);
  });

  it('сторож видит чужой элемент и пропускает роли (самопроверка)', () => {
    const fake = {
      '/src/a.tsx': `const f = <><ModalExit /><ModalPrimary>Да</ModalPrimary></>;
        export const A = () => <Modal footer={<>{x && <ModalNote>п</ModalNote>}<Button>Ещё</Button></>} />;
        export const B = () => <WindowStep footer={f} />;
        export const C = () => <Modal footer={<div className="flex"><ModalExit /></div>} />;`,
    };
    expect(footerViolations(fake)).toEqual(['/src/a.tsx:2 — <Button>', '/src/a.tsx:4 — <div>']);
  });
});
