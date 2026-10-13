import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiClient } from './client';

/**
 * Название продукта и логотип компании (ТЗ CORE-25.1, issue #967).
 *
 * Читается АНОНИМНО: оформление стоит на странице входа, то есть нужно до входа. Поэтому запрос
 * живёт здесь, а не среди настроек, и зовётся из страницы входа так же, как из шапки.
 */
export interface Branding {
  productName: string;
  /** Название задано администратором, а не подставлено умолчанием. */
  isCustom: boolean;
  hasLogo: boolean;
  /** Метка версии файла — едет в адрес картинки, иначе заменённый логотип останется старым. */
  logoVersion: string | null;
  icons: BrandIcons;
}

/** Значки вкладки и приложения (issue #1315): чьи они сейчас — и почему, если не из логотипа. */
export interface BrandIcons {
  /** `failed` — логотип есть, а значки из него не построены: отдаются стандартные. */
  state: 'standard' | 'custom' | 'failed';
  /** Метка версии значков — в адрес как `?v=`. Строка непрозрачная: разбирать её нельзя. */
  version: string;
  /** Почему не построены: `file`, `size`, `builder`, `timeout`; null, пока отказа нет. */
  problem: string | null;
  /** Ширина к высоте того, что вписано в значок; null, пока значки не построены. */
  aspect: number | null;
}

export const BRANDING_KEY = ['branding'] as const;

/**
 * Ответ сервера — с полем `icons` в любом случае. Пока идёт обновление, клиент новой версии может
 * получить ответ от сервера прежней, где этого поля ещё нет; значки не повод ронять ни шапку, ни
 * экран настройки. Подставляется «стандартные, метки нет»: ссылку значка без метки никто не тронет,
 * превью и предупреждений не будет.
 */
export function withIcons(raw: Branding): Branding {
  return (raw as Partial<Branding>).icons
    ? raw
    : { ...raw, icons: { state: 'standard', version: '', problem: null, aspect: null } };
}

/**
 * @param follow через сколько спросить снова по тому, что получено, — или `false`. Задаёт его ТОЛЬКО
 *   экран настройки (см. `rebuildPoll`), и это не случайность: хук зовут шапка и страница входа, то
 *   есть каждая открытая вкладка каждого пользователя. Опрос отсюда при вечном отказе построителя
 *   (на сервере нет Typst) слал бы запрос в минуту с каждой вкладки без конца — а корзина запросов
 *   оформления одна на адрес, и офис за одним адресом сам довёл бы себя до 429.
 */
export function useBranding(follow?: (branding: Branding | undefined) => number | false) {
  return useQuery<Branding>({
    queryKey: BRANDING_KEY,
    queryFn: () => apiClient.get('/branding').then(r => withIcons(r.data)),
    // Оформление меняют раз в жизни экземпляра, а спрашивают на каждом экране: без длинного
    // «свежего» окна имя мигало бы умолчанием при каждом переходе.
    staleTime: 10 * 60 * 1000,
    refetchInterval: follow ? query => follow(query.state.data) : false,
  });
}

/** Адрес картинки логотипа — с меткой версии; null, когда логотипа нет. */
export function logoUrl(branding: Branding | undefined): string | null {
  if (!branding?.hasLogo) return null;
  return `/api/branding/logo?v=${encodeURIComponent(branding.logoVersion ?? '')}`;
}

export function useSaveProductName() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (productName: string | null) =>
      apiClient.put('/branding', { productName }).then(r => withIcons(r.data)),
    onSuccess: data => qc.setQueryData(BRANDING_KEY, data),
  });
}

export function useUploadLogo() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (file: File) => {
      const form = new FormData();
      form.append('file', file);
      return apiClient.post('/branding/logo', form).then(r => withIcons(r.data));
    },
    onSuccess: data => qc.setQueryData(BRANDING_KEY, data),
  });
}

export function useRemoveLogo() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: () => apiClient.delete('/branding/logo').then(r => withIcons(r.data)),
    onSuccess: data => qc.setQueryData(BRANDING_KEY, data),
  });
}

/**
 * Название экземпляра для показа (ТЗ CORE-25.1, issue #967).
 *
 * ⚠️ Пока оно НЕ ПОЛУЧЕНО — null, и место на экране остаётся пустым: ни умолчания, ни заглушки.
 * Подставить общее имя и через мгновение заменить его названием заказчика значит мигнуть чужим
 * именем там, где настройка заводилась ровно чтобы его не показывать.
 *
 * Хук живёт здесь, а не рядом с компонентом знака: модуль, экспортирующий и компонент, и хук,
 * теряет горячую перезагрузку (issue #858).
 */
export function useProductName(): string | null {
  const { data } = useBranding();
  return data?.productName ?? null;
}
