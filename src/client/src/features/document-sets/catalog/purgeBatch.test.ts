import { describe, expect, it } from 'vitest';
import type { PurgeBatchPreview } from '@/shared/api/recordPurge';
import {
  batchConsequences, batchFailure, batchSubtitle, carriedOver, batchTitle, batchToast, emptyText, excludedGroups, moreNote, wasNote,
} from './purgeBatch';

const record = (id: string, references: number, untraceable = 0) =>
  ({ id, name: `Запись ${id}`, archived: false, references, untraceable, holders: [] });

const preview = (over: Partial<PurgeBatchPreview> = {}): PurgeBatchPreview => ({
  records: [record('a', 2), record('b', 1)], excluded: [], references: 3, untraceable: 0,
  checked: 2, missing: 0, more: 0, limit: 200, ...over,
});

const byType = { typeId: 't', scope: 'System', scopeId: null };
const named = { ids: ['a', 'b', 'c', 'd'] };

describe('пакетная очистка: слова диалога', () => {
  it('заголовок называет число удаляемых, а пустой пакет — что удалять нечего', () => {
    expect(batchTitle(preview())).toBe('Удалить записей: 2, потеряв ссылки?');
    expect(batchTitle(preview({ records: [] }))).toBe('Удалять нечего');
  });

  it('подзаголовок выбора называет, сколько войдёт и сколько нет, — до ввода числа', () => {
    const partial = preview({
      checked: 4, missing: 1, excluded: [{ id: 'c', name: 'В', reason: 'никто не ссылается' }],
    });
    expect(batchSubtitle(named, undefined, partial)).toBe('Выбрано 4: будут удалены 2, не войдут 2');
    expect(batchSubtitle(named, undefined, preview())).toBe('Выбрано записей: 2');
    expect(batchSubtitle(byType, 'Организация', preview({ checked: 14 }))).toBe('Тип «Организация», проверено записей: 14');
  });

  it('три «пусто» читаются по-разному', () => {
    const none = preview({ records: [], references: 0 });
    expect(emptyText(byType, 'Организация', none)).toContain('нет действующих записей');
    expect(emptyText(named, undefined, { ...none, checked: 2, missing: 2 })).toContain('уже нет: 2');
    expect(emptyText(named, undefined, { ...none, checked: 2, missing: 0 })).toBe('Из выбранных не подходит ни одна.');
  });

  it('не вошедшие сгруппированы по причине', () => {
    expect(excludedGroups([
      { id: '1', name: 'А', reason: 'свободна' }, { id: '2', name: 'Б', reason: 'занята' },
      { id: '3', name: 'В', reason: 'свободна' },
    ])).toEqual([{ reason: 'свободна', names: ['А', 'В'] }, { reason: 'занята', names: ['Б'] }]);
  });

  it('последствия говорят о записях во множественном числе и о журнале по каждой', () => {
    const lines = batchConsequences(preview({ untraceable: 3 }));
    expect(lines[0]).toMatch(/^Записи будут удалены совсем/);
    expect(lines.at(-1)).toContain('по каждой записи');
    // Середина — та же, что у одной записи: все ссылки неотслеживаемые → «не покажет никто».
    expect(lines.some(l => l.includes('не покажет никто'))).toBe(true);
  });

  it('потолок пакета назван, итог называет не тронутые', () => {
    expect(moreNote(preview())).toBeNull();
    expect(moreNote(preview({ more: 37 }))).toContain('ещё записей: 37');
    expect(batchToast({ records: 6, references: 143, untraceable: 0 }, 4))
      .toBe('Удалено записей: 6. Потеряно ссылок: 143. Не тронуто: 4.');
    expect(batchToast({ records: 6, references: 143, untraceable: 0 }, 0))
      .toBe('Удалено записей: 6. Потеряно ссылок: 143.');
  });

  it('свежий список после отказа не теряет не вошедшие при первом показе', () => {
    const before = preview({ checked: 4, missing: 1, excluded: [{ id: 'c', name: 'В', reason: 'свободна' }] });
    const now = preview({ checked: 2, records: [record('a', 3)], excluded: [{ id: 'b', name: 'Б', reason: 'занята' }], references: 3 });
    const merged = carriedOver(before, now);
    expect(merged.excluded.map(e => e.id)).toEqual(['b', 'c']);
    expect([merged.checked, merged.missing]).toEqual([4, 1]);
    expect(batchSubtitle(named, undefined, merged)).toBe('Выбрано 4: будут удалены 1, не войдут 3');
  });

  it('изменившееся число у записи названо, неизменное — нет', () => {
    expect(wasNote(preview(), 'a', 5)).toBe('было 2');
    expect(wasNote(preview(), 'a', 2)).toBeNull();
    expect(wasNote(preview(), 'новая', 2)).toBeNull();
    expect(wasNote(null, 'a', 2)).toBeNull();
  });
});

describe('пакетная очистка: разбор отказа', () => {
  const refusal = (status: number, data: unknown) => ({ response: { status, data } });

  it('отказ со свежим списком — «изменилось», слова берутся у сервера', () => {
    const failure = batchFailure(refusal(409, { error: 'Пакет изменился. Ничего не удалено.', preview: preview() }));
    expect(failure).toMatchObject({ kind: 'changed', note: 'Пакет изменился. Ничего не удалено.' });
  });

  it('отказ без списка — окончательный', () => {
    expect(batchFailure(refusal(409, { error: 'Запись не удалена: внешний ключ' })))
      .toEqual({ kind: 'final', text: 'Запись не удалена: внешний ключ' });
  });

  it('нет ответа или сбой сервера — исход неизвестен, «не удалено» не говорится', () => {
    expect(batchFailure(new Error('Network Error'))).toEqual({ kind: 'unknown' });
    expect(batchFailure(refusal(502, null))).toEqual({ kind: 'unknown' });
  });
});
