import { Navigate, Outlet, useLocation } from 'react-router';
import { useAuth } from '@/shared/hooks/useAuth';
import { useAccess, NO_ACCESS } from '@/shared/api/access';
import { currencyKnown } from '@/shared/format/format';
import { workNav, settingsNav, type NavItem } from './navConfig';
import { allowed, missingFor } from './navAccess';
import { NoAccessPage } from './NoAccessPage';

export function ProtectedRoute() {
  const { user } = useAuth();
  const { isPending } = useAccess(!!user);
  if (!user) return <Navigate to="/login" replace />;

  // Валюта учёта ещё не известна — ни из зеркала, ни от сервера (issue #1248): форматтер рисовал
  // бы рубль наугад, а суммы есть и вне разделов под `RequireAccess` (образец формата в профиле).
  // Ждём ответа о доступе — он несёт валюту. Запрос упал — рисуем: рубль по умолчанию лучше пустого
  // экрана, а в браузере, где система уже открывалась, зеркало есть и ждать нечего.
  if (!currencyKnown() && isPending) return null;

  return <Outlet />;
}

/**
 * Доступ к разделу — по правам (ТЗ AUTH-15, issue #952).
 *
 * Пришло на смену `AdminRoute`, который читал роль из токена и молча отправлял неадминистратора на
 * список строек. Замена не косметическая: перенаправление неотличимо от поломки — человек нажал
 * пункт, оказался не там и не знает, сломалась система или ему нельзя.
 *
 * ⚠️ Чем закрыт раздел, спрашивается у `navConfig` — там же, откуда берётся меню. Отдельный список
 * «маршрут → право» был бы вторым описанием того же, и разошёлся бы он молча: пункт исчез из меню,
 * а маршрут остался открытым (или наоборот — пункт виден, а вход закрыт).
 */
export function RequireAccess() {
  const { user } = useAuth();
  const { data: access, isPending } = useAccess();
  const { pathname } = useLocation();

  if (!user) return <Navigate to="/login" replace />;

  // Пока доступ не известен, не показываем ни раздел, ни отказ: и то и другое было бы
  // утверждением, которого мы пока не можем сделать.
  if (isPending) return null;

  const item = sectionFor(pathname);
  if (!item) return <Outlet />;

  const info = access ?? NO_ACCESS;
  return allowed(item, info)
    ? <Outlet />
    : <NoAccessPage what={item.label} missing={missingFor(item, info)} />;
}

/** Раздел, которому принадлежит адрес: самое длинное совпадение по границе сегмента. */
function sectionFor(pathname: string): NavItem | undefined {
  return [...workNav, ...settingsNav]
    .filter(item => pathname === item.to || pathname.startsWith(item.to + '/'))
    .sort((a, b) => b.to.length - a.to.length)[0];
}
