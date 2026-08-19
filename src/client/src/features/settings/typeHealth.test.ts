import { describe, it, expect } from 'vitest';
import { typeHealth, healthBadgeLabel } from './typeHealth';
import type { SchemaField } from '@/shared/api/schema';

const field = (over: Partial<SchemaField> = {}): SchemaField => ({
  key: 'Ключ', title: 'Название', type: 'string', ...over,
} as SchemaField);

describe('typeHealth — блокирующие проблемы', () => {
  it('здоровый тип не даёт ни одной', () => {
    const h = typeHealth([field()], [], [], {});
    expect(h.blocking).toEqual([]);
    expect(h.soft).toEqual([]);
  });

  it('ключ, уже занятый предком, — именно из-за него схема не сохраняется', () => {
    // живой случай #794: поле «Подписи» есть и у типа, и у предка «Проект»
    const h = typeHealth([field({ key: 'Подписи', title: 'Подписи' })], ['Подписи', 'Дата'], [], {});
    expect(h.blocking.map(b => b.kind)).toEqual(['key-conflict']);
    expect(h.blocking[0].text).toContain('Подписи');
  });

  it('пустой ключ не тянет за собой остальные жалобы на то же поле', () => {
    const h = typeHealth([field({ key: '  ', title: '' })], [], [], {});
    expect(h.blocking.map(b => b.kind)).toEqual(['empty-key']);
  });

  it('недопустимые символы в ключе и пустое название', () => {
    const h = typeHealth([field({ key: 'Ключ-с-дефисом', title: ' ' })], [], [], {});
    expect(h.blocking.map(b => b.kind).sort()).toEqual(['bad-key', 'empty-title']);
  });

  it('поле составного типа без выбранного типа', () => {
    const h = typeHealth([field({ type: 'array' })], [], [], {});
    expect(h.blocking.map(b => b.kind)).toEqual(['missing-type']);
  });

  it('дубль ключа внутри типа считается один раз, а не по разу на копию', () => {
    const h = typeHealth([field({ key: 'A' }), field({ key: 'A' }), field({ key: 'A' })], [], [], {});
    expect(h.blocking.filter(b => b.kind === 'duplicate-key')).toHaveLength(1);
  });

  it('дублирующееся имя Typst-функции', () => {
    const h = typeHealth([field()], [], [], {}, [
      { fnName: 'full', body: '' }, { fnName: 'full', body: '' },
    ] as never);
    expect(h.blocking.map(b => b.kind)).toEqual(['duplicate-fn']);
  });
});

describe('typeHealth — мягкие проблемы', () => {
  it('ссылка на удалённое поле в группировке', () => {
    const h = typeHealth([field({ key: 'A' })], [], [], {
      groups: [{ key: 'g1', title: 'Шапка', fieldKeys: ['A', 'Пропало'] }],
    });
    expect(h.blocking).toEqual([]);
    expect(h.soft.map(s => s.text)).toEqual(['Ссылка на несуществующее поле «Пропало»']);
  });

  it('исключение унаследованного поля висячим не считается', () => {
    // ключ живёт у предка ДО исключений — в этом и смысл исключения
    const h = typeHealth([field({ key: 'A' })], ['Родительское'], ['Родительское'], {
      excludedFields: ['Родительское'],
    });
    expect(h.soft).toEqual([]);
  });
});

describe('healthBadgeLabel', () => {
  it('склоняет счётчики и молчит, когда проблем нет', () => {
    expect(healthBadgeLabel({ blocking: [], soft: [] })).toEqual({ blocking: undefined, soft: undefined });
    const one = healthBadgeLabel({ blocking: [{ kind: 'key-conflict', text: '' }], soft: [] });
    expect(one.blocking).toBe('не сохранится — 1 ошибка');
    const five = healthBadgeLabel({
      blocking: Array.from({ length: 5 }, () => ({ kind: 'key-conflict' as const, text: '' })), soft: [],
    });
    expect(five.blocking).toBe('не сохранится — 5 ошибок');
    const two = healthBadgeLabel({ blocking: [], soft: [
      { kind: 'dangling-ref', text: '' }, { kind: 'dangling-ref', text: '' },
    ] });
    expect(two.soft).toBe('2 висячие ссылки');
  });
});
