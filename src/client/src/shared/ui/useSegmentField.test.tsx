// @vitest-environment jsdom
import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { useState } from 'react';
import { useSegmentField, type SegmentModel } from './useSegmentField';

/**
 * Движок сегментов сам по себе, без даты (ревью PR #1560). Тесты даты проверяют только то, что задевает
 * дата; здесь — ветки, которые она не трогает, а поле времени задевает: сегмент без `padAbove`,
 * стрелка, которой модель отказала (`step` → null), чужой разделитель, другая длина сегмента.
 *
 * <p>Модель игрушечная нарочно: «AA-BBB», у AA — `padAbove` и шаг стрелкой, у BBB — ни того, ни
 * другого. Проверка через живую модель времени смешала бы движок с её правилами.</p>
 */

type Part = 'a' | 'b';
const EMPTY = { a: '', b: '' };
const read: SegmentModel<Part>['read'] = ({ a, b }) => {
  if (a === '' && b === '') return { kind: 'empty' };
  if (a.length < 2 || b.length < 3) return { kind: 'partial' };
  if (+a > 23) return { kind: 'impossible' };
  return { kind: 'value', value: `${a}-${b}` };
};
const MODEL: SegmentModel<Part> = {
  order: ['a', 'b'], sep: '-', labels: { a: 'AA', b: 'BBB' }, names: { a: 'первый', b: 'второй' },
  len: { a: 2, b: 3 }, padAbove: { a: 2 }, separators: [':'], empty: EMPTY,
  of: v => {
    if (!v) return EMPTY;
    const t = /^(\d{2})-(\d{3})$/.exec(v);
    return t ? { a: t[1], b: t[2] } : null;
  },
  read,
  fix: (part, typed) => part === 'a' && typed.length === 1 ? '0' + typed : null,
  step: (part, delta, s) => part === 'a' ? String(((+s.a || 0) + delta + 24) % 24).padStart(2, '0') : null,
  paste: text => { const t = /^(\d{2})-(\d{3})$/.exec(text.trim()); return t ? { a: t[1], b: t[2] } : null; },
  text: s => read(s).kind === 'value' ? `${s.a}-${s.b}` : null,
  problems: { partial: 'не дописано', impossible: 'такого нет', foreign: 'не то' },
  selectedAll: 'выделено всё',
};

function Field({ initial = '' }: { initial?: string }) {
  const [value, setValue] = useState(initial);
  const { groupProps, segments, problem } = useSegmentField(MODEL, {
    label: 'Поле', value, onChange: setValue, disabled: false, readOnly: false, required: false, invalid: false,
  });
  return (
    <>
      <div {...groupProps}>{segments}</div>
      <output data-testid="value">{value}</output>
      <output data-testid="problem">{problem ?? ''}</output>
    </>
  );
}

const seg = (name: string) => screen.getByLabelText(name) as HTMLInputElement;
const value = () => screen.getByTestId('value').textContent;

afterEach(cleanup);

describe('движок сегментов на модели не-даты', () => {
  it('сегмент без padAbove ждёт всех цифр: «9» остаётся «9», а не «09» с переходом', () => {
    render(<Field />);
    seg('второй').focus();
    fireEvent.change(seg('второй'), { target: { value: '9' } });
    expect(seg('второй').value).toBe('9');
    expect(document.activeElement).toBe(seg('второй'));
  });

  it('padAbove: цифра выше порога — «0X» и переход к следующему сегменту', () => {
    render(<Field />);
    fireEvent.focus(seg('первый'));
    fireEvent.change(seg('первый'), { target: { value: '5' } });
    expect(seg('первый').value).toBe('05');
    expect(document.activeElement).toBe(seg('второй'));
  });

  it('длина сегмента — из модели: три цифры во втором дают значение', () => {
    render(<Field />);
    fireEvent.change(seg('первый'), { target: { value: '12' } });
    fireEvent.change(seg('второй'), { target: { value: '345' } });
    expect(value()).toBe('12-345');
  });

  it('разделитель модели переводит дальше, а уход дописывает сегмент правилом модели', () => {
    render(<Field />);
    fireEvent.focus(seg('первый'));
    fireEvent.change(seg('первый'), { target: { value: '1' } });
    fireEvent.keyDown(seg('первый'), { key: ':' });
    expect(document.activeElement).toBe(seg('второй'));
    fireEvent.blur(seg('первый'));
    expect(seg('первый').value).toBe('01');
  });

  it('стрелка, которой модель отказала, сегмент не трогает; разрешённая шагает', () => {
    render(<Field initial="22-345" />);
    fireEvent.keyDown(seg('второй'), { key: 'ArrowUp' });
    expect(seg('второй').value).toBe('345');
    fireEvent.keyDown(seg('первый'), { key: 'ArrowUp' });
    fireEvent.keyDown(seg('первый'), { key: 'ArrowUp' });
    expect(value()).toBe('00-345');
  });

  it('вставка целиком — по правилу модели; несуществующее не уходит наружу', () => {
    render(<Field />);
    fireEvent.paste(seg('второй'), { clipboardData: { getData: () => '07-123' } });
    expect(value()).toBe('07-123');
    fireEvent.paste(seg('первый'), { clipboardData: { getData: () => '30-123' } });
    expect(value()).toBe('07-123');
    expect(screen.getByTestId('problem').textContent).toBe('такого нет');
  });
});
