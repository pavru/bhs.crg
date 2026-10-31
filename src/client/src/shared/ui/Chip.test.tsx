// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { Add, Archive, Warning } from './icons';
import { Chip } from './Chip';

afterEach(cleanup);

const classes = (el: Element) => el.className.split(/\s+/);

describe('Chip', () => {
  // Подсказка — у самой кнопки: неактивный чип остаётся в Tab как aria-disabled и говорит почему (ревью #1704).
  it('title неактивного чипа — aria-disabled вместо disabled, причина — описанием', () => {
    const onClick = vi.fn();
    render(<Chip selected={false} disabled title="Тэг уже назначен" onClick={onClick}>Дата</Chip>);
    const chip = screen.getByRole('button', { name: 'Дата' });
    expect(chip.hasAttribute('disabled')).toBe(false);
    expect(chip.getAttribute('aria-disabled')).toBe('true');
    expect(chip.getAttribute('aria-description')).toBe('Тэг уже назначен');
    fireEvent.click(chip);
    expect(onClick).not.toHaveBeenCalled();
  });

  // [UI-68]: filter chip — 32, форма 8; выбранный — secondary-container без контура, с галочкой.
  it('filter: aria-pressed; у выбранного нет контура и знак сменяется галочкой', () => {
    const { rerender } = render(<Chip selected={false} icon={Archive}>В архиве</Chip>);
    let chip = screen.getByRole('button', { name: 'В архиве' });
    expect(chip.getAttribute('aria-pressed')).toBe('false');
    expect(classes(chip)).toEqual(expect.arrayContaining(['h-8', 'rounded-sm', 'text-label-large', 'border-outline-variant']));
    const unselectedIcon = chip.querySelector('svg')!.innerHTML;

    rerender(<Chip selected icon={Archive}>В архиве</Chip>);
    chip = screen.getByRole('button', { name: 'В архиве' });
    expect(chip.getAttribute('aria-pressed')).toBe('true');
    expect(classes(chip)).toEqual(expect.arrayContaining(['border-transparent', 'bg-secondary-container']));
    expect(classes(chip)).not.toContain('border-outline-variant');
    expect(chip.querySelector('svg')!.innerHTML).not.toBe(unselectedIcon);
  });

  it('filter с menu — не переключатель: стрелка, без галочки', () => {
    render(<Chip selected menu icon={Archive}>Колонки</Chip>);
    const chip = screen.getByRole('button', { name: 'Колонки' });
    expect(chip.hasAttribute('aria-pressed')).toBe(false);
    expect(chip.querySelectorAll('svg')).toHaveLength(2);
  });

  it('assist — не переключатель, знак primary', () => {
    render(<Chip icon={Add}>Добавить</Chip>);
    const chip = screen.getByRole('button', { name: 'Добавить' });
    expect(chip.hasAttribute('aria-pressed')).toBe(false);
    expect(chip.querySelector('svg')!.getAttribute('class')).toContain('text-primary');
  });

  it('input: снятие — отдельная кнопка с именем из removeLabel; selected только красит', () => {
    const onClick = vi.fn();
    const onRemove = vi.fn();
    render(<Chip selected count={3} onClick={onClick} onRemove={onRemove} removeLabel="Снять условие «Ошибки»">Ошибки</Chip>);
    fireEvent.click(screen.getByRole('button', { name: 'Снять условие «Ошибки»' }));
    expect(onRemove).toHaveBeenCalledOnce();
    expect(onClick).not.toHaveBeenCalled();
    expect(screen.getByRole('button', { name: /Ошибки.*3/ }).hasAttribute('aria-pressed')).toBe(false);
  });

  // Тона нет: о состоянии говорит знак цвета роли, контейнер тот же ([UI-68]).
  it('iconTone красит знак, а не чип; iconLabel называет знак диктору', () => {
    render(<Chip selected={false} icon={Warning} iconTone="warning" iconLabel="нет в справочнике">Поставщик</Chip>);
    const chip = screen.getByRole('button', { name: /Поставщик/ });
    expect(classes(chip)).toContain('border-outline-variant');
    expect(chip.className).not.toMatch(/warning/);
    const icon = screen.getByLabelText('нет в справочнике');
    expect(icon.getAttribute('class')).toContain('text-warning');
  });

  it('тип: iconTone без icon, menu с onRemove и assist без знака не собираются', () => {
    // @ts-expect-error цвет без знака — снова тон
    void <Chip selected={false} iconTone="error">a</Chip>;
    // @ts-expect-error замыкающий знак у input chip один — крестик
    void <Chip menu onRemove={() => {}} removeLabel="Снять">a</Chip>;
    // @ts-expect-error у input chip имя крестика обязательно
    void <Chip onRemove={() => {}}>a</Chip>;
    // @ts-expect-error assist без знака — бывший пунктир
    void <Chip>a</Chip>;
    expect(true).toBe(true);
  });
});
