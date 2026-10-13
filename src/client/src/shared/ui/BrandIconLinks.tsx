import { useEffect, useReducer } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { BRANDING_KEY, useBranding } from '@/shared/api/branding';
import { BRAND_ICONS_KEY, applyBrandIcons } from './brandIcons';

/**
 * Держит значок вкладки на адресе с действующей меткой версии (issue #1315) — почему это дело
 * приложения, а не одного сервера, см. `brandIcons.ts`.
 *
 * Пока оформление не получено, ссылки не трогаем: там стоит то, что поставил ранний скрипт по
 * метке прошлого захода, либо адрес без метки из `index.html`.
 */
export function BrandIconLinks() {
  // Выход из системы выбрасывает весь кеш ответов (AuthProvider), а наблюдатель запроса переходит на
  // новый запрос только при перерисовке своего компонента. Этот компонент создан в App и сам не
  // перерисовывается никогда — ни пропсов, ни меняющегося контекста у него нет. Без этой подписки
  // он остался бы на удалённом запросе: во вкладке, где хоть раз выходили, новая метка значков не
  // дошла бы до ссылки до перезагрузки страницы, хотя шапка логотип уже сменила (проверка правок
  // перед PR). Подписка на сам кеш, а не на сессию: причина — удаление запроса, каким бы путём
  // оно ни случилось.
  const queryClient = useQueryClient();
  const [, rebind] = useReducer((n: number) => n + 1, 0);
  useEffect(() => queryClient.getQueryCache().subscribe(event => {
    if (event.type === 'removed' && event.query.queryKey[0] === BRANDING_KEY[0]) rebind();
  }), [queryClient]);

  // Пустая метка — ответ сервера прежней версии (см. `useBranding`): ссылку тогда не трогаем.
  const version = useBranding().data?.icons.version;

  useEffect(() => {
    if (!version) return;
    applyBrandIcons(document, version);
    // Метку запоминаем: при следующей загрузке ранний скрипт поставит ссылку ещё до того, как
    // браузер возьмёт значок по адресу без метки — иначе прежний значок мигал бы на каждой загрузке.
    try { localStorage.setItem(BRAND_ICONS_KEY, version); } catch { /* приватный режим — без запоминания */ }
  }, [version]);

  return null;
}
