// @vitest-environment jsdom
import { useState } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { RadioGroup, type RadioOption } from './RadioGroup';

// Радиокнопки MD3 ([UI-69]): роли и клавиатура по APG, имя группы, цель — строка с подписью.
afterEach(cleanup);
type Scope = 'Full' | 'Configuration';
const ITEMS: RadioOption<Scope>[] = [
  { value: 'Full', label: 'настройку и проектные данные' },
  { value: 'Configuration', label: 'только настройку' },
];

function Backup({ onPick = () => {} }: { onPick?: (v: Scope) => void }) {
  const [scope, setScope] = useState<Scope>('Full');
  return <RadioGroup label="Что включать" items={ITEMS} value={scope} onChange={v => { setScope(v); onPick(v); }} />;
}

describe('RadioGroup', () => {
  it('группа — radiogroup с видимым именем, пункт — radio с aria-checked', () => {
    render(<Backup />);
    const group = screen.getByRole('radiogroup', { name: 'Что включать' });
    expect(group).toBeTruthy();
    expect(screen.getByText('Что включать').id).toBe(group.getAttribute('aria-labelledby'));
    expect(screen.getByRole('radio', { name: 'настройку и проектные данные' }).getAttribute('aria-checked')).toBe('true');
    expect(screen.getByRole('radio', { name: 'только настройку' }).getAttribute('aria-checked')).toBe('false');
  });

  it('Tab входит в группу один раз — на выбранный вариант', () => {
    render(<Backup />);
    const [full, config] = screen.getAllByRole('radio');
    expect(full.tabIndex).toBe(0);
    expect(config.tabIndex).toBe(-1);
  });

  it('стрелка выбирает соседний и переводит фокус, по кругу (APG)', () => {
    const onPick = vi.fn();
    render(<Backup onPick={onPick} />);
    const [full, config] = screen.getAllByRole('radio');
    full.focus();
    fireEvent.keyDown(full, { key: 'ArrowDown' });
    expect(onPick).toHaveBeenLastCalledWith('Configuration');
    expect(document.activeElement).toBe(config);
    expect(config.getAttribute('aria-checked')).toBe('true');
    expect(config.tabIndex).toBe(0);
    fireEvent.keyDown(config, { key: 'ArrowRight' });
    expect(onPick).toHaveBeenLastCalledWith('Full');
    expect(document.activeElement).toBe(full);
  });

  it('цель — вся строка: нажатие на подпись выбирает вариант', () => {
    const onPick = vi.fn();
    render(<Backup onPick={onPick} />);
    fireEvent.click(screen.getByText('только настройку'));
    expect(onPick).toHaveBeenCalledWith('Configuration');
  });

  it('нажатие на выбранный вариант onChange не зовёт; с confirm — зовёт: выбор ведёт дальше', () => {
    const onChange = vi.fn();
    const { rerender } = render(<RadioGroup aria-label="Вариант" items={ITEMS} value="Full" onChange={onChange} />);
    fireEvent.click(screen.getAllByRole('radio')[0]);
    expect(onChange).not.toHaveBeenCalled();
    rerender(<RadioGroup aria-label="Вариант" items={ITEMS} value="Full" onChange={onChange} confirm />);
    fireEvent.click(screen.getAllByRole('radio')[0]);
    expect(onChange).toHaveBeenCalledWith('Full');
  });

  it('хвост хоть у одного варианта — строки во всю ширину, подпись тянется: хвост у правого края', () => {
    render(<RadioGroup aria-label="Вариант" value="Full" onChange={() => {}}
      items={[ITEMS[0], { ...ITEMS[1], trailing: <span>задан</span> }]} />);
    for (const r of screen.getAllByRole('radio')) {
      expect(r.className.split(/\s+/)).toContain('w-full');
      expect((r.children[1] as HTMLElement).className.split(/\s+/)).toContain('flex-1');
    }
  });

  it('confirm — стрелка двигает только фокус, выбирает нажатие', () => {
    const onChange = vi.fn();
    render(<RadioGroup aria-label="Вариант" items={ITEMS} value="Full" onChange={onChange} confirm />);
    const [full, config] = screen.getAllByRole('radio');
    full.focus();
    fireEvent.keyDown(full, { key: 'ArrowDown' });
    expect(onChange).not.toHaveBeenCalled();
    expect(document.activeElement).toBe(config);
    fireEvent.click(config);
    expect(onChange).toHaveBeenCalledWith('Configuration');
  });

  it('отключённая группа не выбирается; круг — on-surface 38 %, без слоя', () => {
    const onChange = vi.fn();
    render(<RadioGroup label="Что включать" items={ITEMS} value="Full" onChange={onChange} disabled />);
    const config = screen.getByRole('radio', { name: 'только настройку' }) as HTMLButtonElement;
    expect(config.disabled).toBe(true);
    fireEvent.click(config);
    expect(onChange).not.toHaveBeenCalled();
    const mark = config.firstElementChild as HTMLElement;
    expect(mark.className).not.toMatch(/state-layer/);
    expect((mark.firstElementChild as HTMLElement).className).toMatch(/border-on-surface\/38/);
  });

  it('круг 20 в слое 40, выбранный — primary с точкой', () => {
    render(<Backup />);
    const mark = screen.getByRole('radio', { name: 'настройку и проектные данные' }).firstElementChild as HTMLElement;
    expect(mark.getAttribute('aria-hidden')).toBe('true');
    expect(mark.className.split(/\s+/)).toEqual(expect.arrayContaining(['size-10', 'rounded-full', 'state-layer']));
    const circle = mark.firstElementChild as HTMLElement;
    expect(circle.className.split(/\s+/)).toEqual(expect.arrayContaining(['size-5', 'border-2', 'border-primary']));
    expect(circle.querySelector('.bg-primary')).toBeTruthy();
  });
});
