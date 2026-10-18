import { describe, expect, it } from 'vitest';
import type { InvoiceImage } from '@/shared/api/invoiceImage';
import type { PrintProfile } from '@/shared/api/printProfiles';
import {
  AUTHOR_LAYOUT_NOTE, canChooseProfile, initialChoice, layoutChipLabel, layoutHint, layoutNotes, layoutNotice,
  rebuiltMessage,
} from './invoiceImageLayout';

/** Раскладка вида у счёта (issue #1456): что сказать о профиле печати и когда давать выбор. */

const built = (over: Partial<InvoiceImage> = {}): InvoiceImage => ({
  state: 'built', pages: 1, notes: [], converter: null, builtAt: null, reason: null, aboutFile: false,
  layout: 'profile', profile: { id: 'sys', name: 'по умолчанию' }, profileState: 'current', ...over,
});

const profile = (id: string, isSystem = false): PrintProfile => ({
  id, name: id, paper: 'A4', orientation: 'portrait', fit: 'width', margins: { top: 5, bottom: 5, left: 5, right: 5 },
  isSystem, isFactory: true, factory: null,
});
const TWO = [profile('sys', true), profile('wide')];

describe('раскладка вида для чтения', () => {
  it('подсказка называет профиль или раскладку файла; не книге — ничего', () => {
    expect(layoutHint(built())).toBe('Раскладка — профиль печати «по умолчанию».');
    expect(layoutHint(built({ layout: 'file', profile: null, profileState: null })))
      .toBe('Раскладка — из самого файла: профиль печати к нему не применяется.');
    expect(layoutHint(built({ layout: null, profile: null, profileState: null }))).toBe('');
  });

  it('строки «изменён / удалён / до профилей» — только у раскладки по профилю', () => {
    expect(layoutNotice(built())).toBeNull();
    expect(layoutNotice(built({ profileState: 'changed' }))).toContain('«по умолчанию» изменён после построения вида');
    expect(layoutNotice(built({ profile: null, profileState: 'deleted' }))).toContain('построит по профилю по умолчанию');
    expect(layoutNotice(built({ profile: null, profileState: 'none' }))).toContain('Вид построен до профилей печати');
    // «До профилей» у не-книги сервер не присылает; если пришлёт — строки всё равно нет.
    expect(layoutNotice(built({ layout: null, profile: null, profileState: 'none' }))).toBeNull();
    expect(layoutNotice(built({ layout: 'file', profile: null, profileState: 'changed' }))).toBeNull();
  });

  it('выбор — при двух профилях и книге без своей раскладки, в том числе у вида до профилей', () => {
    expect(canChooseProfile(built(), TWO)).toBe(true);
    expect(canChooseProfile(built({ profile: null, profileState: 'none' }), TWO)).toBe(true);
    expect(canChooseProfile(built(), [profile('sys', true)])).toBe(false);
    expect(canChooseProfile(built({ layout: 'file', profile: null, profileState: null }), TWO)).toBe(false);
    expect(canChooseProfile(built({ layout: null, profile: null, profileState: null }), TWO)).toBe(false);
  });

  it('сначала отмечен профиль показанного вида, иначе по умолчанию', () => {
    expect(initialChoice(built({ profile: { id: 'wide', name: 'wide' } }), TWO)).toBe('wide');
    expect(initialChoice(built({ profile: null, profileState: 'deleted' }), TWO)).toBe('sys');
  });

  it('подпись чипа называет состояние, когда имени нет', () => {
    expect(layoutChipLabel(built())).toBe('Раскладка: по умолчанию');
    expect(layoutChipLabel(built({ profile: null, profileState: 'deleted' }))).toBe('Раскладка: профиль удалён');
    expect(layoutChipLabel(built({ profile: null, profileState: 'none' }))).toBe('Раскладка: до профилей');
    expect(layoutChipLabel(built({ profile: null, profileState: null }))).toBe('Раскладка: профиль не применён');
  });

  it('потерянный текст у книги со своей раскладкой объясняется; у раскладки по профилю — нет', () => {
    const lost = 'Часть текста не попала на страницу: из слов в ячейках найдено 80 %.';
    expect(layoutNotes(built({ layout: 'file', profile: null, profileState: null, notes: [lost, 'страниц: 2'] })))
      .toEqual([`${lost} ${AUTHOR_LAYOUT_NOTE}`, 'страниц: 2']);
    expect(layoutNotes(built({ notes: [lost] }))).toEqual([lost]);
  });

  it('тост называет профиль только у раскладки по профилю', () => {
    expect(rebuiltMessage(built())).toBe('Вид для чтения перестроен по профилю «по умолчанию».');
    expect(rebuiltMessage(built({ layout: 'file', profile: null, profileState: null }))).toBe('Вид для чтения перестроен.');
  });
});
