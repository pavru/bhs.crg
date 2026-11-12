import { describe, expect, it } from 'vitest';
import { RECORD_GROUPS } from './paletteRecords';
import { RECORD_ROUTES } from './recordRoutes';

/**
 * Перепись «Недавних» (shell-08, задача 4.4; ТЗ UI-35): каждый экран записи из `RECORD_ROUTES` отмечает
 * открытие (`useRecentTouch`), и отмечает только тип, у которого экран есть. Забытая отметка — запись,
 * которую человек открывает каждый день, а «Недавние» о ней молчат.
 */
const sources = import.meta.glob<string>(['/src/**/*.{ts,tsx}', '!/src/**/*.test.*'], { query: '?raw', import: 'default' });

// Исходники читаются ОДИН раз на файл: их сотни, и под полным прогоном каждое чтение заново не укладывалось
// в предел теста.
let touched: Promise<Set<string>> | undefined;
function touchedTypes(): Promise<Set<string>> {
  return touched ??= (async () => {
    const found = new Set<string>();
    const texts = await Promise.all(Object.values(sources).map(load => load()));
    for (const text of texts) for (const m of text.matchAll(/useRecentTouch\('([^']+)'/g)) found.add(m[1]);
    return found;
  })();
}

/** Чтение всех исходников под полным прогоном — секунды, а не миллисекунды. */
const SCAN_TIMEOUT = 20_000;

describe('«Недавние»: отметка открытия', () => {
  it('каждый тип из RECORD_ROUTES отмечается экраном записи', async () => {
    const marked = await touchedTypes();
    for (const type of Object.keys(RECORD_ROUTES)) expect(marked.has(type), type).toBe(true);
  }, SCAN_TIMEOUT);

  // Обратная сторона: отметка типа, которого Главная не рисует, копилась бы на сервере и вытесняла бы
  // показываемые записи, а сама в блоке не появилась бы никогда.
  it('каждый отмечаемый тип Главная умеет показать: экран и группа есть', async () => {
    for (const type of await touchedTypes()) {
      expect(Object.hasOwn(RECORD_ROUTES, type), `${type}: нет в RECORD_ROUTES`).toBe(true);
      expect(RECORD_GROUPS.some(g => g.recordType === type), `${type}: нет группы в RECORD_GROUPS`).toBe(true);
    }
  }, SCAN_TIMEOUT);
});
