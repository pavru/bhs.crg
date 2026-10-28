// @vitest-environment jsdom
import type { ReactNode } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router';
import { Assignment, Monitoring } from '@/shared/ui/icons';
import type { AccessInfo } from '@/shared/api/access';
import { constructionPanels, type ConstructionPanel } from '@/shared/ui/constructionPanels';
import { WorkspaceContext, useWorkspace } from '@/shared/ui/workspaceContext';
import { panelViews, useScopePanels } from './scopePanels';

let access: AccessInfo | undefined;
vi.mock('@/shared/api/access', async importOriginal => ({
  ...await importOriginal<typeof import('@/shared/api/access')>(),
  useAccess: () => ({ data: access, isPending: access === undefined }),
}));

afterEach(cleanup);

// Панели блока «Работы» — тестовые: настоящие вписывают plan-25 («Ход работ») и plan-09 («Сметы»).
const progress: ConstructionPanel = {
  id: 'progress', label: 'Ход работ', icon: Monitoring, block: 'work', levels: ['construction'],
  section: 'progress', permission: 'costs.report.read',
};
const estimates: ConstructionPanel = {
  id: 'estimates', label: 'Сметы', icon: Assignment, block: 'work', levels: ['construction'],
  module: 'plan', permission: 'plan.estimate.read',
};
const registry = [progress, estimates, ...constructionPanels.filter(p => p.id === 'catalog')];
const views = {
  progress: () => <p>содержимое хода работ</p>,
  estimates: () => <p>содержимое смет</p>,
  catalog: () => <p>содержимое каталога</p>,
};

function Probe() {
  const panels = useScopePanels('construction', '/c/c1', { constructionId: 'c1' }, registry, views);
  return <><nav aria-label="панели">{panels.workNav}{panels.ownNav('Эта стройка')}</nav><main>{panels.detail}</main></>;
}

function Address() {
  return <output aria-label="адрес">{useLocation().pathname}</output>;
}

/** Пространство без модулей: панели модулей в нём «вне пространства». */
function WithoutModules({ children }: { children: ReactNode }) {
  const ws = useWorkspace();
  return <WorkspaceContext.Provider value={{ ...ws, inside: p => !p.module }}>{children}</WorkspaceContext.Provider>;
}

function open(url: string, who: AccessInfo | undefined, narrow = false) {
  access = who;
  const routes = (
    <Routes>
      <Route path="/c/:constructionId" element={<Probe />} />
      <Route path="/c/:constructionId/:panel/*" element={<Probe />} />
    </Routes>
  );
  render(
    <MemoryRouter initialEntries={[url]}>
      {narrow ? <WithoutModules>{routes}</WithoutModules> : routes}
      <Address />
    </MemoryRouter>,
  );
}

const address = () => screen.getByLabelText('адрес').textContent;
const navText = () => screen.getByRole('navigation', { name: 'панели' }).textContent;
const modules = [{ code: 'plan', title: 'Планирование', available: true }];

/** Панели карточки стройки в странице (plan-02, ТЗ AUTH-16.1, AUTH-15). */
describe('панели страницы стройки', () => {
  it('голый адрес — первая видимая панель блока «Работы»', () => {
    open('/c/c1', { permissions: ['costs.report.read'], modules: [] });
    expect(address()).toBe('/c/c1/progress');
    expect(screen.getByText('содержимое хода работ')).toBeTruthy();
  });

  it('голый адрес без панелей «Работ» — «Каталог»', () => {
    open('/c/c1', { permissions: [], modules: [] });
    expect(address()).toBe('/c/c1/catalog');
  });

  it('пока доступ в пути — без перенаправления, но пункты без прав уже на месте', () => {
    open('/c/c1', undefined);
    expect(address()).toBe('/c/c1');
    expect(navText()).toContain('Каталог');
    expect(navText()).not.toContain('Ход работ');
  });

  it('панель без права по прямой ссылке — страница «недоступно» с названием права', () => {
    open('/c/c1/estimates', { permissions: [], modules });
    expect(address()).toBe('/c/c1/estimates');
    expect(screen.getByRole('heading', { name: 'Раздел «Сметы» недоступен' })).toBeTruthy();
    expect(navText()).not.toContain('Сметы');
  });

  it('панель модуля вне пространства по ссылке — в навигации с пометкой, без перенаправления', () => {
    open('/c/c1/estimates', { permissions: ['plan.estimate.read'], modules }, true);
    expect(address()).toBe('/c/c1/estimates');
    expect(screen.getByText('содержимое смет')).toBeTruthy();
    expect(screen.getByRole('button', { name: /Сметы/ }).textContent).toContain('вне пространства');
  });

  it('вне пространства панель модуля в навигацию без ссылки не попадает', () => {
    open('/c/c1/catalog', { permissions: ['plan.estimate.read'], modules }, true);
    expect(navText()).not.toContain('Сметы');
  });
});

/**
 * Реестр панелей (`constructionPanels.ts`) описывает панель, а рисует её карта `panelViews` (plan-02).
 * Запись без содержимого открывалась бы пустой карточкой, содержимое без записи — недостижимо.
 */
describe('содержимое панелей карточки стройки', () => {
  it('у каждой панели реестра есть содержимое, и лишнего нет', () => {
    expect(Object.keys(panelViews).sort()).toEqual(constructionPanels.map(p => p.id).sort());
  });
});
