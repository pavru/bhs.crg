import { readdirSync, readFileSync, statSync } from 'node:fs';
import { join, relative, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';

/**
 * Меню — одно на всё приложение ([UI-76], sharedui-md3-13): слой Radix `DropdownMenu` рисует только
 * `shared/ui/Menu.tsx`. Экран, взявший пакет сам, рисует свой слой — с рамкой, своим радиусом и своей
 * высотой пункта; так их и стало 13 разных до этого change.
 *
 * Смотрит импорт пакета, а не имена: `Content`, `SubContent` и импорт под другим именем ловятся одним
 * правилом. Тесты не исключены — у них есть `Menu`.
 */
const SRC = fileURLToPath(new URL('../src', import.meta.url));
const OWNER = 'shared/ui/Menu.tsx';
const IMPORT = /from\s+['"]@radix-ui\/react-dropdown-menu['"]|import\(\s*['"]@radix-ui\/react-dropdown-menu['"]\s*\)/;

function files(dir) {
  return readdirSync(dir).flatMap(name => {
    const path = join(dir, name);
    if (statSync(path).isDirectory()) return files(path);
    return /\.(tsx?|mjs)$/.test(name) ? [path] : [];
  });
}

const read = path => ({ path: relative(SRC, path).split(sep).join('/'), text: readFileSync(path, 'utf8') });

/** Файлы, которые берут пакет меню сами. */
export function menuLayerOwners(sources) {
  return sources.filter(({ text }) => IMPORT.test(text)).map(({ path }) => path);
}

/**
 * Слои поповеров и списка `Select` берут поверхность одной константой `LAYER_SURFACE` (2а.3): фона,
 * рамки и тени своих у них нет. Иначе тёмная тема снова разведёт их с меню, как до этого change.
 *
 * Имя пакета в файле берётся из его импорта (`import * as P from '@radix-ui/react-popover'`), а не
 * угадывается: под другим именем слой иначе прошёл бы мимо. Классы читаются только из открывающего
 * тега `Content` — без этого проверка уходила бы к `className` следующего элемента.
 */
const LAYER_PACKAGES = /import\s+\*\s+as\s+(\w+)\s+from\s+['"]@radix-ui\/react-(?:popover|select)['"]/g;
const SHARED = '${LAYER_SURFACE}';
const OWN_LOOK = /(?:^|[\s`"])(?:bg-surface[\w-]*|border(?:-[\w-]+)?|shadow-[\w-]+)(?=[\s`"]|$)/;
const CLASS_ATTR = /\bclassName=(\{`[^`]*`\}|"[^"]*")/;

/** Открывающий тег с позиции `<` до его `>`: `>` внутри фигурных скобок и строк тег не кончает. */
function openingTag(text, from) {
  let depth = 0;
  let quote = null;
  for (let i = from; i < text.length; i++) {
    const c = text[i];
    if (quote) { if (c === quote) quote = null; continue; }
    if (c === '"' || c === "'" || c === '`') quote = c;
    else if (c === '{') depth++;
    else if (c === '}') depth--;
    else if (c === '>' && depth === 0) return text.slice(from, i + 1);
  }
  return text.slice(from);
}

/** Слои без общей поверхности или со своим видом: `файл: классы` (`файл: <без className>`). */
export function strayLayers(sources) {
  return sources.flatMap(({ path, text }) => [...text.matchAll(LAYER_PACKAGES)].flatMap(([, alias]) =>
    [...text.matchAll(new RegExp(String.raw`<${alias}\.Content\b`, 'g'))]
      .map(m => openingTag(text, m.index).match(CLASS_ATTR)?.[1] ?? '<без className>')
      .filter(cls => !cls.includes(SHARED) || OWN_LOOK.test(cls.replace(SHARED, '')))
      .map(cls => `${path}: ${cls}`)));
}

const POPOVER = "import * as Popover from '@radix-ui/react-popover';\n";

describe('слой меню — только у общего Menu', () => {
  it('импорт @radix-ui/react-dropdown-menu — только в shared/ui/Menu.tsx', () => {
    expect(menuLayerOwners(files(SRC).map(read))).toEqual([OWNER]);
  });

  it('поповеры и список Select — общей поверхностью слоя, без своего фона, рамки и тени', () => {
    expect(strayLayers(files(SRC).filter(path => !/\.test\./.test(path)).map(read))).toEqual([]);
  });

  it('самопроверка слоя: своя рамка или тень краснят, общая константа — нет', () => {
    expect(strayLayers([
      { path: 'a.tsx', text: POPOVER + '<Popover.Content className="z-50 rounded-md border border-outline-variant bg-surface p-3">' },
      { path: 'b.tsx', text: POPOVER + '<Popover.Content align="end"\n className={`z-50 rounded-md p-3 ${LAYER_SURFACE}`}>' },
      { path: 'c.tsx', text: "import * as RS from '@radix-ui/react-select';\n<RS.Content className={`z-50 shadow-elevation-3 ${LAYER_SURFACE}`}>" },
    ])).toEqual([
      'a.tsx: "z-50 rounded-md border border-outline-variant bg-surface p-3"',
      'c.tsx: {`z-50 shadow-elevation-3 ${LAYER_SURFACE}`}',
    ]);
  });

  it('самопроверка слоя: имя — из импорта, классы — только своего тега', () => {
    expect(strayLayers([
      // Пакет под другим именем.
      { path: 'a.tsx', text: "import * as PP from '@radix-ui/react-popover';\n<PP.Content className=\"border bg-surface\">" },
      // Своих классов у слоя нет — общая поверхность следующего элемента его не прикрывает.
      { path: 'b.tsx', text: POPOVER + '<Popover.Content side={x > 1 ? "top" : "bottom"}>\n<div className={`p-3 ${LAYER_SURFACE}`} />' },
      // Классы слоя в порядке; рамка следующего элемента — не его.
      { path: 'c.tsx', text: POPOVER + '<Popover.Content className={`p-3 ${LAYER_SURFACE}`}>\n<div className="border-b" />' },
    ])).toEqual([
      'a.tsx: "border bg-surface"',
      'b.tsx: <без className>',
    ]);
  });

  it('самопроверка: ловит импорт под другим именем и динамический', () => {
    expect(menuLayerOwners([
      { path: 'a.tsx', text: "import * as DM from '@radix-ui/react-dropdown-menu';" },
      { path: 'b.tsx', text: 'import { Content } from "@radix-ui/react-dropdown-menu"' },
      { path: 'c.tsx', text: "const m = await import('@radix-ui/react-dropdown-menu');" },
      { path: 'd.tsx', text: "import * as Popover from '@radix-ui/react-popover';" },
    ])).toEqual(['a.tsx', 'b.tsx', 'c.tsx']);
  });
});
