import { apiClient } from '@/shared/api/client';
import { workNav, settingsNav } from '@/shared/ui/navConfig';

export const apiCalls: string[] = [];
const permissions = [...new Set([...workNav, ...settingsNav].flatMap(item => item.permission ? [item.permission] : []))];
export const access = {
  permissions: [...permissions, 'core.users.manage', 'core.types.edit', 'core.system.manage', 'id.config.edit'],
  modules: ['id', 'costs', 'plan', 'work'].map(code => ({ code, title: code, enabled: true, available: true })),
  accounting: { currency: 'RUB' }, company: { timeZone: 'Asia/Vladivostok' }, employee: null,
};
export function mockApi(path: string, method: string) {
  apiCalls.push(`${method.toUpperCase()} ${path}`);
  if (path.includes('/branding')) return { productName: 'BHS.CRG', isCustom: false, hasLogo: false, logoVersion: null, icons: { state: 'standard', version: '', problem: null, aspect: null } };
  if (path.includes('/account/access')) return access;
  if (path.includes('/account/settings')) return { locale: 'ru-RU', theme: 'light' };
  if (path.includes('/account')) return { displayName: 'Александр Потоцкий', email: 'alex@example.test', emailConfirmed: true, roles: [], permissions };
  if (/\/version/.test(path)) return { version: 'Каталог UI', build: 'local' };
  if (/active|favorites|users|recipients|employees|suggestions|notifications|search/.test(path)) return [];
  if (method.toLowerCase() !== 'get') return { id: 'example', version: '1', message: 'Действие выполнено в каталоге' };
  return [];
}

/** The story iframe never sends API traffic to an application server. */
export function installStoryApi() {
  apiClient.defaults.adapter = async config => ({
    config, status: 200, statusText: 'OK', headers: {},
    data: mockApi(config.url ?? '', config.method ?? 'get'),
  });
  const nativeFetch = window.fetch.bind(window);
  window.fetch = (input, init) => {
    const url = new URL(typeof input === 'string' ? input : input instanceof URL ? input.href : input.url, location.href);
    if (url.pathname.startsWith('/api/')) {
      return Promise.resolve(new Response(JSON.stringify(mockApi(url.pathname.slice(4), init?.method ?? 'get')), { headers: { 'Content-Type': 'application/json' } }));
    }
    return nativeFetch(input, init);
  };
}
