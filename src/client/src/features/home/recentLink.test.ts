import { describe, expect, it } from 'vitest';
import type { AccessInfo } from '@/shared/api/access';
import type { RecentRecord } from '@/shared/api/recentRecords';
import { recentTarget } from './recentLink';

const access = (permissions: string[]): AccessInfo => ({ permissions, modules: [] });
const all = access(['core.constructions.read', 'id.document.read', 'id.config.edit']);
const record = (recordType: string, extra: Partial<RecentRecord> = {}): RecentRecord => ({
  recordType, group: 'g', groupTitle: 'g', module: 'id', id: 'v-3', title: 'Запись', openedAt: '2026-11-13T10:00:00Z', ...extra,
});

/**
 * Куда ведёт строка «Недавних» (shell-09, Р-4; [UI-35.1]): комплект и шаблон открываются под другой записью,
 * и адрес им строит карточка (`opens`). Без неё и без права экрана ссылки нет — ни на чужую страницу, ни в отказ.
 */
describe('«Недавние»: комплект и шаблон', () => {
  it('комплект открывается под своей стройкой', () => {
    const set = record('КомплектДокументов', { id: 'k/1', opens: { under: 'c-1' } });
    expect(recentTarget(set, all)?.to).toBe('/document-sets/c-1/sets/k%2F1');
  });

  it('шаблон открывается текущей версией под своим типом, а не отмеченной версией', () => {
    const shape = record('ШаблонДокумента', { opens: { under: 't-1', id: 'v-4' } });
    expect(recentTarget(shape, all)?.to).toBe('/templates?type=t-1&template=v-4');
    // Без текущей версии — та, о которой спросили.
    expect(recentTarget(record('ШаблонДокумента', { opens: { under: 't-1' } }), all)?.to)
      .toBe('/templates?type=t-1&template=v-3');
  });

  it('без «открыть под» ссылки нет: адрес без стройки вёл бы на чужую страницу', () => {
    expect(recentTarget(record('КомплектДокументов'), all)).toBeNull();
    expect(recentTarget(record('ШаблонДокумента', { opens: null }), all)).toBeNull();
  });

  it('без права экрана ссылки нет: комплект — без «Строек», шаблон — без «Шаблонов»', () => {
    const set = record('КомплектДокументов', { opens: { under: 'c-1' } });
    const shape = record('ШаблонДокумента', { opens: { under: 't-1', id: 'v-4' } });
    expect(recentTarget(set, access(['id.document.read', 'id.config.edit']))).toBeNull();
    expect(recentTarget(shape, access(['id.document.read', 'core.constructions.read']))).toBeNull();
  });
});
