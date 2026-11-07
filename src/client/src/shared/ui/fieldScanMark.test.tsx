// @vitest-environment jsdom
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { DateField } from './DateField';
import { fieldEnd, fieldLabelRest, FieldDensityContext } from './fieldBox';
import { Select, SelectItem } from './Select';
import { TextAreaField } from './TextAreaField';
import { TextField } from './TextField';

vi.mock('@/shared/hooks/useLocale', async importOriginal => ({
  ...(await importOriginal<typeof import('@/shared/hooks/useLocale')>()),
  useLocale: () => ['ru-RU', () => {}, 'idle'],
}));

/**
 * Знак «со скана, не проверено» в хвосте поля ([UI-88], sharedui-field-scan-mark): место знака в таблице хвоста,
 * знак с подсказкой у четырёх полей оболочки, порядок перед знаком ошибки и одна приписка к имени для диктора.
 */

beforeAll(() => {
  globalThis.ResizeObserver ??= class { observe() {} unobserve() {} disconnect() {} } as unknown as typeof ResizeObserver;
  // Radix Select зовёт API указателя и прокрутки, которых в jsdom нет.
  Element.prototype.hasPointerCapture ??= () => false;
  Element.prototype.releasePointerCapture ??= () => {};
  Element.prototype.scrollIntoView ??= () => {};
});
afterEach(() => { cleanup(); vi.useRealTimers(); });

const scanMarks = () => document.querySelectorAll('[data-field-scan-mark]');
const scanMark = () => document.querySelector<HTMLElement>('[data-field-scan-mark]')!;
const errorMark = () => document.querySelector<HTMLElement>('[data-field-error-mark]')!;
const classes = (el: Element) => el.className.split(/\s+/);
/** `a` стоит в разметке раньше `b`. */
const before = (a: Node, b: Node) => (a.compareDocumentPosition(b) & Node.DOCUMENT_POSITION_FOLLOWING) !== 0;
const NOTE = '(со скана, не проверено)';

describe('fieldBox: место знака «со скана»', () => {
  it('без кнопки хвоста — строка mark: 30 у текста, 38 у подписи в покое, при касании то же', () => {
    expect(fieldEnd('form', { fromScan: true })).toBe('pr-[calc(1.875rem+var(--field-mark,0px))]');
    expect(fieldLabelRest({ fromScan: true })).toBe('max-w-[calc(100%-2.375rem-var(--field-mark,0px))]');
  });

  it('перед кнопкой хвоста — та же строка, что у замка (markButton)', () => {
    expect(fieldEnd('form', { trailing: true, fromScan: true })).toBe(fieldEnd('form', { trailing: true, trailingMark: true }));
    expect(fieldLabelRest({ trailing: true, fromScan: true })).toBe(fieldLabelRest({ trailing: true, trailingMark: true }));
  });

  it('без пометки места не прибавляется', () => {
    expect(fieldEnd('form', { fromScan: false })).toBe(fieldEnd('form'));
  });
});

describe('Знак «со скана»: вид и подсказка', () => {
  it('значок 18 цвета warning, скрыт от диктора, подсказка — при наведении', () => {
    vi.useFakeTimers();
    render(<TextField label="Номер счёта" fromScan value="СЧ-104" onChange={() => {}} />);
    const mark = scanMark();
    expect(mark.querySelector('svg')!.getAttribute('width')).toBe('18');
    expect(classes(mark)).toEqual(expect.arrayContaining(['text-warning', 'pointer-events-auto']));
    expect(mark.getAttribute('aria-hidden')).not.toBeNull();
    fireEvent.pointerEnter(mark, { pointerType: 'mouse' });
    act(() => { vi.advanceTimersByTime(500); });
    expect(screen.getByRole('tooltip').textContent).toBe('Со скана, не проверено. Правка снимает метку');
  });

  it('без пометки нет ни знака, ни приписки', () => {
    render(<TextField label="Номер счёта" value="СЧ-104" onChange={() => {}} />);
    expect(scanMarks()).toHaveLength(0);
    expect(screen.getByRole('textbox', { name: 'Номер счёта' })).not.toBeNull();
  });
});

describe('TextField и TextAreaField', () => {
  it('знак в слоте хвоста, текст и подпись в покое кончаются до него, имя с припиской', () => {
    const { container } = render(<TextField label="Номер счёта" fromScan value="" onChange={() => {}} />);
    const input = screen.getByRole('textbox', { name: `Номер счёта ${NOTE}` });
    expect(classes(input)).toContain('pr-[calc(1.875rem+var(--field-mark,0px))]');
    expect(classes(container.querySelector('label')!)).toContain('max-w-[calc(100%-2.375rem-var(--field-mark,0px))]');
    // Видимая подпись и вырез рамки — без приписки: она для диктора.
    expect(container.querySelector('legend')!.textContent).not.toContain('со скана');
    expect(container.querySelector('label .sr-only')!.textContent).toBe(NOTE);
  });

  it('с кнопкой хвоста — знак перед ней, место как у знака перед кнопкой', () => {
    render(<TextField label="Сумма" fromScan value="" onChange={() => {}}
      trailing={<button type="button">Очистить</button>} />);
    expect(before(scanMark(), screen.getByRole('button', { name: 'Очистить' }))).toBe(true);
    expect(classes(screen.getByRole('textbox', { name: `Сумма ${NOTE}` })))
      .toContain('pr-[calc(4.125rem+var(--field-mark,0px))]');
  });

  it('с ошибкой — знак «со скана» перед знаком ошибки, оба в поле', () => {
    render(<TextField label="ИНН" fromScan error="Не тот ИНН" value="1" onChange={() => {}} />);
    expect(before(scanMark(), errorMark())).toBe(true);
  });

  it('в клетке сетки подписи нет — приписку несёт имя контрола', () => {
    render(<FieldDensityContext.Provider value="cell">
      <TextField label="Количество" fromScan value="" onChange={() => {}} />
    </FieldDensityContext.Provider>);
    expect(screen.getByRole('textbox', { name: `Количество ${NOTE}` })).not.toBeNull();
    expect(scanMarks()).toHaveLength(1);
  });

  it('многострочное поле — знак и отступ обёртки области под него', () => {
    const { container } = render(<TextAreaField label="Назначение" fromScan value="" onChange={() => {}} />);
    expect(screen.getByRole('textbox', { name: `Назначение ${NOTE}` })).not.toBeNull();
    expect(scanMarks()).toHaveLength(1);
    expect(container.querySelector('.pt-4')!.className).toContain('pr-[calc(1.875rem+var(--field-mark,0px))]');
  });
});

describe('Select', () => {
  const field = (extra: { error?: string } = {}) => render(
    <Select label="Поставщик" fromScan value="a" onValueChange={() => {}} {...extra}>
      <SelectItem value="a">ООО «Кабель-Торг»</SelectItem>
    </Select>,
  );

  it('имя с припиской через aria-labelledby, знак внутри триггера перед стрелкой', () => {
    field();
    const trigger = screen.getByRole('combobox', { name: `Поставщик ${NOTE}` });
    const arrow = document.querySelector('[data-field-arrow]')!;
    expect(trigger.contains(scanMark())).toBe(true);
    expect(trigger.contains(arrow)).toBe(false);
    expect(before(scanMark(), arrow)).toBe(true);
    // Отступ триггера — под стрелку; знак встаёт перед ним в раскладке триггера.
    expect(classes(trigger)).toContain('pr-[calc(2.75rem+var(--field-mark,0px))]');
  });

  it('щелчок по знаку открывает список', () => {
    field();
    const trigger = screen.getByRole('combobox', { name: `Поставщик ${NOTE}` });
    fireEvent.pointerDown(scanMark(), { button: 0, ctrlKey: false, pointerType: 'mouse' });
    // Открытый список Radix прячет остальное от диктора — триггер берётся до щелчка.
    expect(trigger.getAttribute('data-state')).toBe('open');
    expect(screen.getByRole('option', { name: 'ООО «Кабель-Торг»' })).not.toBeNull();
  });

  it('с ошибкой — оба знака в триггере, «со скана» первым', () => {
    field({ error: 'Обязательное поле' });
    const trigger = screen.getByRole('combobox', { name: `Поставщик ${NOTE}` });
    expect(trigger.contains(errorMark())).toBe(true);
    expect(before(scanMark(), errorMark())).toBe(true);
  });

  it('ошибка без пометки — знак ошибки тоже в триггере, перед стрелкой', () => {
    render(<Select label="Раздел" error="Обязательное поле" value="" onValueChange={() => {}}>
      <SelectItem value="a">Первый</SelectItem>
    </Select>);
    const trigger = screen.getByRole('combobox', { name: 'Раздел' });
    expect(trigger.contains(errorMark())).toBe(true);
    expect(before(errorMark(), document.querySelector('[data-field-arrow]')!)).toBe(true);
    expect(scanMarks()).toHaveLength(0);
  });

  it('плотное без подписи и клетка — приписка к имени из aria-label', () => {
    render(<>
      <Select density="dense" aria-label="Итог" fromScan value="a" onValueChange={() => {}}>
        <SelectItem value="a">Сумма</SelectItem>
      </Select>
      <Select density="cell" aria-label="Ставка" fromScan value="a" onValueChange={() => {}}>
        <SelectItem value="a">20 %</SelectItem>
      </Select>
    </>);
    expect(screen.getByRole('combobox', { name: `Итог ${NOTE}` })).not.toBeNull();
    expect(screen.getByRole('combobox', { name: `Ставка ${NOTE}` })).not.toBeNull();
    expect(scanMarks()).toHaveLength(2);
  });
});

describe('DateField', () => {
  it('знак перед крестиком и календарём, хвост — те же две кнопки, имя группы с припиской', () => {
    render(<DateField label="Дата счёта" fromScan value="2026-09-03" onChange={() => {}} />);
    const group = screen.getByRole('group', { name: `Дата счёта ${NOTE}` });
    const clear = screen.getByRole('button', { name: 'Очистить дату' });
    const calendar = screen.getByRole('button', { name: 'Открыть календарь' });
    expect(group.contains(scanMark())).toBe(true);
    expect(before(scanMark(), clear)).toBe(true);
    expect(before(clear, calendar)).toBe(true);
    expect(group.querySelectorAll('button')).toHaveLength(2);
  });

  it('с ошибкой — «со скана» перед знаком ошибки, оба перед хвостом', () => {
    render(<DateField label="Дата счёта" fromScan invalid value="2026-09-03" onChange={() => {}} />);
    expect(before(scanMark(), errorMark())).toBe(true);
    expect(before(errorMark(), screen.getByRole('button', { name: 'Очистить дату' }))).toBe(true);
  });
});
