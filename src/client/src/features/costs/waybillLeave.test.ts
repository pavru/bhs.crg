import { describe, expect, it } from 'vitest';
import { waybillLeave, type WaybillLeaveInput } from './waybillLeave';

/** Условие охраны и вопрос формы накладной (ревью PR #1817, #1821). */

const base: WaybillLeaveInput = { dirty: false, typing: false, correcting: false, reason: '', gone: false, writing: false };

describe('waybillLeave', () => {
  it('нечего терять — охрана молчит', () => {
    expect(waybillLeave(base).guard).toBe(false);
    expect(waybillLeave({ ...base, correcting: true, reason: '   ' }).guard).toBe(false);
  });

  it('правки — охрана держит, вопрос называет их и пишет черновик', () => {
    const leave = waybillLeave({ ...base, dirty: true });
    expect(leave).toMatchObject({ guard: true, savable: true });
    expect(leave.question).toBe('Не сохранены изменения накладной. Сохранить их перед переходом?');
  });

  it('недописанная дата — тоже правка, но записать её нельзя: кнопки записи нет', () => {
    const leave = waybillLeave({ ...base, typing: true });
    expect(leave).toMatchObject({ guard: true, savable: false });
    expect(leave.question).toContain('Дата отпуска дописана не до конца');
  });

  it('набранная причина исправления — тоже правка', () => {
    expect(waybillLeave({ ...base, correcting: true, reason: 'опечатка' }).guard).toBe(true);
  });

  it('в исправлении вопрос не пишет и называет, чем оно записывается', () => {
    const leave = waybillLeave({ ...base, dirty: true, correcting: true });
    expect(leave.savable).toBe(false);
    expect(leave.question).toContain('«Записать исправление»');
  });

  it('накладную удалили — охрана молчит, что бы ни было набрано', () => {
    expect(waybillLeave({ ...base, dirty: true, typing: true, correcting: true, reason: 'опечатка', gone: true }).guard)
      .toBe(false);
  });

  it('идёт запись — вопрос говорит о ней, а не о потере; запись вопросом не гаснет', () => {
    const draft = waybillLeave({ ...base, dirty: true, writing: true });
    expect(draft.question).toMatch(/^Накладная записывается\. Переход — после записи/);
    expect(draft.savable).toBe(true);
    const correction = waybillLeave({ ...base, correcting: true, reason: 'опечатка', writing: true });
    expect(correction.question).toMatch(/^Исправление записывается\./);
    expect(correction.question).not.toContain('потеряете');
  });
});
