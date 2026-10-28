import { useState } from 'react';
import { NavLink, useLocation } from 'react-router';
import { ChevronDown, ChevronRight, LayoutGrid } from 'lucide-react';
import { NO_ACCESS, useAccess } from '@/shared/api/access';
import { ruCount } from '@/shared/utils/pluralize';
import { useGuardedNavClick, useGuardedNavigate } from './NavigationGuard';
import { useWorkspace } from './workspaceContext';
import { outsideItem } from './workspaces';
import type { NavItem } from './navConfig';
import { panelLink } from './panelLink';

/**
 * «Ещё N разделов вне пространства ▸» — последней строкой подпанели, постоянно (ТЗ AUTH-16.1;
 * issue #1001, #1429). Меню само говорит, что скрыто: без этой строки к скрытому разделу вёл бы только
 * поиск, то есть нужно было бы заранее знать ответ. Переход открывает раздел БЕЗ смены пространства.
 *
 * Строка одна на все разделы рейла, а не своя у каждого: раздел, у которого вне пространства ВСЁ, из
 * рейла уходит, и его страницам остаётся только эта строка.
 */
export function OutsideNav({ items }: { items: NavItem[] }) {
  const { pathname } = useLocation();
  // Пришли на страницу из этого списка — он раскрывается, и палитрой или ссылкой, когда подпанель уже
  // стоит: иначе выбранный пункт пропадал бы из виду. Раскрывается в МОМЕНТ прихода (поправка
  // состояния при рендере, как велит React), а не держится раскрытым: свернуть его человек может.
  const here = items.some(({ to }) => pathname === to || pathname.startsWith(`${to}/`));
  const [open, setOpen] = useState(here);
  const [wasHere, setWasHere] = useState(here);
  if (here !== wasHere) {
    setWasHere(here);
    if (here) setOpen(true);
  }
  const guardedClick = useGuardedNavClick();
  if (items.length === 0) return null;

  const caption = `Ещё ${ruCount(items.length, 'раздел', 'раздела', 'разделов')} вне пространства`;
  return (
    <div data-testid="outside-nav" className="mt-3">
      <button type="button" aria-expanded={open} onClick={() => setOpen(o => !o)}
        className="w-full flex items-center gap-2 px-3 py-2 rounded-full text-left text-xs text-fg3 hover:bg-black/5 dark:hover:bg-white/10 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary">
        {open ? <ChevronDown size={14} className="shrink-0" /> : <ChevronRight size={14} className="shrink-0" />}
        <span>{caption}</span>
      </button>
      {open && (
        <div className="space-y-0.5 mt-0.5">
          {items.map(({ to, label }) => (
            <NavLink key={to} to={to} onClick={guardedClick(to)}
              className={({ isActive }) => panelLink(isActive, true)}>
              <span className="truncate">{label}</span>
            </NavLink>
          ))}
        </div>
      )}
    </div>
  );
}

/**
 * Плашка «Открыто вне пространства „…“» с возвратом (ТЗ AUTH-16.1). Ссылка из уведомления или письма
 * в раздел вне пространства ОТКРЫВАЕТСЯ, а пространство не меняется: запрет превратил бы удобство в
 * ограничение доступа, авто-переключение молча меняло бы сохранённый выбор от одной навигации.
 */
export function OutsideBanner({ items, inside }: { items: NavItem[]; inside: NavItem[] }) {
  const ws = useWorkspace();
  const { data: access = NO_ACCESS } = useAccess();
  const { pathname } = useLocation();
  const navigate = useGuardedNavigate();
  const owner = ws.shown ? outsideItem(pathname, items, ws.current, access) : null;
  if (!owner) return null;

  const home = inside[0];
  return (
    <div className="shrink-0 flex items-center gap-2 px-4 py-1.5 text-sm bg-surface-container-low text-on-surface-variant border-b border-outline-variant"
      data-testid="outside-banner">
      <LayoutGrid size={14} className="shrink-0 text-fg3" />
      <span className="flex-1 min-w-0 truncate">
        «{owner.label}» открыт вне пространства «{ws.current.name}»
      </span>
      {home && (
        <button type="button" onClick={() => navigate(home.to)}
          className="shrink-0 font-medium text-primary underline hover:no-underline">
          Вернуться в пространство
        </button>
      )}
    </div>
  );
}

/**
 * Пункты модуля убраны пространством — сказано на месте (ревью PR #1330). Без этой строки страница
 * раздела в пространстве «Счета» просто не показывала комплектов, и это читалось как «комплекты
 * пропали» или «нет прав», хотя адрес — «Стройки», и плашки «вне пространства» нет. Строки нет, если
 * модуль человеку недоступен: тогда пункты скрыты правами, и пространство ни при чём.
 */
export function HiddenByWorkspace({ module, what }: { module: string; what: string }) {
  const ws = useWorkspace();
  const { data: access = NO_ACCESS } = useAccess();
  const title = access.modules.find(m => m.code === module && m.available)?.title;
  if (!title || ws.shows(module)) return null;
  return (
    <p className="px-3 py-1.5 text-xs text-fg3" data-testid="hidden-by-workspace">
      {what} — модуль «{title}», он вне пространства «{ws.current.name}».{' '}
      <button type="button" className="underline hover:no-underline text-primary" onClick={() => ws.choose('all')}>
        Открыть все модули
      </button>
    </p>
  );
}
