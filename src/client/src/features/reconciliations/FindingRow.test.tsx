// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import type { Finding } from '@/shared/api/reconciliations';
import { FindingRow } from './ReconciliationsPage';

/** Находка сверки — строка без своей цели ([UI-84], sharedui-md3-21 задача 2.4). */

afterEach(cleanup);

const finding = (over: Partial<Finding> = {}): Finding => ({
  id: 'f1', key: 'ввг-3х2.5', label: 'Кабель ВВГ 3×2,5', leftValue: 120, rightValue: 118, status: 'Mismatch',
  provenance: { left: null, right: null }, resolved: false, decision: null, ...over,
}) as Finding;

function row(props: Partial<Parameters<typeof FindingRow>[0]> = {}) {
  const onDecide = vi.fn();
  render(<MemoryRouter><FindingRow f={finding()} onDecide={onDecide} linkable slot checked={false} onToggle={() => {}} {...props} /></MemoryRouter>);
  return onDecide;
}

describe('FindingRow', () => {
  it('строка без своей цели: «Разобрать» — действие у края, видно без наведения', () => {
    const onDecide = row();
    const decide = screen.getByRole('button', { name: 'Разобрать' });
    expect(decide.className).not.toContain('opacity-0');
    fireEvent.click(decide);
    expect(onDecide).toHaveBeenCalledTimes(1);
    expect(screen.getByText('Кабель ВВГ 3×2,5').closest('button')).toBeNull();
  });

  it('значения — своим кеглем body-medium у края с sm, на телефоне — во второй строке', () => {
    row();
    const [inLine, atEdge] = screen.getAllByText('120 / 118');
    expect(atEdge.className).toContain('text-body-medium');
    expect(atEdge.className).toContain('hidden sm:inline');
    expect(inLine.className).toContain('sm:hidden');
  });

  it('несвязываемая строка в списке со связываемыми — пустое место под флажок, подписи в одном столбце', () => {
    row({ linkable: false });
    expect(screen.queryByRole('checkbox')).toBeNull();
    expect(document.querySelector('[aria-hidden][class~="size-4.5"]')).not.toBeNull();
  });

  it('статус — первым во второй строке рядом с провенансом, а не ведущим узлом: на 375 названию остаётся ширина', () => {
    row({ f: finding({ provenance: { left: { sourceId: 's', column: 'Количество', rows: [3] }, right: null } }) });
    const line = screen.getByText('слева: Количество, строк 1').parentElement!;
    expect(line.textContent).toMatch(/^Расхождение/);
    expect(line.contains(screen.getByText('Кабель ВВГ 3×2,5'))).toBe(false);
  });

  it('в списке нечего связывать — места под флажок нет', () => {
    row({ linkable: false, slot: false });
    expect(document.querySelector('[class~="size-4.5"]')).toBeNull();
  });
});
