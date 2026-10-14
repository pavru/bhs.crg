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

  it('выход раскрывает список и исчезает, а фокус возвращается в поиск', () => {
    open();
    // Как с клавиатуры: Tab на кнопку, затем нажатие. jsdom щелчком фокус не переносит — без этого
    // фокус и так стоял бы в поиске, и проверка была бы зелёной на любом коде.
    const button = screen.getByRole('button', { name: ASIDE.reveal });
    button.focus();
    fireEvent.click(button);
    expect(screen.getByText('Комплект ЭМ')).toBeTruthy();
    expect(screen.queryByRole('button', { name: ASIDE.reveal })).toBeNull();
    // Кнопка исчезла — фокус не должен уйти с ней: стрелки и Enter живут в поле поиска.
    expect(document.activeElement).toBe(screen.getByLabelText('Поиск типа'));
  });

  it('пустой список и выход: Enter в поиске раскрывает', () => {
    render(<TypePicker open onOpenChange={() => {}} types={[TYPES[1]]} onSelect={vi.fn()} aside={ASIDE} />);
    fireEvent.keyDown(screen.getByLabelText('Поиск типа'), { key: 'Enter' });
    expect(screen.getByText('Комплект ЭМ')).toBeTruthy();
  });

  it('без текстов aside признак не прячет пункт: спрятать без выхода — потерять', () => {
    render(<TypePicker open onOpenChange={() => {}} types={TYPES} onSelect={vi.fn()} />);
    expect(screen.getByText('Комплект ЭМ')).toBeTruthy();
  });

  it('одноимённые стройки по обе стороны — две группы, без дубля ключа', () => {
    const errors = vi.spyOn(console, 'error').mockImplementation(() => {});
    const twins: PickType[] = [
      { id: '1', name: 'КЖ', code: '', section: 'ЖК «Север»' },
      { id: '2', name: 'ЭМ', code: '', section: 'ЖК «Север»', aside: true },
    ];
    render(<TypePicker open onOpenChange={() => {}} types={twins} onSelect={vi.fn()} aside={ASIDE} />);
    fireEvent.click(screen.getByRole('button', { name: ASIDE.reveal }));
    expect(screen.getAllByText('ЖК «Север»')).toHaveLength(2);
    expect(errors.mock.calls.filter(c => String(c[0]).includes('same key'))).toEqual([]);
    errors.mockRestore();
  });
});
