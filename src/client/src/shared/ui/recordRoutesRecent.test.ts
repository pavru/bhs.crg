import { describe, expect, it } from 'vitest';
import { RECORD_ROUTES } from './recordRoutes';

/**
 * Перепись «Недавних» (shell-08, задача 4.4; ТЗ UI-35): каждый экран записи из `RECORD_ROUTES` отмечает
 * открытие (`useRecentTouch`), и отмечает только тип, у которого экран есть. Забытая отметка — запись,
 * которую человек открывает каждый день, а «Недавние» о ней молчат.
 */
const sources = import.meta.glob<string>(['/src/**/*.{ts,tsx}', '!/src/**/*.test.*'], { query: '?raw', import: 'default' });

async function touchedTypes(): Promise<Set<string>> {
  const found = new Set<string>();
  for (const load of Object.values(sources))
    for (const m of (await load()).matchAll(/useRecentTouch\('([^']+)'/g)) found.add(m[1]);
  return found;
}

describe('«Недавние»: отметка открытия', () => {
  it('каждый тип из RECORD_ROUTES отмечается экраном записи', async () => {
    const touched = await touchedTypes();
    for (const type of Object.keys(RECORD_ROUTES)) expect(touched.has(type), type).toBe(true);
  });
});
