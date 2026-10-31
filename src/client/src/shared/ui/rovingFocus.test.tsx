// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { useRoving } from './rovingFocus';

afterEach(cleanup);

const VALUES = ['a', 'b', 'c', 'd'] as const;

function Row({ value, select, off = [] }: { value: string | undefined; select: false | ((v: string) => void); off?: string[] }) {
  const roving = useRoving(VALUES, { value, select, disabled: v => off.includes(v) });
  return (
    <div onKeyDown={roving.onKeyDown}>
      {VALUES.map((v, i) => <button key={v} type="button" {...roving.item(i)}>{v}</button>)}
    </div>
  );
}

const btn = (name: string) => screen.getByRole('button', { name });

describe('useRoving', () => {
  it('одна остановка Tab — выбранный; значение вне списка — первый доступный', () => {
    const { rerender } = render(<Row value="b" select={false} />);
    expect(VALUES.map(v => btn(v).tabIndex)).toEqual([-1, 0, -1, -1]);
    rerender(<Row value="zz" select={false} off={['a']} />);
    expect(VALUES.map(v => btn(v).tabIndex)).toEqual([-1, 0, -1, -1]);
  });

  it('стрелка ведёт к соседнему мимо отключённого, по кругу; Home и End — крайние', () => {
    render(<Row value="a" select={false} off={['b']} />);
    btn('a').focus();
    fireEvent.keyDown(btn('a'), { key: 'ArrowRight' });
    expect(document.activeElement).toBe(btn('c'));
    fireEvent.keyDown(btn('c'), { key: 'End' });
    expect(document.activeElement).toBe(btn('d'));
    fireEvent.keyDown(btn('d'), { key: 'ArrowRight' });
    expect(document.activeElement).toBe(btn('a'));
    fireEvent.keyDown(btn('a'), { key: 'ArrowLeft' });
    expect(document.activeElement).toBe(btn('d'));
    fireEvent.keyDown(btn('d'), { key: 'Home' });
    expect(document.activeElement).toBe(btn('a'));
  });

  it('select: false не выбирает; с select стрелка выбирает соседний', () => {
    const pick = vi.fn();
    const { rerender } = render(<Row value="a" select={false} />);
    btn('a').focus();
    fireEvent.keyDown(btn('a'), { key: 'ArrowDown' });
    expect(document.activeElement).toBe(btn('b'));
    rerender(<Row value="a" select={pick} />);
    btn('a').focus();
    fireEvent.keyDown(btn('a'), { key: 'ArrowRight' });
    expect(pick).toHaveBeenCalledWith('b');
  });
});
