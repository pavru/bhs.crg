import { describe, expect, it } from 'vitest';
import { catalogRefIds } from './archivedRefIds';

const ref = (entryId: string) => ({ $ref: 'catalog', entryId, displayName: entryId });

describe('catalogRefIds', () => {
  it('находит ссылки на любой глубине: поле, строка таблицы, вариант union, вложенное составное', () => {
    const data = {
      Подрядчик: ref('b'),
      Подписанты: [ref('a'), { Организация: ref('c') }, { ФИО: 'Иванов', Адрес: { Город: ref('d') } }],
    };
    expect(catalogRefIds(data)).toEqual(['a', 'b', 'c', 'd']);
  });

  it('ссылки на документы и на поля документов не берёт: архива у них нет', () => {
    const data = {
      Акт: { $ref: 'instance', instanceId: 'x', displayName: 'АОСР' },
      Поле: { $ref: 'document', instanceId: 'y', fieldKey: 'Подрядчик', displayName: 'АОСР → Подрядчик' },
    };
    expect(catalogRefIds(data)).toEqual([]);
  });

  it('одну запись называет один раз и в устойчивом порядке — список едет в ключ запроса', () => {
    expect(catalogRefIds([ref('b'), ref('a'), ref('b')])).toEqual(['a', 'b']);
    expect(catalogRefIds([ref('a'), ref('b')])).toEqual(catalogRefIds([ref('b'), ref('a')]));
  });

  it('пустое и не-объекты — пусто, а не падение', () => {
    expect(catalogRefIds(null)).toEqual([]);
    expect(catalogRefIds('строка')).toEqual([]);
    expect(catalogRefIds({ $ref: 'catalog' })).toEqual([]);
  });
});
