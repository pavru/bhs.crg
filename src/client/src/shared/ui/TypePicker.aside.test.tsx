// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { TypePicker, type PickType } from './TypePicker';

/**
 * Пункты «в стороне» пикера (issue #1219): комплекты неизбранных строек в переносе и копировании.
 * Без запроса их нет и есть выход; поиск находит их отдельным блоком; выход раскрывает список.
 */

afterEach(cleanup);

const TYPES: PickType[] = [
  { id: '1', name: 'Комплект КЖ', code: 'Раздел 1', section: 'ЖК «Север»' },
  { id: '2', name: 'Комплект ЭМ', code: 'Раздел 2', section: 'ТЦ «Меридиан»', aside: true },
];
const ASIDE = { label: 'Остальные стройки', reveal: 'Показать комплекты всех строек (ещё 1 стройка)' };

function open() {
  render(<TypePicker open onOpenChange={() => {}} types={TYPES} onSelect={vi.fn()} aside={ASIDE} />);
}

describe('TypePicker: пункты в стороне (#1219)', () => {
  it('без запроса пункта в стороне нет, выход внизу', () => {
    open();
    expect(screen.queryByText('Комплект ЭМ')).toBeNull();
    expect(screen.getByText('Комплект КЖ')).toBeTruthy();
    expect(screen.getByRole('button', { name: ASIDE.reveal })).toBeTruthy();
  });

  it('поиск находит пункт в стороне под своим заголовком, а не «ничего не найдено»', () => {
    open();
    fireEvent.change(screen.getByLabelText('Поиск типа'), { target: { value: 'ЭМ' } });
    expect(screen.getByText('Комплект ЭМ')).toBeTruthy();
    expect(screen.getByText('Остальные стройки')).toBeTruthy();
    expect(screen.queryByText('Ничего не найдено')).toBeNull();
  });

  it('выход раскрывает список и исчезает', () => {
    open();
    fireEvent.click(screen.getByRole('button', { name: ASIDE.reveal }));
    expect(screen.getByText('Комплект ЭМ')).toBeTruthy();
    expect(screen.queryByRole('button', { name: ASIDE.reveal })).toBeNull();
  });
});
