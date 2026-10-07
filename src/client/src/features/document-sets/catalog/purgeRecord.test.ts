import { describe, it, expect } from 'vitest';
import { purgeOffered, type PurgeOffer } from '@/shared/api/recordPurge';
import { changedNote, consequences, countMatches, holderLabel, mismatchShown, purgedToast, typedCount } from './purgeRecord';

/** Принудительное удаление записи — подтверждение числом теряемых ссылок (issue #1187). */

const offer = (references: number): PurgeOffer => ({ allowed: true, references, untraceable: 0, holders: [] });

describe('введённое число', () => {
  it('пробелы — разделитель разрядов, а не часть числа', () => {
    expect(typedCount(' 1 043 ')).toBe(1043);
    expect(typedCount('43')).toBe(43);
  });

  it('не цифры целиком — не число', () => {
    expect(typedCount('')).toBeNull();
    expect(typedCount('43 шт')).toBeNull();
    expect(typedCount('4З')).toBeNull();
    expect(typedCount('-43')).toBeNull();
    expect(typedCount('43.0')).toBeNull();
  });

  it('кнопка доступна только при точном совпадении', () => {
    expect(countMatches('43', offer(43))).toBe(true);
    expect(countMatches('4 3', offer(43))).toBe(true);
    expect(countMatches('44', offer(43))).toBe(false);
    expect(countMatches('', offer(43))).toBe(false);
    // Пустое поле — не ноль: «0» подтверждает только ноль, а его сервер не предлагает.
    expect(countMatches('', offer(0))).toBe(false);
  });
});

describe('сообщение о несовпадении', () => {
  it('пока печатают — молчит, набрали столько же знаков — говорит', () => {
    expect(mismatchShown('1', offer(143), false)).toBe(false);
    expect(mismatchShown('14', offer(143), false)).toBe(false);
    expect(mismatchShown('144', offer(143), false)).toBe(true);
    expect(mismatchShown('143', offer(143), false)).toBe(false);
  });

  it('ушли из поля или нажали Enter — говорит и о коротком вводе', () => {
    expect(mismatchShown('14', offer(143), true)).toBe(true);
    expect(mismatchShown('', offer(143), true)).toBe(false);
    expect(mismatchShown('абв', offer(143), true)).toBe(true);
  });
});

describe('предложение из отказа', () => {
  const refusal = (purge: unknown) => ({ response: { data: { error: 'Нельзя удалить', purge } } });

  it('читается поле ответа, а не слова причины', () => {
    expect(purgeOffered(refusal({ allowed: true, references: 3, untraceable: 1, holders: [] })))
      .toEqual({ allowed: true, references: 3, untraceable: 1, holders: [] });
    expect(purgeOffered({ response: { data: { error: 'можно удалить, потеряв ссылки' } } })).toBeNull();
  });

  it('нет поля или оно пустое — выхода нет', () => {
    expect(purgeOffered(refusal(null))).toBeNull();
    expect(purgeOffered(refusal({ allowed: true }))).toBeNull();
    expect(purgeOffered(new Error('сеть'))).toBeNull();
  });

  it('без права — предложение есть, но не разрешено и без разбивки', () => {
    expect(purgeOffered(refusal({ allowed: false, references: 3 })))
      .toEqual({ allowed: false, references: 3, untraceable: 0, holders: [] });
  });
});

describe('последствия', () => {
  const of = (references: number, untraceable: number) =>
    consequences({ allowed: true, references, untraceable, holders: [] }).join(' | ');

  it('все ссылки найдутся — обещаем отбор, о ненаходимых молчим', () => {
    expect(of(40, 0)).toContain('модуль покажет их в своих отборах');
    expect(of(40, 0)).not.toContain('не покажет никто');
  });

  it('часть не найдётся — называем сколько', () => {
    expect(of(43, 3)).toContain('модуль покажет их в своих отборах');
    expect(of(43, 3)).toContain('Из них 3 не покажет никто');
  });

  it('не найдётся ни одна — отбор не обещаем', () => {
    expect(of(3, 3)).not.toContain('модуль покажет');
    expect(of(3, 3)).toContain('Потом их не покажет никто');
    expect(of(3, 3)).not.toContain('Из них');
  });

  it('о резервной копии и журнале говорится всегда', () => {
    for (const text of [of(40, 0), of(43, 3), of(3, 3)]) {
      expect(text).toContain('только из резервной копии');
      expect(text).toContain('записывается в журнал');
    }
  });
});

describe('слова', () => {
  it('строка разбивки называет владельца и что держит', () => {
    expect(holderLabel({ owner: '«Счета и накладные» (модуль выключен)', what: 'строки счетов', rows: 40, documents: null, traceable: true }))
      .toBe('«Счета и накладные» (модуль выключен) · строки счетов');
  });

  it('изменившееся число называет оба и говорит, что ничего не удалено', () => {
    const note = changedNote(43, 45);

    expect(note).toContain('было 43, стало 45');
    expect(note).toContain('Ничего не удалено');
  });

  it('успех называет запись и потерю', () => {
    expect(purgedToast('ООО Ромашка', 43)).toBe('Запись «ООО Ромашка» удалена. Потеряно ссылок: 43.');
  });
});
