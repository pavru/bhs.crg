// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import type { Template } from '@/shared/api/types';
import { GenerationTemplateRows } from './GenerationTemplateRows';

/** Шаблоны генерации (Р8, sharedui-md3-18): флажок — участие, строка — шаблон для параметров; каждый — своё. */

afterEach(cleanup);

const tpl = (id: string, name: string, isDefault = false) =>
  ({ id, name, isDefault, version: 3, isActive: true }) as unknown as Template;

const mount = (focusedId: string | null = 'a') => {
  const onToggle = vi.fn();
  const onFocus = vi.fn();
  render(<GenerationTemplateRows templates={[tpl('a', 'АОСР', true), tpl('b', 'АОСР краткий')]} participating={['a']}
    focusedId={focusedId} disabled={false} onToggle={onToggle} onFocus={onFocus} />);
  return { onToggle, onFocus };
};

describe('шаблоны генерации', () => {
  it('флажок меняет участие, а выбранный для параметров шаблон — нет', () => {
    const { onToggle, onFocus } = mount();
    fireEvent.click(screen.getByRole('checkbox', { name: /«АОСР краткий»/ }));
    expect(onToggle).toHaveBeenCalledWith('b', true);
    expect(onFocus).not.toHaveBeenCalled();
  });

  it('строка выбирает шаблон для параметров и флажок не меняет', () => {
    const { onToggle, onFocus } = mount();
    fireEvent.click(screen.getByRole('button', { name: /АОСР краткий/ }));
    expect(onFocus).toHaveBeenCalledWith('b');
    expect(onToggle).not.toHaveBeenCalled();
  });

  it('выбранный — aria-current у одной строки, флажок — сосед цели, а не внутри неё', () => {
    mount('a');
    const current = screen.getAllByRole('button').filter(b => b.getAttribute('aria-current') === 'true');
    expect(current).toHaveLength(1);
    expect(current[0].textContent).toContain('★ АОСР');
    expect(current[0].contains(screen.getByRole('checkbox', { name: /«АОСР»/ }))).toBe(false);
  });
});
