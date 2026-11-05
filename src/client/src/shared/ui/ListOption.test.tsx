// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { useState } from 'react';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { ListOption } from './ListOption';
import { ListItem } from './ListItem';
import { ChevronRight } from './icons';

/** Вариант выбора ([UI-87], sharedui-md3-21) и формы строки карточки ([UI-84]): `passive`, `checked`. */

afterEach(cleanup);

const classes = (el: Element) => el.className.split(/\s+/);
const box = (id: string) => document.getElementById(id)!.firstElementChild!;

/** Поле поиска с listbox: фокус в поле, активный — по стрелкам. */
function Picker({ onChoose, onStep }: { onChoose: (v: string) => void; onStep?: () => void }) {
  const items = ['Кабель', 'Лоток', 'Труба'];
  const [active, setActive] = useState(0);
  return (
    <>
      <input aria-label="Найти" role="combobox" aria-expanded aria-controls="lb" aria-activedescendant={`o${active}`}
        onKeyDown={e => {
          if (e.key === 'ArrowDown') setActive(a => (a + 1) % items.length);
          if (e.key === 'Enter') onChoose(items[active]);
        }} />
      <div id="lb" role="listbox">
        {items.map((v, i) => (
          <ListOption key={v} id={`o${i}`} headline={v} active={i === active} current={v === 'Труба'}
            disabled={v === 'Лоток' && !onStep} onChoose={() => onChoose(v)} onHover={() => setActive(i)}
            action={onStep && i === 0 ? { icon: ChevronRight, label: 'Разделы', onAct: onStep } : undefined} />
        ))}
      </div>
    </>
  );
}

describe('ListOption', () => {
  it('фокус остаётся в поле: стрелка двигает активный, его заливка — secondary-container', () => {
    render(<Picker onChoose={() => {}} />);
    const input = screen.getByRole('combobox');
    input.focus();
    fireEvent.keyDown(input, { key: 'ArrowDown' });
    expect(document.activeElement).toBe(input);
    expect(input.getAttribute('aria-activedescendant')).toBe('o1');
    expect(classes(document.getElementById('o1')!)).toContain('bg-secondary-container');
    expect(classes(document.getElementById('o0')!)).not.toContain('bg-secondary-container');
    // Вариант — не остановка Tab
    for (const o of screen.getAllByRole('option')) expect(o.hasAttribute('tabindex')).toBe(false);
  });

  it('aria-selected — только у текущего значения, с галочкой «сейчас»; у активного его нет', () => {
    render(<Picker onChoose={() => {}} />);
    const options = screen.getAllByRole('option');
    expect(options.map(o => o.getAttribute('aria-selected'))).toEqual(['false', 'false', 'true']);
    expect(options[2].getAttribute('aria-description')).toBe('сейчас');
    expect(options[2].querySelector('svg')).not.toBeNull();
    expect(options[0].hasAttribute('aria-description')).toBe(false);
    expect(screen.getByRole('option', { name: 'Труба' })).toBe(options[2]);
  });

  it('нажатие выбирает и не уводит фокус; архивный — «в архиве», не выбирается', () => {
    const onChoose = vi.fn();
    render(<Picker onChoose={onChoose} />);
    const [first, archived] = screen.getAllByRole('option');
    expect(fireEvent.mouseDown(first)).toBe(false); // preventDefault: поле не теряет фокус
    fireEvent.click(first);
    expect(onChoose).toHaveBeenCalledWith('Кабель');
    expect(archived.getAttribute('aria-disabled')).toBe('true');
    expect(archived.textContent).toContain('в архиве');
    fireEvent.click(archived);
    expect(onChoose).toHaveBeenCalledTimes(1);
  });

  it('действие внутри варианта не выбирает его и не берёт фокус', () => {
    const onChoose = vi.fn(), onStep = vi.fn();
    render(<Picker onChoose={onChoose} onStep={onStep} />);
    const step = screen.getByRole('button', { name: 'Разделы', hidden: true });
    expect(step.getAttribute('tabindex')).toBe('-1');
    expect(fireEvent.mouseDown(step)).toBe(false);
    fireEvent.click(step);
    expect(onStep).toHaveBeenCalledTimes(1);
    expect(onChoose).not.toHaveBeenCalled();
  });

  it('наведение на неактивный — слой состояния и onHover, не заливка primary-container', () => {
    render(<Picker onChoose={() => {}} />);
    const third = document.getElementById('o2')!;
    expect(classes(third)).toContain('state-layer');
    expect(third.className).not.toContain('primary-container');
    fireEvent.mouseMove(third);
    expect(screen.getByRole('combobox').getAttribute('aria-activedescendant')).toBe('o2');
  });

  it('формы: list — 48 / 64 и body-large, menu — 40, на касании 48, label-large с переносом', () => {
    render(<>
      <ListOption id="l1" headline="Акт" active={false} onChoose={() => {}} />
      <ListOption id="l2" headline="Акт" supporting="раздел" active={false} onChoose={() => {}} />
      <ListOption id="m1" form="menu" headline="Стройка на Ленина" supporting="4 раздела" active={false} onChoose={() => {}} />
    </>);
    expect(classes(box('l1'))).toContain('min-h-12');
    expect(classes(box('l2'))).toContain('min-h-16');
    expect(document.querySelector('#l1 .text-body-large')).not.toBeNull();
    expect(classes(box('m1'))).toEqual(expect.arrayContaining(['min-h-10', 'pointer-coarse:min-h-12']));
    const label = screen.getByText('Стройка на Ленина');
    expect(classes(label)).toEqual(expect.arrayContaining(['text-label-large', 'break-words']));
    expect(classes(screen.getByText('4 раздела'))).toContain('text-body-small');
  });
});

describe('ListItem: строки карточки', () => {
  it('отмеченные — заливка без aria-current', () => {
    render(<>
      <ListItem data-testid="a" headline="Строка 2" checked onClick={() => {}} />
      <ListItem data-testid="b" headline="Строка 4" checked onClick={() => {}} />
    </>);
    for (const id of ['a', 'b']) {
      const target = screen.getByTestId(id);
      expect(target.hasAttribute('aria-current')).toBe(false);
      expect(classes(target.parentElement!)).toContain('bg-secondary-container');
    }
  });

  it('passive — не кнопка и без слоя нажатия; соседи остаются соседями', () => {
    render(<ListItem passive data-testid="p" headline="Найдено в интернете" action={<button type="button">В библиотеку</button>} />);
    const body = screen.getByTestId('p');
    expect(body.tagName).toBe('DIV');
    expect(body.hasAttribute('data-li-target')).toBe(false);
    expect(classes(body)).not.toContain('cursor-pointer');
    expect(classes(body.parentElement!)).not.toContain('state-layer-row');
    expect(screen.getAllByRole('button')).toHaveLength(1);
    expect(screen.getByRole('button').parentElement!.hasAttribute('data-li-side')).toBe(true);
  });

  it('без цели и без passive строка не собирается', () => {
    // @ts-expect-error — цель обязательна: onClick, to или passive
    const bad = <ListItem headline="x" />;
    // @ts-expect-error — у строки без цели нет «открытой карточки»
    const sel = <ListItem passive headline="x" selected />;
    expect([bad, sel]).toHaveLength(2);
  });
});
