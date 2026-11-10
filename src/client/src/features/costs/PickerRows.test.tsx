// @vitest-environment jsdom
import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import type { NomenclatureCandidates } from '@/shared/api/nomenclatureCandidates';
import { CandidatesBlock, CandidatesNotices, CandidatesStack, CandidatesTail, type CandidatesAnswer } from './PickerRows';
import { candidatesView } from './candidatesView';

/*
 * Части блока подбора по месту ([COST-7], D6 costs-picker-sheet-reflow): каждое состояние подбора по-прежнему
 * сказано своими словами (issue #1518), а между строками кандидатов и справочника не стоит ничего, что
 * переносится по ширине, — иначе лист, сузив колонку, сдвигал бы строки справочника из-под мыши.
 */

afterEach(cleanup);

const answered = (data: Partial<NomenclatureCandidates>): CandidatesAnswer => ({
  data: { items: [], more: false, inArchive: 0, unreadable: 0, recalledUnusable: null, ...data } as NomenclatureCandidates,
  isPending: false, isError: false, isSuccess: true, error: null, refetch: async () => ({}) as never,
});
const pending: CandidatesAnswer = { data: undefined, isPending: true, isError: false, isSuccess: false, error: null, refetch: async () => ({}) as never };
const failed: CandidatesAnswer = {
  data: undefined, isPending: false, isError: true, isSuccess: false, error: new Error('сбой'), refetch: async () => ({}) as never,
};

/** Три части в том порядке, в каком их ставит окно: полоса, колонка, под списками. */
function Parts({ answer, rows = 0, more = false, asked = true }: { answer: CandidatesAnswer; rows?: number; more?: boolean; asked?: boolean }) {
  const view = candidatesView({ asked, answer, rows, more, current: { name: 'Кабель ВВГ', archived: true } });
  return (
    <>
      <section data-testid="band"><CandidatesNotices view={view} answer={answer} /></section>
      <section data-testid="column">
        <CandidatesBlock view={view} headingId="h">
          {rows > 0 && <div role="listbox" aria-labelledby="h"><div role="option" aria-selected={false}>строка</div></div>}
        </CandidatesBlock>
      </section>
      <section data-testid="tail"><CandidatesTail view={view} /></section>
    </>
  );
}

const textOf = (id: string) => screen.getByTestId(id).textContent ?? '';

describe('блок подбора разложен по месту ([COST-7])', () => {
  it('ждём: в колонке заголовок и «подбираем», под списками ничего', () => {
    render(<Parts answer={pending} />);
    expect(textOf('column')).toContain('Подходящие для этой строки');
    expect(textOf('column')).toContain('Подбираем по словам строки…');
    expect(textOf('tail')).toBe('');
    expect(textOf('band')).toContain('Сейчас:');
  });

  it('отказ подбора — в полосе с «Повторить», колонка пуста: заголовок без строк читался бы «не нашлось»', () => {
    render(<Parts answer={failed} />);
    expect(textOf('band')).toMatch(/Подбор не сработал: сбой/);
    expect(screen.getByRole('button', { name: 'Повторить' })).toBeTruthy();
    expect(textOf('column')).toBe('');
    expect(textOf('tail')).toBe('');
  });

  it('отказ, но строки есть (соседние строки счёта, прежний ответ): строки видны под заголовком — выделение ведёт к видимым', () => {
    render(<Parts answer={failed} rows={1} />);
    expect(textOf('band')).toMatch(/Подбор не сработал/);
    expect(textOf('column')).toContain('Подходящие для этой строки');
    expect(screen.getByRole('listbox', { name: 'Подходящие для этой строки' })).toBeTruthy();
  });

  it('перезапрос отказал, прежний ответ остался: оговорки о его числах держатся', () => {
    const stale: CandidatesAnswer = { ...answered({ inArchive: 2, unreadable: 1 }), isError: true, isSuccess: false, error: new Error('сбой') };
    render(<Parts answer={stale} rows={1} more />);
    for (const words of ['Есть ещё подходящие', 'Похожих в архиве: 2', 'Не сверено позиций: 1']) expect(textOf('tail')).toContain(words);
    expect(textOf('column')).not.toContain('не нашлось');
  });

  it('ничего не нашлось: в колонке одна строка, пояснение — под списками', () => {
    render(<Parts answer={answered({})} />);
    expect(textOf('column')).toContain('По словам строки подходящих не нашлось.');
    expect(textOf('column')).not.toContain('Это не значит');
    expect(textOf('tail')).toContain('Это не значит, что позиции в справочнике нет');
  });

  it('есть строки: оговорки о неполноте, архиве и несверенных — под списками, не между ними', () => {
    render(<Parts answer={answered({ inArchive: 2, unreadable: 3 })} rows={1} more />);
    expect(screen.getByRole('listbox')).toBeTruthy();
    const column = textOf('column');
    for (const words of ['Есть ещё подходящие', 'Похожих в архиве: 2', 'Не сверено позиций: 3']) {
      expect(column).not.toContain(words);
      expect(textOf('tail')).toContain(words);
    }
  });

  it('есть строки, оговорок нет — под списками пусто', () => {
    render(<Parts answer={answered({})} rows={2} />);
    expect(textOf('tail')).toBe('');
    expect(textOf('column')).not.toContain('не нашлось');
  });

  it('спросить не о чем — сказано в полосе, колонка и хвост пусты', () => {
    render(<Parts answer={answered({})} asked={false} />);
    expect(textOf('band')).toContain('В строке нет наименования');
    expect(textOf('column')).toBe('');
    expect(textOf('tail')).toBe('');
  });

  it('запомненная позиция в архиве — абзацем в полосе', () => {
    render(<Parts answer={answered({ recalledUnusable: { id: 'x', name: 'Кабель старый', issue: 'archived' } })} rows={1} />);
    expect(textOf('band')).toContain('запомнена позиция «Кабель старый» — она в архиве');
    expect(textOf('column')).not.toContain('запомнена');
  });
});

describe('окно без листа — CandidatesStack', () => {
  it('три части подряд: отказ с «Повторить», строки, оговорки — в одном блоке', () => {
    const stale: CandidatesAnswer = { ...answered({ inArchive: 1 }), isError: true, isSuccess: false, error: new Error('сбой') };
    render(
      <CandidatesStack view={candidatesView({ asked: true, answer: stale, rows: 1, more: false, current: null })} answer={stale} headingId="h">
        <div role="listbox" aria-labelledby="h"><div role="option" aria-selected={false}>строка</div></div>
      </CandidatesStack>,
    );
    expect(screen.getByRole('button', { name: 'Повторить' })).toBeTruthy();
    expect(screen.getByRole('listbox', { name: 'Подходящие для этой строки' })).toBeTruthy();
    expect(screen.getByText(/Похожих в архиве: 1/)).toBeTruthy();
  });
});
