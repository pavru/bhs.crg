import { describe, expect, it } from 'vitest';
import { Block, CheckCircle, Draft, Help, Payments } from '@/shared/ui/icons';
import { BY_FILE_HINT, headlineByFile, rowState } from './invoiceRowState';

/**
 * Состояние счёта в строке ленты ([COST-6.3], макет А). Чем ломается: оплаченный не отличить от разобранного,
 * оплаченный черновик выглядит черновиком, отклонённый — оплаченным, новое состояние сервера — черновиком.
 */

const item = (state: string, payment = 'Не оплачен', number: string | null = '17', scanFileName: string | null = null) =>
  ({ state, payment, number, scanFileName });

describe('rowState', () => {
  it('черновик — тихий значок черновика', () => {
    expect(rowState(item('Черновик'))).toEqual({ icon: Draft, word: 'черновик', hint: 'Черновик — ещё не разобран' });
  });

  // Отклонить и оплатить можно и черновик без номера (ревью PR #1860): подпись — имя файла при любом состоянии.
  it.each([
    ['Черновик', 'Не оплачен', 'Черновик — ещё не разобран'],
    ['Отклонён', 'Не оплачен', 'Отклонён — по счёту не платим'],
    ['Черновик', 'Оплачен', 'Оплачен, но не разобран — черновик'],
    ['Сверен', 'Не оплачен', 'Состояние «Сверен»'],
  ])('%s, %s, без номера, со сканом: подсказка объясняет, что подпись — имя файла', (state, payment, hint) => {
    expect(rowState(item(state, payment, null, 'скан.pdf')).hint).toBe(`${hint}
${BY_FILE_HINT}`);
  });

  it('номер есть — о файле подсказка молчит, даже если скан приложен', () => {
    expect(rowState(item('Отклонён', 'Не оплачен', '17', 'скан.pdf')).hint).toBe('Отклонён — по счёту не платим');
    expect(headlineByFile({ number: '17', scanFileName: 'скан.pdf' })).toBe(false);
    expect(headlineByFile({ number: null, scanFileName: null })).toBe(false);
  });

  it('разобранный неоплаченный — check_circle цвета primary', () => {
    expect(rowState(item('Разобран'))).toMatchObject({ icon: CheckCircle, tone: 'text-primary', word: 'разобран' });
  });

  it('оплаченный — payments цвета success', () => {
    expect(rowState(item('Разобран', 'Оплачен'))).toEqual({ icon: Payments, tone: 'text-success', word: 'оплачен', hint: 'Оплачен' });
  });

  it('оплаченный черновик — значок «оплачен», а слово называет и черновик', () => {
    expect(rowState(item('Черновик', 'Оплачен'))).toMatchObject({
      icon: Payments, tone: 'text-success', word: 'оплачен, черновик', hint: 'Оплачен, но не разобран — черновик',
    });
  });

  it('отклонённый — тихий block, старше оплаты', () => {
    expect(rowState(item('Отклонён'))).toEqual({ icon: Block, word: 'отклонён', hint: 'Отклонён — по счёту не платим' });
    expect(rowState(item('Отклонён', 'Оплачен')).icon).toBe(Block);
  });

  it('незнакомое состояние — не черновик: вопрос и слово сервера', () => {
    expect(rowState(item('Сверен'))).toEqual({ icon: Help, word: 'сверен', hint: 'Состояние «Сверен»' });
  });
});
