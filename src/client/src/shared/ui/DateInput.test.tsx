// @vitest-environment jsdom
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, within } from '@testing-library/react';
import { useState } from 'react';
import { DateInput } from './DateInput';

// Язык пользователя задаёт маску (issue #1461). jsdom говорит по-английски, поэтому язык — явно:
// по умолчанию русский, а проверки других языков меняют его на время своего теста.
let locale = 'ru-RU';
vi.mock('@/shared/hooks/useLocale', async importOriginal => ({
  ...(await importOriginal<typeof import('@/shared/hooks/useLocale')>()),
  useLocale: () => [locale, () => {}, 'idle'],
}));
import { DateField } from './DateField';

/**
 * Честный ввод даты (issue #1460). Проверяется и «отдано правильное», и «НЕ отдано»: несуществующая
 * и недописанная дата наружу не уходят, а набранное при уходе из поля не исчезает молча.
 */

beforeAll(() => {
  // Календарь — поповер Radix: jsdom не знает ни наблюдателя размеров, ни захвата указателя.
  globalThis.ResizeObserver ??= class { observe() {} unobserve() {} disconnect() {} } as unknown as typeof ResizeObserver;
  Element.prototype.hasPointerCapture ??= () => false;
  Element.prototype.scrollIntoView ??= () => {};
});
afterEach(() => { cleanup(); vi.useRealTimers(); locale = 'ru-RU'; });

/** Поле с настоящим состоянием — как в форме: отданное значение возвращается в `value`. */
function Controlled({ initial = '', onChange, field = false, calendar = false }: { initial?: string; onChange?: (v: string) => void; field?: boolean; calendar?: boolean }) {
  const [value, setValue] = useState(initial);
  const change = (v: string) => { onChange?.(v); setValue(v); };
  return (
    <>
      {field
        ? <DateField label="Дата платежа" value={value} onChange={change} hint="от неё считается срок" />
        : <DateInput label="Дата" value={value} onChange={change} calendar={calendar} />}
      <output data-testid="value">{value}</output>
      <button type="button">дальше</button>
    </>
  );
}

const seg = (ph: 'ДД' | 'ММ' | 'ГГГГ') => screen.getByPlaceholderText(ph) as HTMLInputElement;

function typeDate(d: string, m: string, y: string) {
  fireEvent.focus(seg('ДД')); fireEvent.change(seg('ДД'), { target: { value: d } });
  fireEvent.focus(seg('ММ')); fireEvent.change(seg('ММ'), { target: { value: m } });
  fireEvent.focus(seg('ГГГГ')); fireEvent.change(seg('ГГГГ'), { target: { value: y } });
}

/** Уход из поля: blur последнего сегмента и таймер, которым поле отличает переход между сегментами. */
function leave(from: HTMLInputElement = seg('ГГГГ')) {
  fireEvent.blur(from);
  act(() => { vi.advanceTimersByTime(200); });
}

describe('DateInput: несуществующая дата', () => {
  it('31.02.2026 НЕ отдаётся и названа ошибкой', () => {
    vi.useFakeTimers();
    const onChange = vi.fn();
    // Ноябрь — 30 дней: «31» не даёт даты уже на первом сегменте, и ни один шаг ввода её не отдаёт.
    render(<Controlled initial="2026-11-18" onChange={onChange} field />);
    typeDate('31', '02', '2026');
    expect(onChange).not.toHaveBeenCalled();
    expect(screen.getByTestId('value').textContent).toBe('2026-11-18');
    expect(screen.getByText('Такой даты нет. От неё считается срок')).toBeTruthy();
    leave();
    expect(seg('ДД').value).toBe('31');
    expect(seg('ДД').getAttribute('aria-invalid')).toBe('true');
  });

  it('месяц 13 и день 39 — тоже не отдаются', () => {
    const onChange = vi.fn();
    render(<Controlled onChange={onChange} />);
    typeDate('18', '13', '2026');
    typeDate('39', '10', '2026');
    expect(onChange).not.toHaveBeenCalled();
  });

  it('исправленная дата отдаётся, ошибка уходит', () => {
    const onChange = vi.fn();
    render(<Controlled onChange={onChange} field />);
    typeDate('31', '02', '2026');
    fireEvent.change(seg('ДД'), { target: { value: '28' } });
    expect(onChange).toHaveBeenLastCalledWith('2026-02-28');
    expect(screen.queryByText(/Такой даты нет/)).toBeNull();
  });
});

describe('DateInput: вставка', () => {
  it.each([['18.10.2026'], ['2026-10-18'], ['18/10/2026']])('%s в любой сегмент заполняет поле', text => {
    for (const ph of ['ДД', 'ММ', 'ГГГГ'] as const) {
      const onChange = vi.fn();
      render(<Controlled onChange={onChange} />);
      fireEvent.paste(seg(ph), { clipboardData: { getData: () => text } });
      expect(onChange).toHaveBeenCalledWith('2026-10-18');
      expect([seg('ДД').value, seg('ММ').value, seg('ГГГГ').value]).toEqual(['18', '10', '2026']);
      cleanup();
    }
  });

  it('вставка несуществующей даты не отдаётся', () => {
    const onChange = vi.fn();
    render(<Controlled onChange={onChange} />);
    fireEvent.paste(seg('ДД'), { clipboardData: { getData: () => '31.02.2026' } });
    expect(onChange).not.toHaveBeenCalled();
    expect(seg('ММ').value).toBe('02');
  });
});

describe('DateInput: двузначный год и недописанное', () => {
  it('«18.10.26» при уходе — 18.10.2026', () => {
    vi.useFakeTimers();
    const onChange = vi.fn();
    render(<Controlled onChange={onChange} />);
    typeDate('18', '10', '26');
    expect(onChange).not.toHaveBeenCalled();
    leave();
    expect(onChange).toHaveBeenLastCalledWith('2026-10-18');
    expect(seg('ГГГГ').value).toBe('2026');
  });

  it('уход с недописанным: набранное видно, ошибка под полем, значение НЕ сохранено', () => {
    vi.useFakeTimers();
    const onChange = vi.fn();
    render(<Controlled initial="2026-10-18" onChange={onChange} field />);
    fireEvent.focus(seg('ДД'));
    fireEvent.change(seg('ДД'), { target: { value: '' } });
    fireEvent.focus(seg('ММ'));
    fireEvent.change(seg('ММ'), { target: { value: '1' } });
    expect(screen.queryByText(/Дата не дописана/)).toBeNull(); // посреди ввода — не ошибка
    leave(seg('ММ'));
    expect(onChange).not.toHaveBeenCalled();
    expect(screen.getByTestId('value').textContent).toBe('2026-10-18');
    expect(seg('ДД').value).toBe('');
    expect(seg('ММ').value).toBe('01'); // одна цифра месяца дополняется нулём, как и раньше
    expect(screen.getByText('Дата не дописана. От неё считается срок')).toBeTruthy();
  });

  it('Esc возвращает прежнее значение и гасится в поле', () => {
    const onChange = vi.fn();
    const outer = vi.fn();
    render(<div onKeyDown={outer}><Controlled initial="2026-10-18" onChange={onChange} /></div>);
    fireEvent.focus(seg('ДД'));
    fireEvent.change(seg('ДД'), { target: { value: '3' } });
    fireEvent.keyDown(seg('ДД'), { key: 'Escape' });
    expect(seg('ДД').value).toBe('18');
    expect(outer).not.toHaveBeenCalled();
    expect(onChange).not.toHaveBeenCalled();
    // Без набранного Esc поле не трогает — его получает окно.
    fireEvent.keyDown(seg('ДД'), { key: 'Escape' });
    expect(outer).toHaveBeenCalledTimes(1);
  });

  it('Esc возвращает значение, с которым вошли в поле, а не промежуточное', () => {
    // «31» поверх 10.2026 складывается в 31.10 и отдаётся по пути к 31.02 (живая проба окна оплаты).
    const onChange = vi.fn();
    render(<Controlled initial="2026-10-19" onChange={onChange} />);
    fireEvent.focus(seg('ДД'));
    fireEvent.change(seg('ДД'), { target: { value: '31' } });
    expect(onChange).toHaveBeenLastCalledWith('2026-10-31');
    fireEvent.focus(seg('ММ'));
    fireEvent.change(seg('ММ'), { target: { value: '02' } });
    fireEvent.keyDown(seg('ММ'), { key: 'Escape' });
    expect(onChange).toHaveBeenLastCalledWith('2026-10-19');
    expect([seg('ДД').value, seg('ММ').value, seg('ГГГГ').value]).toEqual(['19', '10', '2026']);
  });

  it('значение сменили снаружи — набранное к прежнему не держится', () => {
    vi.useFakeTimers();
    const { rerender } = render(<DateInput label="Дата" value="2026-10-18" onChange={() => {}} calendar={false} />);
    fireEvent.focus(seg('ГГГГ'));
    fireEvent.change(seg('ГГГГ'), { target: { value: '202' } });
    leave();
    expect(seg('ГГГГ').value).toBe('202');
    rerender(<DateInput label="Дата" value="2025-01-05" onChange={() => {}} calendar={false} />);
    expect([seg('ДД').value, seg('ММ').value, seg('ГГГГ').value]).toEqual(['05', '01', '2025']);
  });
});

describe('DateInput: значение показывается, а не пропадает', () => {
  it('ISO со временем и ДД.ММ.ГГГГ из ячейки — не пустое поле', () => {
    render(<DateInput label="Дата" value="2026-10-18T00:00:00" onChange={() => {}} calendar={false} />);
    expect([seg('ДД').value, seg('ММ').value, seg('ГГГГ').value]).toEqual(['18', '10', '2026']);
    cleanup();
    render(<DateInput label="Дата" value="18.10.2026" onChange={() => {}} compact />);
    expect(seg('ДД').value).toBe('18');
  });

  it('не дата — текстом с пометкой; переписать её можно', () => {
    const onChange = vi.fn();
    render(<DateInput label="Дата" value="вчера" onChange={onChange} calendar={false} />);
    const text = screen.getByRole('button', { name: /вчера — не дата/ });
    expect(text.getAttribute('aria-describedby')).toBeTruthy();
    expect(document.getElementById(text.getAttribute('aria-describedby')!)?.textContent).toMatch(/не дата/);
    // Проход Tab'ом не начинает ввод: показанное не стирается, фокус не уводится в сегменты.
    fireEvent.focus(text);
    expect(screen.getByRole('button', { name: /вчера — не дата/ })).toBeTruthy();
    expect(screen.queryByPlaceholderText('ДД')).toBeNull();
    vi.useFakeTimers();
    fireEvent.click(text);
    act(() => { vi.runOnlyPendingTimers(); });
    typeDate('18', '10', '2026');
    expect(onChange).toHaveBeenLastCalledWith('2026-10-18');
  });
});

describe('DateInput: правки по ревью PR #1542', () => {
  it('не дата у поля только для чтения — текстом, без ошибки «наберите заново»', () => {
    const onProblem = vi.fn();
    render(<DateInput label="Дата" value="вчера" onChange={() => {}} onProblem={onProblem} readOnly />);
    expect(screen.getByText('вчера')).toBeTruthy();
    expect(onProblem).not.toHaveBeenCalledWith(expect.stringMatching(/не дата/));
    cleanup();
    render(<DateField label="Дата" value="вчера" onChange={() => {}} readOnly />);
    expect(screen.queryByText(/наберите дату заново/)).toBeNull();
  });

  it('ошибка связана с сегментами описанием — и у DateField, чья подпись с полем не связана', () => {
    render(<Controlled initial="2026-11-18" field />);
    typeDate('31', '02', '2026');
    const id = seg('ДД').getAttribute('aria-describedby');
    expect(id && document.getElementById(id)?.textContent).toBe('Такой даты нет');
    expect(document.querySelectorAll('[role="alert"]').length).toBe(0);
  });

  it('Esc на значке календаря значение не трогает — он принадлежит окну', () => {
    const onChange = vi.fn();
    const outer = vi.fn();
    // Значение пришло после монтирования — как данные формы, загруженные позже.
    const { rerender } = render(<div onKeyDown={outer}><DateInput label="Дата" value="" onChange={onChange} /></div>);
    rerender(<div onKeyDown={outer}><DateInput label="Дата" value="2026-10-18" onChange={onChange} /></div>);
    fireEvent.keyDown(screen.getByRole('button', { name: 'Открыть календарь' }), { key: 'Escape' });
    expect(onChange).not.toHaveBeenCalled();
    expect(outer).toHaveBeenCalledTimes(1);
  });

  it('выбор в календаре дня, равного значению, снимает набранное с ошибкой', () => {
    render(<Controlled initial="2026-02-19" calendar />);
    fireEvent.focus(seg('ДД'));
    fireEvent.change(seg('ДД'), { target: { value: '31' } });
    expect(seg('ДД').getAttribute('aria-invalid')).toBe('true');
    fireEvent.click(screen.getByRole('button', { name: 'Открыть календарь' }));
    const grid = screen.getByRole('grid', { name: 'Календарь' });
    fireEvent.click(within(grid).getByRole('button', { name: '19' }));
    expect(screen.getByTestId('value').textContent).toBe('2026-02-19');
    expect(seg('ДД').value).toBe('19');
    expect(seg('ДД').getAttribute('aria-invalid')).toBeNull();
  });
});

describe('DateInput: маска по языку пользователя (issue #1461)', () => {
  const placeholders = () => [...document.querySelectorAll('input')].map(i => i.placeholder);
  const separators = () => [...document.querySelectorAll('span.select-none')].map(s => s.textContent);

  it.each([
    ['ru-RU', 'day', ['ДД', 'ММ', 'ГГГГ'], '.'], ['ru-RU', 'month', ['ММ', 'ГГГГ'], '.'], ['ru-RU', 'year', ['ГГГГ'], ''],
    ['en-US', 'day', ['MM', 'DD', 'YYYY'], '/'], ['en-US', 'month', ['MM', 'YYYY'], '/'], ['en-US', 'year', ['YYYY'], ''],
    ['en-GB', 'day', ['DD', 'MM', 'YYYY'], '/'], ['en-GB', 'month', ['MM', 'YYYY'], '/'], ['en-GB', 'year', ['YYYY'], ''],
    ['de-DE', 'day', ['TT', 'MM', 'JJJJ'], '.'], ['de-DE', 'month', ['MM', 'JJJJ'], '.'], ['de-DE', 'year', ['JJJJ'], ''],
  ] as const)('%s, точность %s: сегменты %j через «%s»', (lang, precision, order, sep) => {
    locale = lang;
    render(<DateInput label="Дата" value="" onChange={() => {}} precision={precision} calendar={false} />);
    expect(placeholders()).toEqual(order);
    expect(new Set(separators())).toEqual(new Set(sep ? [sep] : []));
  });

  it.each([['ru-RU'], ['en-US'], ['en-GB'], ['de-DE']])('%s: хранимое значение одно и то же, сегменты — в порядке языка', lang => {
    locale = lang;
    render(<DateInput label="Дата" value="2026-10-18" onChange={() => {}} calendar={false} />);
    const shown = [...document.querySelectorAll('input')].map(i => i.value);
    expect(shown).toEqual(lang === 'en-US' ? ['10', '18', '2026'] : ['18', '10', '2026']);
  });

  it('en-US: ввод 10/18/2026 по сегментам с автопереходом — 2026-10-18', () => {
    locale = 'en-US';
    const onChange = vi.fn();
    render(<Controlled onChange={onChange} />);
    const [first, second, third] = [...document.querySelectorAll('input')];
    fireEvent.focus(first); fireEvent.change(first, { target: { value: '10' } });
    expect(document.activeElement).toBe(second); // за месяцем у en-US идёт день
    fireEvent.change(second, { target: { value: '18' } });
    expect(document.activeElement).toBe(third);
    fireEvent.change(third, { target: { value: '2026' } });
    expect(onChange).toHaveBeenLastCalledWith('2026-10-18');
  });

  it('вставка: «10/18/2026» у en-US, «18.10.2026» у ru-RU; «01.02.2026» — по языку, без угадывания', () => {
    const pasted = (lang: string, text: string) => {
      locale = lang;
      const onChange = vi.fn();
      render(<Controlled onChange={onChange} />);
      fireEvent.paste(document.querySelector('input')!, { clipboardData: { getData: () => text } });
      cleanup();
      return onChange.mock.calls.at(-1)?.[0];
    };
    expect(pasted('en-US', '10/18/2026')).toBe('2026-10-18');
    expect(pasted('ru-RU', '18.10.2026')).toBe('2026-10-18');
    expect(pasted('ru-RU', '01.02.2026')).toBe('2026-02-01');
    expect(pasted('en-US', '01.02.2026')).toBe('2026-01-02');
    expect(pasted('en-US', '2026-10-18')).toBe('2026-10-18'); // ISO — при любом языке
    expect(pasted('en-US', '18/10/2026')).toBeUndefined();    // месяца 18 нет — не дата, не догадка
  });
});

// Цифра в полный сегмент без выделения заменяет его (живые прогоны PR #1544): иначе её съедает
// `maxLength`.
describe('DateInput: цифра в полный сегмент', () => {
  it('без выделения — сегмент выделяется, и цифра его заменит', () => {
    render(<Controlled initial="2026-10-19" />);
    const month = seg('ММ');
    month.focus();
    month.setSelectionRange(2, 2);
    fireEvent.keyDown(month, { key: '0' });
    expect([month.selectionStart, month.selectionEnd]).toEqual([0, 2]);
  });

  it('с выделением и в неполном сегменте — не трогает', () => {
    render(<Controlled initial="2026-10-19" />);
    const month = seg('ММ');
    month.focus();
    month.setSelectionRange(1, 2);
    fireEvent.keyDown(month, { key: '5' });
    expect([month.selectionStart, month.selectionEnd]).toEqual([1, 2]);
    fireEvent.keyDown(month, { key: 'ArrowLeft' });
    expect([month.selectionStart, month.selectionEnd]).toEqual([1, 2]);
  });

  // Настоящая причина «31.2.2026» в CI: выделение после автоперехода ставилось только таймером, и на
  // медленной машине он срабатывал ПОСЛЕ первой цифры месяца — выделял «0», и «2» его заменяла.
  it('автопереход выделяет сразу, а запоздавший таймер не выделяет набранное', () => {
    vi.useFakeTimers();
    render(<Controlled initial="2026-10-19" />);
    const day = seg('ДД');
    const month = seg('ММ');
    day.focus();
    fireEvent.change(day, { target: { value: '3' } });
    fireEvent.change(day, { target: { value: '31' } });
    expect(document.activeElement).toBe(month);
    expect([month.selectionStart, month.selectionEnd]).toEqual([0, 2]);
    fireEvent.change(month, { target: { value: '0' } });
    act(() => { vi.runAllTimers(); });
    expect(month.value).toBe('0');
    expect([month.selectionStart, month.selectionEnd]).toEqual([1, 1]);
  });
});

// Имя поля и сегментов (issue #1463): поле находится по подписи, а не по подсказке «ДД», которой у
// en-US нет; сегменты называются словами языка. Снимешь имя — краснеют и эти тесты, и живые прогоны.
describe('DateInput: имя для чтения с экрана', () => {
  it('DateField: группа названа подписью, сегменты — словами языка', () => {
    render(<Controlled initial="2026-10-19" field />);
    const group = screen.getByRole('group', { name: 'Дата платежа' });
    expect(within(group).getAllByRole('textbox').map(t => t.getAttribute('aria-label'))).toEqual(['день', 'месяц', 'год']);
  });

  it('en-US: сегменты в порядке языка и по-английски', () => {
    locale = 'en-US';
    render(<DateInput label="Payment date" value="2026-10-19" onChange={() => {}} calendar={false} />);
    const group = screen.getByRole('group', { name: 'Payment date' });
    expect((within(group).getByRole('textbox', { name: 'month' }) as HTMLInputElement).value).toBe('10');
    expect(within(group).getAllByRole('textbox').map(t => t.getAttribute('aria-label'))).toEqual(['month', 'day', 'year']);
  });

  it('в ячейке кнопка 32 px занимает в строке место прежнего значка', () => {
    render(<DateInput label="Дата" value="2026-10-19" onChange={() => {}} compact />);
    const classes = screen.getByRole('button', { name: 'Открыть календарь' }).className.split(' ');
    expect(classes).toEqual(expect.arrayContaining(['h-8', 'w-8', '-mr-1.5']));
  });

  it('кнопка календаря — не меньше 32 px', () => {
    render(<Controlled initial="2026-10-19" calendar />);
    expect(screen.getByRole('button', { name: 'Открыть календарь' }).className).toMatch(/\bh-8 w-8\b/);
  });
});

// Стрелки как у нативного поля даты (issue #1463): ↑/↓ — значение на шаг, ←/→ с края — сосед.
describe('DateInput: стрелки', () => {
  const key = (el: HTMLElement, k: string, extra = {}) => fireEvent.keyDown(el, { key: k, ...extra });

  it('↑/↓ меняют день и месяц по кругу и отдают дату', () => {
    const onChange = vi.fn();
    render(<Controlled initial="2026-10-31" onChange={onChange} />);
    key(seg('ДД'), 'ArrowUp');
    expect(seg('ДД').value).toBe('01');
    expect(onChange).toHaveBeenLastCalledWith('2026-10-01');
    key(seg('ДД'), 'ArrowDown');
    expect(seg('ДД').value).toBe('31');
    key(seg('ММ'), 'ArrowUp');
    expect(seg('ММ').value).toBe('11');
    // 31.11 не существует: показано, помечено и наружу не отдано.
    expect(seg('ДД').getAttribute('aria-invalid')).toBe('true');
    expect(screen.getByTestId('value').textContent).toBe('2026-10-31');
    key(seg('ММ'), 'ArrowDown'); key(seg('ММ'), 'ArrowDown');
    expect(seg('ММ').value).toBe('09');
  });

  it('пустой сегмент: ↑ — первый, ↓ — последний; пустой год — год компании', () => {
    vi.useFakeTimers({ toFake: ['Date'] });
    vi.setSystemTime(new Date('2027-03-05T12:00:00Z'));
    render(<Controlled />);
    key(seg('ДД'), 'ArrowUp');
    expect(seg('ДД').value).toBe('01');
    key(seg('ММ'), 'ArrowDown');
    expect(seg('ММ').value).toBe('12');
    key(seg('ГГГГ'), 'ArrowUp');
    expect(seg('ГГГГ').value).toBe('2027');
    expect(screen.getByTestId('value').textContent).toBe('2027-12-01');
  });

  it('день ходит по кругу своего месяца; недописанный год стрелка не трогает; год — в границах календаря', () => {
    const onChange = vi.fn();
    render(<Controlled initial="2026-11-30" onChange={onChange} />);
    key(seg('ДД'), 'ArrowUp');
    expect(seg('ДД').value).toBe('01');
    expect(onChange).toHaveBeenLastCalledWith('2026-11-01');
    key(seg('ДД'), 'ArrowDown');
    expect(seg('ДД').value).toBe('30');
    // Февраль без года — по високосному: 29 не отсекается раньше, чем год известен.
    cleanup();
    render(<Controlled />);
    fireEvent.change(seg('ММ'), { target: { value: '02' } });
    key(seg('ДД'), 'ArrowDown');
    expect(seg('ДД').value).toBe('29');
    fireEvent.change(seg('ГГГГ'), { target: { value: '202' } });
    key(seg('ГГГГ'), 'ArrowUp');
    expect(seg('ГГГГ').value).toBe('202');
    cleanup();
    render(<Controlled initial="2100-01-01" />);
    key(seg('ГГГГ'), 'ArrowUp');
    expect(seg('ГГГГ').value).toBe('2100');
  });

  it('Alt+↓ — календарь, а не шаг', () => {
    render(<Controlled initial="2026-10-19" calendar />);
    key(seg('ДД'), 'ArrowDown', { altKey: true });
    expect(seg('ДД').value).toBe('19');
    expect(screen.getByRole('grid', { name: 'Календарь' })).toBeTruthy();
  });

  it('←/→ с края сегмента — к соседу, посреди — нет', () => {
    render(<Controlled initial="2026-10-19" />);
    const month = seg('ММ');
    month.focus();
    month.setSelectionRange(1, 1);
    key(month, 'ArrowRight');
    expect(document.activeElement).toBe(month);
    month.setSelectionRange(2, 2);
    key(month, 'ArrowRight');
    expect(document.activeElement).toBe(seg('ГГГГ'));
    const year = seg('ГГГГ');
    year.setSelectionRange(0, 0);
    key(year, 'ArrowLeft');
    expect(document.activeElement).toBe(month);
    month.select();
    key(month, 'ArrowLeft');
    expect(document.activeElement).toBe(seg('ДД'));
    key(seg('ДД'), 'ArrowLeft');
    expect(document.activeElement).toBe(seg('ДД'));
  });

  it('у поля только для чтения стрелки значение не меняют', () => {
    render(<DateInput label="Дата" value="2026-10-19" onChange={() => {}} readOnly calendar={false} />);
    key(seg('ДД'), 'ArrowUp');
    expect(seg('ДД').value).toBe('19');
  });
});

// «Очистить» и «Сегодня» (issue #1463, макет «Очистить и Сегодня», вариант В): крестик — ускоритель у
// поля формы, гарантированный путь — строка действий календаря, с клавиатуры — Ctrl+A → Delete.
describe('DateInput: очистка', () => {
  function Form({ initial = '2026-11-03', required = false, compact = false }: { initial?: string; required?: boolean; compact?: boolean }) {
    const [value, setValue] = useState(initial);
    return (
      <div onKeyDown={outerKey}>
        {compact
          ? <DateInput label="Оплатить до" value={value} onChange={setValue} compact />
          : <DateField label="Оплатить до" value={value} onChange={setValue} required={required} hint="срок из договора" />}
        <output data-testid="value">{value}</output>
      </div>
    );
  }
  const outerKey = vi.fn();
  afterEach(() => outerKey.mockReset());
  const value = () => screen.getByTestId('value').textContent;
  const clearButton = () => screen.queryByRole('button', { name: 'Очистить дату' });
  const openCalendar = () => fireEvent.click(screen.getByRole('button', { name: 'Открыть календарь' }));
  const selectAll = (el: HTMLInputElement = seg('ММ')) => { el.focus(); fireEvent.keyDown(el, { key: 'a', code: 'KeyA', ctrlKey: true }); };

  it('крестик стирает дату, фокус — в первый сегмент, Esc возвращает прежнюю', () => {
    render(<Form />);
    fireEvent.click(clearButton()!);
    expect(value()).toBe('');
    expect(document.activeElement).toBe(seg('ДД'));
    expect(clearButton()).toBeNull(); // стирать больше нечего
    fireEvent.keyDown(seg('ДД'), { key: 'Escape' });
    expect(value()).toBe('2026-11-03');
  });

  it('у обязательного и пустого поля крестика нет; у обязательного нет и «Очистить» в календаре', () => {
    render(<Form initial="" />);
    expect(clearButton()).toBeNull();
    cleanup();
    render(<Form required />);
    expect(clearButton()).toBeNull();
    openCalendar();
    expect(screen.queryByRole('button', { name: 'Очистить' })).toBeNull();
    expect(screen.getByRole('button', { name: /^Сегодня, / })).toBeTruthy();
  });

  it('недописанное поверх пустого стирается крестиком — вместе с ошибкой', () => {
    vi.useFakeTimers();
    render(<Form initial="" />);
    fireEvent.focus(seg('ДД'));
    fireEvent.change(seg('ДД'), { target: { value: '18' } });
    leave(seg('ММ'));
    expect(screen.getByText('Дата не дописана. Срок из договора')).toBeTruthy();
    fireEvent.click(clearButton()!);
    act(() => { vi.advanceTimersByTime(200); });
    expect(seg('ДД').value).toBe('');
    expect(screen.queryByText(/Дата не дописана/)).toBeNull();
    expect(screen.getByText('срок из договора')).toBeTruthy();
  });

  it('«не дата» стирается крестиком', () => {
    vi.useFakeTimers();
    render(<Form initial="вчера" />);
    fireEvent.click(clearButton()!);
    act(() => { vi.runAllTimers(); });
    expect(value()).toBe('');
    expect(document.activeElement).toBe(seg('ДД'));
  });

  it('в ячейке крестика нет — «Очистить» в календаре, фокус в первый сегмент', () => {
    render(<Form compact />);
    expect(clearButton()).toBeNull();
    openCalendar();
    fireEvent.click(screen.getByRole('button', { name: 'Очистить' }));
    expect(value()).toBe('');
    expect(document.activeElement).toBe(seg('ДД'));
    expect(screen.queryByRole('grid')).toBeNull();
  });

  it('«Сегодня» в календаре ставит день компании', () => {
    vi.useFakeTimers({ toFake: ['Date'] });
    vi.setSystemTime(new Date(2026, 9, 21, 12));
    render(<Form compact />);
    openCalendar();
    fireEvent.click(screen.getByRole('button', { name: /^Сегодня, / }));
    expect(value()).toBe('2026-10-21');
  });

  it('Ctrl+A → Delete стирает и у обязательного поля; фокус — в первый сегмент', () => {
    render(<Form required />);
    selectAll();
    expect(screen.getByText('Выделена вся дата')).toBeTruthy();
    fireEvent.keyDown(seg('ММ'), { key: 'Delete' });
    expect(value()).toBe('');
    expect(document.activeElement).toBe(seg('ДД'));
    expect(screen.queryByText('Выделена вся дата')).toBeNull();
  });

  it('Ctrl+A → цифра начинает дату заново с первого сегмента; прежняя держится до новой, Esc её возвращает', () => {
    render(<Form />);
    selectAll(seg('ГГГГ'));
    fireEvent.keyDown(seg('ГГГГ'), { key: '2' });
    expect(seg('ДД').value).toBe('2');
    expect(seg('ММ').value).toBe('');
    expect(document.activeElement).toBe(seg('ДД'));
    // Посреди набора форма пустоты не получает: значение — прежнее, пока новая дата не сложится.
    expect(value()).toBe('2026-11-03');
    fireEvent.keyDown(seg('ДД'), { key: 'Escape' });
    expect(seg('ДД').value).toBe('03');
    expect(value()).toBe('2026-11-03');
  });

  it('Ctrl+A → цифра 4–9 дополняет день нулём и уводит дальше, как обычный набор', () => {
    render(<Form />);
    selectAll();
    fireEvent.keyDown(seg('ММ'), { key: '5' });
    expect(seg('ДД').value).toBe('05');
    expect(document.activeElement).toBe(seg('ММ'));
    expect(seg('ММ').value).toBe('');
  });

  it('Ctrl+A в пустом поле ничего не выделяет', () => {
    render(<Form initial="" />);
    selectAll();
    expect(screen.queryByText('Выделена вся дата')).toBeNull();
    expect(seg('ММ').className.split(' ')).not.toContain('bg-brand-subtle'); // `focus:` — не в счёт
  });

  it('Ctrl+A → Ctrl+X: дата в буфер, поле стёрто целиком', () => {
    render(<Form />);
    selectAll();
    const setData = vi.fn();
    fireEvent.keyDown(seg('ММ'), { key: 'x', code: 'KeyX', ctrlKey: true });
    expect(screen.getByText('Выделена вся дата')).toBeTruthy(); // Ctrl+X выделения не снимает
    fireEvent.cut(seg('ММ'), { clipboardData: { setData } });
    expect(setData).toHaveBeenCalledWith('text/plain', '03.11.2026');
    expect(value()).toBe('');
    expect(document.activeElement).toBe(seg('ДД'));
  });

  it('Ctrl+A → Ctrl+C у недописанной даты не подменяет копирование браузера', () => {
    vi.useFakeTimers();
    render(<Form initial="" />);
    fireEvent.focus(seg('ДД'));
    fireEvent.change(seg('ДД'), { target: { value: '18' } });
    selectAll();
    const setData = vi.fn();
    fireEvent.copy(seg('ММ'), { clipboardData: { setData } });
    expect(setData).not.toHaveBeenCalled();
  });

  it('Ctrl+A → Esc снимает выделение, дату не трогает, окну Esc не уходит', () => {
    render(<Form />);
    selectAll();
    fireEvent.keyDown(seg('ММ'), { key: 'Escape' });
    expect(screen.queryByText('Выделена вся дата')).toBeNull();
    expect(value()).toBe('2026-11-03');
    expect(outerKey).not.toHaveBeenCalledWith(expect.objectContaining({ key: 'Escape' }));
    // Второй Esc — уже окну: правки не было.
    fireEvent.keyDown(seg('ММ'), { key: 'Escape' });
    expect(outerKey).toHaveBeenCalledTimes(2); // Ctrl+A и этот Esc; первый Esc погашен
  });

  it('Ctrl+A → Ctrl+C копирует дату в маске языка', () => {
    render(<Form />);
    selectAll();
    const setData = vi.fn();
    fireEvent.keyDown(seg('ММ'), { key: 'Control', ctrlKey: true });
    fireEvent.copy(seg('ММ'), { clipboardData: { setData } });
    expect(setData).toHaveBeenCalledWith('text/plain', '03.11.2026');
  });
});
