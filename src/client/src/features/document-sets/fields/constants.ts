import type { CatalogScope } from '@/shared/api/types';
import type { SchemaField } from '@/shared/api/schema';

export const STATUS_LABELS: Record<string, string> = {
  Draft: 'Черновик', Generating: 'Генерация...', Generated: 'Готово', Failed: 'Ошибка',
};
export const STATUS_COLORS: Record<string, string> = {
  Draft: 'bg-muted text-fg2',
  Generating: 'bg-warning-subtle text-warning',
  Generated: 'bg-success-subtle text-success',
  Failed: 'bg-danger-subtle text-danger',
};
export const SCOPE_COLORS: Record<CatalogScope, string> = {
  Set: 'bg-success-subtle text-success',
  Section: 'bg-brand-subtle text-brand-hover',
  Construction: 'bg-warning-subtle text-warning',
  System: 'bg-muted text-fg2',
};

export function fieldInputClass(invalid = false, readOnly = false) {
  if (readOnly) {
    return 'w-full border rounded-md px-3 py-2 text-sm text-fg3 bg-muted border-stroke cursor-not-allowed';
  }
  return `w-full border rounded-md px-3 py-2 text-sm text-fg1 focus:outline-none focus-visible:ring-2 focus-visible:ring-brand bg-surface ${
    invalid ? 'border-danger focus-visible:ring-danger' : 'border-stroke-strong'
  }`;
}

export const TABLE_SHOWN_TYPES = new Set([
  'string', 'text', 'number', 'date', 'boolean', 'enum', 'primitive', 'complex',
]);

/**
 * Предлагать ли массиву табличный ввод (issue #748).
 *
 * Отдельной функцией — ради теста: условие однострочное, но его снятие возвращает возможность
 * нарушить инвариант union'а «ровно один ключ» (#320) молча, а такое обязано ломать сборку.
 *
 * @param isUnionItem элемент массива — union-тип: колонки были бы ВАРИАНТАМИ и читались как «и».
 */
export function showsArrayTable(subFields: SchemaField[], isUnionItem: boolean): boolean {
  return !isUnionItem && subFields.some(f => TABLE_SHOWN_TYPES.has(f.type));
}

export const DEFAULT_COL_WIDTHS: Partial<Record<string, number>> = {
  number: 80, date: 118, boolean: 52, enum: 130, complex: 170,
};
export function defaultColWidth(f: SchemaField) {
  return DEFAULT_COL_WIDTHS[f.type] ?? 140;
}

/**
 * Тип полезной нагрузки при перетаскивании строки массива.
 *
 * <p><b>Тип свой, не <code>text/plain</code></b> — и вот это правило проверяемое: с текстовым типом
 * ручка становится источником перетаскивания для всей страницы, и отпущенная над чужим полем ввода
 * вставляет туда номер строки (issue #518).</p>
 *
 * <p><b>А вот «без груза Firefox отменяет перетаскивание» — неверно, и раньше здесь утверждалось
 * обратное.</b> Правило шло из #517 и разошлось отсюда по пяти местам как факт. Проверено вручную
 * 2026-08-17 в живом Firefox, на кнопке-ручке (<code>&lt;button draggable&gt;</code>, то есть на
 * элементе, перетаскиваемом не «от природы» — именно в этом и состояло объяснение): перетаскивание
 * доходит до <code>drop</code> и без <code>setData</code>. Заодно снята задача #760, заведённая на
 * шесть мест, где груза нет: чинить там нечего.</p>
 *
 * <p>Груз всё же кладём — он соответствует спецификации HTML5 DnD и ничего не стоит, — но как
 * страховку, а не как обязанность перед конкретным браузером. Разница важна: ложное «иначе
 * сломается» переживает свою причину и заставляет чинить несуществующее.</p>
 *
 * <p>Автоматической проверкой это не удержать: Playwright перетаскивание синтезирует, и в контроле
 * исход с <code>setData</code> и без совпадал во всех режимах — headless и с окном, Firefox и
 * Chromium. Значит подтвердить или опровергнуть подобное правило может только живая мышь.</p>
 */
export const ROW_DRAG_MIME = 'application/x-crg-array-row';

export const CELL_INPUT =
  'w-full h-full px-1.5 bg-transparent border-none outline-none text-xs text-fg1 tabular-nums focus:bg-brand-subtle';

export function tryPrettyJson(val: unknown): string {
  try { return JSON.stringify(val, null, 2); } catch { return '{}'; }
}

export function tryParseJson(s: string): { ok: boolean; value?: Record<string, unknown>; error?: string } {
  try { return { ok: true, value: JSON.parse(s) as Record<string, unknown> }; }
  catch (e) { return { ok: false, error: String(e) }; }
}
