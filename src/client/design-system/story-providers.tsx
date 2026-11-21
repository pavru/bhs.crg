import { useEffect, useState, type ReactNode } from 'react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router';
import { AuthContext } from '@/shared/hooks/useAuth';
import { LocaleContext } from '@/shared/hooks/useLocale';
import { Ctx as ThemeContext, type Theme } from '@/shared/ui/themeContext';
import { WorkspaceContext, type WorkspaceContextValue } from '@/shared/ui/workspaceContext';
import { SiteFilterContext, type SiteFilterValue } from '@/shared/siteFilter/siteFilterContext';
import { PinnedPagesContext } from '@/shared/ui/pinnedPagesContext';
import { SnackbarProvider } from '@/shared/ui/SnackbarProvider';
import { ModalControlContext } from '@/shared/ui/modalControl';
import { access } from './story-api';

const noop = () => {};
const nothing = async () => {};
const all = { key: 'all', kind: 'all' as const, name: 'Все разделы', modules: ['costs', 'id', 'plan', 'work'] };
const workspace: WorkspaceContextValue = {
  shown: true, current: all, list: [all, { ...all, key: 'id', kind: 'module', name: 'Документы', modules: ['id'] }], own: [], defaultChoice: null,
  choose: noop, updateOwn: nothing, setDefault: nothing, shows: () => true, showsSection: () => true, inside: () => true,
};
const sites: SiteFilterValue = {
  status: 'ready', favorites: new Set(['site-1']), on: true, active: true,
  sites: [{ id: 'site-1', name: 'Корпус 1' }, { id: 'site-2', name: 'Корпус 2' }],
  visible: () => true, setOn: noop, setFavorite: nothing, setFavorites: nothing,
};

export function StoryProviders({ children, theme, component }: { children: ReactNode; theme: 'light' | 'dark'; component: string }) {
  const [localTheme, setLocalTheme] = useState<Theme>(theme);
  const [workspaceKey, setWorkspaceKey] = useState(['OutsideBanner', 'HiddenByWorkspace'].includes(component) ? 'id' : 'all');
  const [favorites, setFavorites] = useState(new Set(['site-1']));
  const [filterOn, setFilterOn] = useState(true);
  const [pinned, setPinned] = useState<string[]>([]);
  const [collapsed, setCollapsed] = useState(false);
  const resolvedTheme = localTheme === 'system' ? matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light' : localTheme;
  useEffect(() => { document.documentElement.dataset.theme = resolvedTheme; }, [resolvedTheme]);
  const [client] = useState(() => {
    const c = new QueryClient({ defaultOptions: { queries: { retry: false, staleTime: Infinity, refetchOnWindowFocus: false } } });
    c.setQueryData(['account', 'access'], access);
    c.setQueryData(['account', 'settings'], { locale: 'ru-RU', theme });
    c.setQueryData(['branding'], { productName: 'BHS.CRG', hasLogo: false, icons: { state: 'standard', version: '', problem: null, aspect: null } });
    return c;
  });
  const current = workspace.list.find(w => w.key === workspaceKey) ?? all;
  const scoped: WorkspaceContextValue = { ...workspace, current, choose: setWorkspaceKey, shows: module => current.kind === 'all' || current.modules.includes(module), inside: item => current.kind === 'all' || !!item.module && current.modules.includes(item.module) };
  const siteValue: SiteFilterValue = { ...sites, favorites, on: filterOn, active: filterOn && favorites.size > 0, setOn: setFilterOn, setFavorite: async (id, selected) => setFavorites(previous => { const next = new Set(previous); if (selected) next.add(id); else next.delete(id); return next; }), setFavorites: async ids => setFavorites(new Set(ids)) };
  return <QueryClientProvider client={client}><MemoryRouter initialEntries={component === 'OutsideBanner' ? ['/costs/invoices'] : ['/']}>
    <AuthContext.Provider value={{ user: { sub: 'example', email: 'alex@example.test', displayName: 'Александр Потоцкий' }, login: nothing, updateSession: noop, logout: noop }}>
      <LocaleContext.Provider value={['ru-RU', noop, 'idle']}><ThemeContext.Provider value={{ theme: localTheme, resolvedTheme, setTheme: setLocalTheme, saveState: 'idle' }}>
        <WorkspaceContext.Provider value={scoped}><SiteFilterContext.Provider value={siteValue}>
          <PinnedPagesContext.Provider value={{ list: pinned, has: to => pinned.includes(to), pin: to => setPinned(previous => [...new Set([...previous, to])]), unpin: to => setPinned(previous => previous.filter(p => p !== to)), move: (from, to) => setPinned(previous => { const next = [...previous]; const [item] = next.splice(from, 1); next.splice(to, 0, item); return next; }), collapsed, setCollapsed }}>
            <SnackbarProvider><ModalControlContext.Provider value={{ requestClose: noop, busy: false }}>{children}</ModalControlContext.Provider></SnackbarProvider>
          </PinnedPagesContext.Provider>
        </SiteFilterContext.Provider></WorkspaceContext.Provider>
      </ThemeContext.Provider></LocaleContext.Provider>
    </AuthContext.Provider>
  </MemoryRouter></QueryClientProvider>;
}
