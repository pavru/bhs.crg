import { describe, expect, it } from 'vitest';
import { Block, CheckCircle, Draft, Help, Payments } from '@/shared/ui/icons';
import { rowState } from './invoiceRowState';

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

  it('черновик из файла: подсказка объясняет, что подпись — имя файла', () => {
    expect(rowState(item('Черновик', 'Не оплачен', null, 'скан.pdf')).hint).toBe('Черновик — номера ещё нет, это имя файла');
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
