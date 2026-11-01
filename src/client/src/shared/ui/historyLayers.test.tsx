// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { StrictMode, useState } from 'react';
import { act, cleanup, render } from '@testing-library/react';
import { resetHistoryLayers, useHistoryLayer } from './historyLayers';
import { Modal } from './Modal';
import { WindowStep } from './WindowStep';
// Слушатель `popstate` — общий с охраной шага: без него слои не слышат «Назад».
import './NavigationGuard';

/**
 * «Назад» закрывает верхний слой (issue #1418). Роутер здесь — слушатель `popstate` после нашего:
 * что дошло до него, увидел бы и роутер.
 */

const routerSaw: unknown[] = [];
const router = (e: PopStateEvent) => routerSaw.push(e.state);
/** jsdom ходит по истории не сразу — как браузер. */
const settle = () => act(() => new Promise(r => setTimeout(r, 30)));
const state = () => window.history.state as { idx?: number; layer?: string; depth?: number } | null;

function mockWidth(wide: boolean) {
  // Ширину спрашивают двумя вопросами — «шире 768» (WIDE) и «уже 768» (NARROW), — и ответ у них обратный.
  window.matchMedia = vi.fn().mockImplementation((q: string) => ({
    matches: q.includes('min-width') ? wide : !wide, addEventListener() {}, removeEventListener() {},
  })) as unknown as typeof window.matchMedia;
}

function Layer({ onBack, open = true }: { onBack: () => boolean; open?: boolean }) {
  useHistoryLayer(open, onBack);
  return null;
}

beforeEach(async () => {
  resetHistoryLayers();
  mockWidth(false);
  window.history.replaceState({ idx: 0, key: 'base' }, '');
  routerSaw.length = 0;
  window.addEventListener('popstate', router);
});

afterEach(() => {
  cleanup();
  window.removeEventListener('popstate', router);
});

describe('слой истории на телефоне', () => {
  it('открытый слой кладёт запись; «Назад» снимает её и закрывает слой — роутер шага не видит', async () => {
    const onBack = vi.fn(() => true);
    render(<Layer onBack={onBack} />);
    await settle();
    expect(state()).toMatchObject({ idx: 0, key: 'base', depth: 1 });

    window.history.back();
    await settle();
    expect(onBack).toHaveBeenCalledTimes(1);
    expect(state()?.layer).toBeUndefined();
    expect(routerSaw).toEqual([]);
  });

  it('слой остался открытым (спросил о правках) — запись кладётся заново', async () => {
    const onBack = vi.fn(() => false);
    render(<Layer onBack={onBack} />);
    await settle();
    window.history.back();
    await settle();
    expect(onBack).toHaveBeenCalledTimes(1);
    expect(state()?.layer).toBeDefined();
    // И следующая «Назад» снова у слоя, а не у страницы.
    window.history.back();
    await settle();
    expect(onBack).toHaveBeenCalledTimes(2);
    expect(routerSaw).toEqual([]);
  });

  it('закрыли своими средствами — запись снимается нашим шагом, которого не видит никто', async () => {
    function Host() {
      const [open, setOpen] = useState(true);
      return <><Layer open={open} onBack={() => true} /><button onClick={() => setOpen(false)}>x</button></>;
    }
    const view = render(<Host />);
    await settle();
    expect(state()?.layer).toBeDefined();
    act(() => view.getByText('x').click());
    await settle();
    expect(state()?.layer).toBeUndefined();
    expect(routerSaw).toEqual([]);
  });

  it('два слоя — «Назад» закрывает только верхний', async () => {
    const lower = vi.fn(() => true);
    const upper = vi.fn(() => true);
    function Host() {
      const [top, setTop] = useState(true);
      return <><Layer onBack={lower} /><Layer open={top} onBack={() => { upper(); setTop(false); return true; }} /></>;
    }
    render(<Host />);
    await settle();
    expect(state()?.depth).toBe(2);
    window.history.back();
    await settle();
    expect(upper).toHaveBeenCalledTimes(1);
    expect(lower).not.toHaveBeenCalled();
    expect(state()?.depth).toBe(1);
  });

  it('StrictMode — одна запись, а не две', async () => {
    const onBack = vi.fn(() => true);
    render(<StrictMode><Layer onBack={onBack} /></StrictMode>);
    await settle();
    expect(state()?.depth).toBe(1);
    // Одна «Назад» — и мы на записи страницы: второй записи под ней нет.
    window.history.back();
    await settle();
    expect(onBack).toHaveBeenCalledTimes(1);
    expect(state()).toEqual({ idx: 0, key: 'base' });
  });

  it('слой закрыл переход (пункт меню) — его запись «Назад» проходит насквозь', async () => {
    function Host() {
      const [open, setOpen] = useState(true);
      return <><Layer open={open} onBack={() => true} /><button onClick={() => {
        // Как ссылка в меню: роутер кладёт новую страницу, меню закрывается.
        window.history.pushState({ idx: 1, key: 'next' }, '');
        setOpen(false);
      }}>go</button></>;
    }
    const view = render(<Host />);
    await settle();
    act(() => view.getByText('go').click());
    await settle();
    expect(state()).toEqual({ idx: 1, key: 'next' });

    window.history.back();
    await settle();
    await settle();
    // Пришли на запись страницы, а не на мёртвую запись меню над ней.
    expect(state()).toEqual({ idx: 0, key: 'base' });
  });
});

describe('на рабочем столе', () => {
  it('слой записей не кладёт — «Назад» достаётся странице', async () => {
    mockWidth(true);
    render(<Layer onBack={() => true} />);
    await settle();
    expect(state()).toEqual({ idx: 0, key: 'base' });
  });
});

describe('окно и его шаг на телефоне', () => {
  function Host({ onClose, isDirty = false }: { onClose: () => void; isDirty?: boolean }) {
    const [step, setStep] = useState(false);
    return (
      <Modal open onOpenChange={o => { if (!o) onClose(); }} title="Строка" isDirty={isDirty}>
        <button type="button" onClick={() => setStep(true)}>Вынести</button>
        {step && <WindowStep title="Вынос" onBack={() => setStep(false)}><p>тело шага</p></WindowStep>}
      </Modal>
    );
  }

  it('«Назад» с шага — к форме окна, следующая — закрывает окно', async () => {
    const onClose = vi.fn();
    const view = render(<Host onClose={onClose} />);
    await settle();
    act(() => view.getByText('Вынести').click());
    expect(view.queryByText('тело шага')).not.toBeNull();

    window.history.back();
    await settle();
    expect(view.queryByText('тело шага')).toBeNull();
    expect(onClose).not.toHaveBeenCalled();
    expect(state()?.layer).toBeDefined();

    window.history.back();
    await settle();
    expect(onClose).toHaveBeenCalledTimes(1);
    expect(routerSaw).toEqual([]);
  });

  it('окно с вводом спрашивает, как крестик; «Назад» при вопросе — «Продолжить редактирование»', async () => {
    const onClose = vi.fn();
    const view = render(<Host onClose={onClose} isDirty />);
    await settle();
    window.history.back();
    await settle();
    expect(view.queryByText('Закрыть без сохранения?')).not.toBeNull();
    window.history.back();
    await settle();
    expect(view.queryByText('Закрыть без сохранения?')).toBeNull();
    expect(onClose).not.toHaveBeenCalled();
    expect(state()?.layer).toBeDefined();
  });
});
