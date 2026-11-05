// Путь модуля по импорту — для правил храповика, которые заходят в другие модули: `raw-field`
// (ui-ratchet-inputs.mjs, класс поля из чужой константы) и `cell-field` (ui-ratchet-cells.mjs, компонент клетки).
// Одно место, чтобы оба правила разрешали один и тот же импорт одинаково (ревью PR #1776).
import { posix } from 'node:path';

/**
 * База пути модуля от `src/` по спецификатору импорта файла `from` (путь от `src/`): алиас `@/` или относительный
 * путь. null — пакет, выход за `src/` или относительный импорт без известного файла.
 */
export function importBase(spec, from) {
  const base = spec.startsWith('@/') ? spec.slice(2)
    : spec.startsWith('.') && from != null ? posix.normalize(posix.join(posix.dirname(from), spec)) : null;
  return base == null || base.startsWith('..') ? null : base;
}

const EXTS = ['.ts', '.tsx', '.js', '.jsx', '/index.ts', '/index.tsx', '/index.js'];

/**
 * Файлы-кандидаты по порядку. Спецификатор с расширением — он сам, а `.js` — сначала `.ts`/`.tsx` (так пишет
 * импорт ESM в TypeScript); без расширения — подбор, в том числе `index` каталога.
 */
export function moduleCandidates(base) {
  if (/\.js$/.test(base)) return [base.replace(/\.js$/, '.ts'), base.replace(/\.js$/, '.tsx'), base];
  if (/\.(?:tsx?|jsx|mjs)$/.test(base)) return [base];
  return EXTS.map(ext => base + ext);
}
