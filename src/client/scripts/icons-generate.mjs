#!/usr/bin/env node
// Генератор набора значков ([UI-60], sharedui-md3-03): из src/shared/ui/icons/manifest.json —
// icons.generated.ts рядом с ним, по именованному экспорту на значок с путём Material Symbols
// Outlined (вес 400) из devDependency @material-symbols/svg-400. В git лежит только используемое:
// пакет весит мегабайты, а именованные экспорты сборщик дробит, как раньше дробил lucide.
//
// Почему не реестр по строковому имени (`<Icon name="delete" />`): строку сборщик не отрежет, и в
// основной чанк ехал бы весь манифест. Почему не шрифт — design зонтика sharedui-md3, D5.
//
// Дрейф сторожит icons-generate.test.mjs тем же generate(), что и здесь.
//
//   npm run icons:generate

import { readFileSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const clientRoot = join(dirname(fileURLToPath(import.meta.url)), '..');
export const iconsDir = join(clientRoot, 'src', 'shared', 'ui', 'icons');
const packageDir = join(clientRoot, 'node_modules', '@material-symbols', 'svg-400');

/** Манифест с диска. */
export const readManifest = () => JSON.parse(readFileSync(join(iconsDir, 'manifest.json'), 'utf8'));

/** Версия пакета — в шапку файла: по ней видно, из какого набора путь. */
export const packageVersion = () => JSON.parse(readFileSync(join(packageDir, 'package.json'), 'utf8')).version;

/** Путь значка из пакета; нет файла — null (решает generate, с именем значка в отказе). */
export function readPath(name) {
  let text;
  try {
    text = readFileSync(join(packageDir, 'outlined', `${name}.svg`), 'utf8');
  } catch {
    return null;
  }
  const paths = [...text.matchAll(/<path\b[^>]*\bd="([^"]+)"/g)];
  if (paths.length !== 1) throw new Error(`@material-symbols/svg-400: в outlined/${name}.svg путей ${paths.length}, а не один`);
  return paths[0][1];
}

/** `check_circle` → `CheckCircle`, `123` → `N123`: имя экспорта обязано быть идентификатором. */
export const exportName = name => {
  const pascal = name.split(/[_-]/).map(p => p.charAt(0).toUpperCase() + p.slice(1)).join('');
  return /^\d/.test(pascal) ? `N${pascal}` : pascal;
};

/**
 * Текст icons.generated.ts. Чистая функция — её же зовёт тест дрейфа; `read(name)` отдаёт путь
 * значка или null (`name` — имя файла с суффиксом `-fill` для заливки).
 */
export function generate(manifest, read, version) {
  const icons = Object.entries(manifest.icons ?? {}).sort(([a], [b]) => a.localeCompare(b));
  const missing = icons.flatMap(([name, o]) => [name, ...(o.fill ? [`${name}-fill`] : [])]).filter(n => read(n) === null);
  if (missing.length)
    throw new Error(`manifest.json: в @material-symbols/svg-400/outlined нет ${missing.join(', ')} — имена сверяют с файлами пакета`);
  const seen = new Map();
  const lines = icons.map(([name, o]) => {
    const id = o.export ?? exportName(name);
    if (seen.has(id)) throw new Error(`manifest.json: экспорт ${id} у двух значков — ${seen.get(id)} и ${name}; один задайте полем export`);
    seen.set(id, name);
    return o.fill
      ? `export const ${id}: FilledIconDef = { d: '${read(name)}', fill: '${read(`${name}-fill`)}' };`
      : `export const ${id}: IconDef = { d: '${read(name)}' };`;
  });
  return [
    '// Сгенерировано scripts/icons-generate.mjs из src/shared/ui/icons/manifest.json — руками не править.',
    '// Правка — в manifest.json, затем: npm run icons:generate',
    `// Material Symbols Outlined, вес 400: @material-symbols/svg-400 ${version}, лицензия Apache 2.0.`,
    "import type { FilledIconDef, IconDef } from './types';",
    '',
    ...lines,
    '',
  ].join('\n');
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  writeFileSync(join(iconsDir, 'icons.generated.ts'), generate(readManifest(), readPath, packageVersion()), 'utf8');
  console.log('Набор значков записан: src/shared/ui/icons/icons.generated.ts.');
}
