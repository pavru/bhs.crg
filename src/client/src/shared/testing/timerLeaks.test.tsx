// @vitest-environment jsdom
import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, render } from '@testing-library/react';
import { useEffect } from 'react';
import { useTimeouts } from '@/shared/hooks/useTimeouts';
import { pageTimers } from '@/shared/utils/pageTimers';
import { takeLeakedTimers, timerGuardOn } from './timerLeaks';

/**
 * Сторож таймеров обязан срабатывать: отключись он (выпал из `setupFiles`, перестал узнавать наши кадры
 * стека), утечки снова поплыли бы в CI «Unhandled Errors» при зелёных тестах.
 */
afterEach(cleanup);

function Bare() {
  useEffect(() => { setTimeout(() => {}, 120); }, []);
  return null;
}

function Owned() {
  const timers = useTimeouts();
  useEffect(() => { timers.set(() => {}, 120); }, [timers]);
  return null;
}

describe('сторож таймеров, переживших тест', () => {
  it('подключён в окружении jsdom', () => {
    expect(timerGuardOn()).toBe(true);
  });

  it('голый таймер, переживший компонент, назван местом, где поставлен', () => {
    render(<Bare />).unmount();
    const leaked = takeLeakedTimers();
    expect(leaked).toHaveLength(1);
    expect(leaked[0]).toMatch(/^setTimeout .*timerLeaks\.test\.tsx/);
    // Найденный снят: второй раз его нет.
    expect(takeLeakedTimers()).toEqual([]);
  });

  it('таймер через useTimeouts снимается при размонтировании — утечки нет', () => {
    render(<Owned />).unmount();
    expect(takeLeakedTimers()).toEqual([]);
  });

  it('пока компонент на месте, его таймер через useTimeouts виден сторожу местом вызова, а не помощником', () => {
    render(<Owned />);
    const leaked = takeLeakedTimers();
    expect(leaked).toHaveLength(1);
    expect(leaked[0]).toMatch(/timerLeaks\.test\.tsx/);
    expect(leaked[0]).not.toMatch(/useTimeouts/);
  });

  it('таймер уровня страницы (pageTimers) утечкой не считается', () => {
    const page = pageTimers();
    page.set(() => {}, 120);
    expect(takeLeakedTimers()).toEqual([]);
    page.clearAll();
  });

  it('сработавший и снятый таймеры не числятся', async () => {
    await new Promise(resolve => setTimeout(resolve, 0));
    clearTimeout(setTimeout(() => {}, 120));
    expect(takeLeakedTimers()).toEqual([]);
  });
});
