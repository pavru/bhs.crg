import { describe, expect, it } from 'vitest';
import type { InvoiceImage } from '@/shared/api/invoiceImage';
import type { PrintProfile } from '@/shared/api/printProfiles';
import {
  canChooseProfile, initialChoice, layoutChipHint, layoutChipParts, layoutHint, layoutNotice, layoutState, rebuiltMessage,
} from './invoiceImageLayout';

/** Раскладка вида у счёта (issue #1456): что сказать о профиле печати и когда давать выбор. */

const built = (over: Partial<InvoiceImage> = {}): InvoiceImage => ({
  state: 'built', pages: 1, notes: [], converter: null, builtAt: null, reason: null, aboutFile: false,
  layout: 'profile', profile: { id: 'sys', name: 'по умолчанию' }, profileState: 'current', layoutNote: null, ...over,
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
    const label = (image: InvoiceImage) => { const { lead, state } = layoutChipParts(image); return lead + state; };
    expect(label(built())).toBe('Раскладка: по умолчанию');
    expect(label(built({ profile: null, profileState: 'deleted' }))).toBe('Раскладка: профиль удалён');
    expect(label(built({ profile: null, profileState: 'none' }))).toBe('Раскладка: до профилей');
    expect(label(built({ profile: null, profileState: null }))).toBe('Раскладка: профиль не применён');
    // Текст «изменён» ушёл в подсказку чипа (issue #1495) — подпись называет состояние сама.
    expect(label(built({ profileState: 'changed' }))).toBe('Раскладка: по умолчанию · изменён');
  });

  it('в окне выбора состояние — без обещаний кнопки «Перестроить»: там строит отмеченный профиль', () => {
    for (const over of [{ profileState: 'changed' }, { profile: null, profileState: 'deleted' }, { profile: null, profileState: 'none' }] as const) {
      expect(layoutState(built(over))).toBeTruthy();
      expect(layoutState(built(over))).not.toContain('Перестроить');
    }
    expect(layoutState(built({ profile: null, profileState: 'deleted' }))).toBe('Профиль печати, по которому построен вид, удалён.');
    expect(layoutState(built())).toBeNull();
    expect(layoutState(built({ layout: 'file', profile: null, profileState: 'changed' }))).toBeNull();
  });

  it('в узкой колонке обрезается имя профиля, а не слово состояния — оно отдельной частью', () => {
    expect(layoutChipParts(built({ profileState: 'changed' }))).toEqual({ lead: 'Раскладка: по умолчанию', state: ' · изменён' });
    expect(layoutChipParts(built({ profile: null, profileState: 'deleted' }))).toEqual({ lead: 'Раскладка:', state: ' профиль удалён' });
    expect(layoutChipParts(built()).state).toBe('');
  });

  it('подсказка чипа — состояние, пометка о раскладке и как выбрать; пустого не пишет', () => {
    const image = built({ profileState: 'changed', layoutNote: 'Лист вписан…' });
    expect(layoutChipHint(image, layoutNotice(image)))
      .toBe(`${layoutNotice(image)}\n\nЛист вписан…\n\nВыбрать профиль — по нажатию.`);
    expect(layoutChipHint(built(), null)).toBe('Выбрать профиль — по нажатию.');
  });

  it('тост называет профиль; профиль, не применённый к книге со своей раскладкой, назван словами', () => {
    const own = built({ layout: 'file', profile: null, profileState: null });
    expect(rebuiltMessage(built(), built())).toBe('Вид для чтения перестроен по профилю «по умолчанию».');
    expect(rebuiltMessage(own, own)).toBe('Вид для чтения перестроен.');
    // Вид «до профилей» оказался книгой со своей раскладкой: выбранный профиль не применился.
    expect(rebuiltMessage(own, built({ profile: null, profileState: 'none' })))
      .toContain('по раскладке самого файла: автор книги задал её сам');
  });
});
