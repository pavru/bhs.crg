import { describe, expect, it } from 'vitest';
import {
  fieldIssue, fromDraft, pageSize, pairIssue, profileSummary, toDraft, validateDraft, type PrintProfileDraft,
} from './printProfileRules';

/** Проверка формы «Профили печати» (issue #1455) — до отправки, теми же пределами, что у сервера. */

const A4 = toDraft({
  name: 'Альбомный A4', paper: 'A4', orientation: 'portrait', fit: 'width',
  margins: { top: 5, bottom: 5, left: 5, right: 5 },
});

const withMargins = (m: Partial<PrintProfileDraft['margins']>, rest: Partial<PrintProfileDraft> = {}) =>
  ({ ...A4, ...rest, margins: { ...A4.margins, ...m } });

describe('проверка профиля печати', () => {
  it('годный черновик — без ошибок, тело запроса числами', () => {
    expect(validateDraft(A4, [])).toEqual({});
    expect(fromDraft(withMargins({ left: ' 12 ' })).margins.left).toBe(12);
  });

  it('название обязательно, не длиннее 80 и не занято — без учёта регистра', () => {
    expect(validateDraft({ ...A4, name: '   ' }, []).name).toBe('Укажите название');
    expect(validateDraft({ ...A4, name: 'я'.repeat(81) }, []).name).toBe('Не длиннее 80 знаков');
    expect(validateDraft(A4, ['альбомный a4']).name).toBe('Профиль с таким названием уже есть');
    expect(validateDraft(A4, ['по умолчанию']).name).toBeUndefined();
  });

  it('пустое поле — ошибка, а не ноль; дробное и отрицательное — тоже', () => {
    for (const value of ['', ' ', '2.5', '2,5', '-1', 'пять']) {
      expect(validateDraft(withMargins({ top: value }), [])['margins.top']).toBe('Целое число миллиметров');
    }
    expect(validateDraft(withMargins({ top: '0' }), [])['margins.top']).toBeUndefined();
  });

  it('поля — от 0 до 40', () => {
    expect(validateDraft(withMargins({ left: '40' }), [])['margins.left']).toBeUndefined();
    expect(validateDraft(withMargins({ left: '41' }), [])['margins.left']).toBe('От 0 до 40 мм');
  });

  it('по ширине и по высоте остаётся не меньше 100 мм — ошибка пары, с осью в тексте', () => {
    // A5 книжная: 148 − 40 − 40 = 68.
    const narrow = validateDraft(withMargins({ left: '40', right: '40' }, { paper: 'A5' }), []);
    expect(pairIssue(narrow, ['left', 'right']))
      .toBe('Слишком широкие поля: по ширине остаётся 68 мм. Нужно не меньше 100 мм.');
    // Ошибка пары — не ошибка поля: под самим полем её нет.
    expect(fieldIssue(narrow, 'left')).toBeUndefined();
    // A5 альбомная: высота 148 − 25 − 25 = 98.
    const low = validateDraft(withMargins({ top: '25', bottom: '25' }, { paper: 'A5', orientation: 'landscape' }), []);
    expect(pairIssue(low, ['top', 'bottom'])).toContain('по высоте остаётся 98 мм');
    // Ровно 100 — годится: A5 книжная, 148 − 24 − 24.
    expect(validateDraft(withMargins({ left: '24', right: '24' }, { paper: 'A5' }), [])).toEqual({});
  });

  it('нехватку места не меряют по негодному полю', () => {
    const issues = validateDraft(withMargins({ left: '', right: '40' }, { paper: 'A5' }), []);
    expect(fieldIssue(issues, 'left')).toBe('Целое число миллиметров');
    expect(issues['margins.right']).toBeUndefined();
    expect(pairIssue(issues, ['left', 'right'])).toBeNull();
  });

  it('отказ сервера читается так же: один текст на обеих сторонах — ошибка пары', () => {
    const server = { 'margins.left': 'По ширине остаётся 98 мм — нужно не меньше 100.', 'margins.right': 'По ширине остаётся 98 мм — нужно не меньше 100.' };
    expect(pairIssue(server, ['left', 'right'])).toContain('98 мм');
    expect(fieldIssue({ 'margins.top': 'Поля — от 0 до 40 мм.' }, 'top')).toBe('Поля — от 0 до 40 мм.');
  });
});

describe('сводка и размер страницы', () => {
  it('одинаковые поля — одним числом, разные — верх/низ/лево/право', () => {
    expect(profileSummary(fromDraft(A4))).toBe('A4 · книжная · по ширине · поля 5 мм');
    expect(profileSummary({ ...fromDraft(A4), orientation: 'landscape', fit: 'none',
      margins: { top: 5, bottom: 5, left: 10, right: 10 } }))
      .toBe('A4 · альбомная · не вписывать · поля 5/5/10/10 мм');
  });

  it('альбомная меняет ширину и высоту местами', () => {
    expect(pageSize('A3', 'portrait')).toEqual({ width: 297, height: 420 });
    expect(pageSize('A3', 'landscape')).toEqual({ width: 420, height: 297 });
  });
});
