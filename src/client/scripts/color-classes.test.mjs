import { readdirSync, readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { beforeAll, describe, expect, it } from 'vitest';
import { colorCandidates, loadColorCheck } from './color-classes-lib.mjs';

// Сторож цветовых классов без токена (issue #1362): почему так — в color-classes-lib.mjs.
const srcRoot = join(dirname(fileURLToPath(import.meta.url)), '..', 'src');
let unknown;
beforeAll(async () => { unknown = await loadColorCheck(join(srcRoot, 'index.css')); });

describe('цветовые классы: что сторож видит', () => {
  it('утилиту с вариантами, прозрачностью и в шаблонной строке', () => {
    expect(colorCandidates('hover:bg-surface2 has-[:disabled]:bg-muted `${a} border-danger/30` "text-fg"')
      .map(c => c.cls)).toEqual(['bg-surface2', 'bg-muted', 'border-danger/30', 'text-fg']);
  });

  it('номер строки', () => {
    expect(colorCandidates('a\nb\n  className="p-1 text-fg"')).toEqual([{ cls: 'text-fg', line: 3 }]);
  });

  it('комментарии пропускает, строки — нет; номер строки после комментария верен', () => {
    expect(colorCandidates('/* text-fg\n bg-surface2 */ "https://x" // text-fg\n<p className="text-fg"/>'))
      .toEqual([{ cls: 'text-fg', line: 3 }]);
    expect(colorCandidates(`{/* text-xs/fg2 */} '//' + "bg-surface2"`).map(c => c.cls)).toEqual(['bg-surface2']);
  });

  it('произвольные значения и не-классы не берёт', () => {
    expect(colorCandidates('bg-[var(--f-bg3)] text-(--x) context-menu textual')).toEqual([]);
  });
});

describe('цветовые классы: что Tailwind не порождает', () => {
  it('имена, которых нет в @theme, — отказ', () => {
    expect(unknown(['bg-surface2', 'text-fg', 'border-primary', 'border-danger-border', 'stroke-strong']))
      .toEqual(['bg-surface2', 'text-fg', 'border-primary', 'border-danger-border', 'stroke-strong']);
  });

  it('токены, прозрачность и не-цветовые значения тех же утилит — проходят', () => {
    expect(unknown(['bg-muted', 'text-fg1', 'border-danger/30', 'bg-black/40', 'text-xs', 'text-caption',
      'text-left', 'border-2', 'border-t', 'ring-2', 'outline-none', 'bg-transparent', 'shadow-sm'])).toEqual([]);
  });
});

it('в клиенте нет цветовых классов без токена', () => {
  const files = readdirSync(srcRoot, { recursive: true })
    .map(f => String(f).replaceAll('\\', '/'))
    .filter(f => /\.(tsx?|jsx?)$/.test(f));
  const found = files.flatMap(file => colorCandidates(readFileSync(join(srcRoot, file), 'utf8'))
    .map(c => ({ ...c, file })));
  const bad = new Set(unknown(found.map(c => c.cls)));
  const offenders = found.filter(c => bad.has(c.cls)).map(c => `src/${c.file}:${c.line} ${c.cls}`);

  // Сообщение называет выход: без него класс заменяют первым похожим, и он снова не рисуется.
  expect(offenders, 'Цветовые классы без токена — Tailwind их не рисует, место остаётся без цвета.\n'
    + 'Токены — @theme inline в src/index.css; что для чего — docs/UI.md, «Цвет»\n'
    + '(text-fg → text-fg1, bg-surface2 → bg-muted, *-primary → *-brand, border-danger-border → border-danger/30).\n'
    + 'В комментарии называйте класс целиком (bg-stroke-strong, а не stroke-strong).').toEqual([]);
});
