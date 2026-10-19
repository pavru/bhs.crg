import { describe, expect, it } from 'vitest';
import type { CardLine } from '@/shared/api/nomenclatureCard';
import { cardMeta, chosenText, lineValue, onlyName } from './nomenclatureCardView';

const line = (patch: Partial<CardLine>): CardLine => ({
  key: 'k', title: 'Поле', text: 'значение', kind: 'text', identity: false, inheritedFrom: null,
  unreadable: false, archived: false, ...patch,
});

describe('значение поля в карточке (#1517)', () => {
  it('«не заполнено», «не прочитано» и «не показывается» — разные слова', () => {
    expect(lineValue(line({ text: null }))).toEqual({ text: 'не заполнено', tone: 'empty' });
    expect(lineValue(line({ text: null, unreadable: true }))).toEqual({ text: 'не прочитано', tone: 'unreadable' });
    expect(lineValue(line({ text: null, kind: 'hidden' }))).toEqual({ text: 'заполнено, не показывается', tone: 'quiet' });
  });

  it('ссылка на архивную — с пометкой', () => {
    expect(lineValue(line({ text: 'Коробка', kind: 'reference', archived: true })).text).toContain('архив');
  });

  it('дата и число — письмом пользователя, нечитаемое число — как есть', () => {
    expect(lineValue(line({ text: '2026-09-29', kind: 'date' })).text).toBe('29.09.2026');
    expect(lineValue(line({ text: '2.5', kind: 'number' })).text).toBe('2,5');
    expect(lineValue(line({ text: 'около 3', kind: 'number' })).text).toBe('около 3');
  });
});

describe('сводка карточки', () => {
  it('пустые части не выводятся', () => {
    expect(cardMeta({ emptyCount: 2, notShown: ['Фото', 'Сертификаты (2)'] }))
      .toBe('Не заполнено полей: 2 · Не показаны: Фото, Сертификаты (2)');
    expect(cardMeta({ emptyCount: 0, notShown: [] })).toBe('');
  });

  it('выбиралась — числом с датой; ни разу — словами', () => {
    expect(chosenText({ times: 14, lastOn: '2026-10-02' })).toBe('Выбиралась: 14 раз, последний — 02.10.2026');
    expect(chosenText({ times: 2, lastOn: null })).toBe('Выбиралась: 2 раза');
    expect(chosenText({ times: 0, lastOn: null })).toBe('Ещё не выбиралась');
  });

  it('заполнено только имя — сказано', () => {
    const name = line({ identity: true });
    expect(onlyName({ lines: [name, line({ text: null, identity: true })], notShown: [], aliases: [] })).toBe(true);
    expect(onlyName({ lines: [name, line({})], notShown: [], aliases: [] })).toBe(false);
    expect(onlyName({ lines: [name], notShown: ['Фото'], aliases: [] })).toBe(false);
  });
});
