import { useEffect, useRef, type MouseEvent, type ReactNode } from 'react';
import * as Dialog from '@radix-ui/react-dialog';
import { Link, useLocation } from 'react-router';
import { Icon } from './Icon';
import { LeftPanelClose, LeftPanelOpen } from './icons';
import { BrandLogo } from '@/shared/ui/Brand';
import { useProductName } from '@/shared/api/branding';
import { useGuardedNavClick } from '@/shared/ui/NavigationGuard';
import { OutsideNav } from '@/shared/ui/WorkspaceNav';
import { PageList } from '@/shared/ui/PageList';
import { PinnedGroup } from '@/shared/ui/PinnedGroup';
import type { PinnedEntry } from '@/shared/ui/pinnedSections';
import type { NavItem, RailId, RailSectionDef } from '@/shared/ui/navConfig';
import type { PanelGroup, RailSection } from '@/shared/ui/railSections';
import { IconButton } from './Button';
import { Tooltip } from './Tooltip';
import { DIALOG_SCRIM } from './dialogSurface';
import { useHistoryLayer } from './historyLayers';

/**
 * Рейл разделов рабочего стола (issue #1429), по образцу MD3 navigation rail: значок и под ним
 * короткая подпись. Сверху вниз — рабочие разделы, черта, «Система».
 *
 * «Система» стоит сразу под разделами, а не прижата к низу: на невысоком экране прижатый пункт
 * уходит из виду (эпик #1428, «Правила оболочки»). Внизу ничего нет: тема, профиль, сообщение о
 * проблеме, выход и версия живут в меню профиля в шапке (#1430).
 */
export function SectionRail({ sections, active, panelHidden, onShowPanel }: {
  sections: RailSection[];
  active: RailId | null;
  /** Подпанель есть у этого адреса, но скрыта человеком: щелчок по своему разделу её возвращает. */
  panelHidden: boolean;
  onShowPanel: () => void;
}) {
  const productName = useProductName();
  const guardedClick = useGuardedNavClick();
  const work = sections.filter(s => !s.def.admin);
  const system = sections.filter(s => s.def.admin);

  // Свой раздел при скрытой подпанели — «покажи страницы раздела», а не переход на его первую
  // страницу: человек, открывший «Накладные», уходил бы на «Счета», хотя просил меню.
  // Щелчок с Ctrl/⌘/Shift/Alt или не левой кнопкой — «открыть в новой вкладке»: его не перехватываем,
  // как и `useGuardedNavClick`.
  const guardedSection = (section: RailSection) => (e: MouseEvent) => {
    const plain = !e.metaKey && !e.ctrlKey && !e.shiftKey && !e.altKey && e.button === 0;
    if (plain && section.def.id === active && panelHidden) { e.preventDefault(); onShowPanel(); return; }
    guardedClick(section.home)(e);
  };

  const item = (section: RailSection) => {
    const on = section.def.id === active;
    const icon = section.def.icon;
    return (
      <Link key={section.def.id} to={section.home} onClick={guardedSection(section)}
        aria-current={on ? 'true' : undefined}
        className="group flex flex-col items-center gap-1 w-20 pb-2 text-label-medium select-none focus-visible:outline-none">
        <SectionPill icon={icon} on={on} />
        {/* Одним весом и цветом (MD3 navigation rail): выбранный отличает индикатор и заливка знака. */}
        <span className="whitespace-nowrap text-on-surface-variant">{section.def.label}</span>
      </Link>
    );
  };

  return (
    <div className="w-20 shrink-0 flex flex-col items-center pb-3 bg-surface-container-low overflow-y-auto">
      {/* Название и логотип экземпляра (ТЗ CORE-25.1, issue #967) — не зашитые. Название видно под
          логотипом; длинное обрезается, целиком — в подсказке. */}
      <Tooltip content={productName} side="right">
        <Link to="/home" onClick={guardedClick('/home')} aria-label={`${productName} — на главную`}
          className="mt-3 mb-4 flex flex-col items-center gap-0.5 w-20 shrink-0 rounded-lg focus-ring">
          <BrandLogo className="w-8 h-8 text-primary" iconSize={24} />
          <span className="w-full px-1 text-label-small text-primary text-center truncate">{productName}</span>
        </Link>
      </Tooltip>

      <nav id="app-rail-nav" aria-label="Разделы" className="flex flex-col items-center gap-1">
        {work.map(item)}
        {system.length > 0 && (
          <>
            {work.length > 0 && <div aria-hidden="true" className="w-10 h-px bg-outline-variant mt-2 mb-3" />}
            {system.map(item)}
          </>
        )}
      </nav>
    </div>
  );
}

/**
 * Значок раздела в плашке — один на рейл и нижнюю навигацию телефона (#1434): выбранный раздел
 * выглядит одинаково, где бы человек его ни увидел. Знак — 24 в обоих (MD3); `wide` — индикатор нижней
 * строки, 64 вместо 56 рейла.
 */
export function SectionPill({ icon, on, wide = false }: { icon: RailSectionDef['icon']; on: boolean; wide?: boolean }) {
  return (
    // Слой и кольцо — у плашки, состояние — у ссылки вокруг (`group`): курсор над подписью тоже зажигает её.
    <span className={`flex items-center justify-center ${wide ? 'w-16' : 'w-14'} h-8 rounded-full ` +
      'state-layer state-layer-group group-focus-visible:focus-ring-mark ' +
      (on ? 'bg-secondary-container text-on-secondary-container' : 'text-on-surface-variant')}>
      <Icon icon={icon} size={24} filled={on} />
    </span>
  );
}

const PANEL_ID = 'section-panel';

/**
 * Подпанель раздела (issue #1429): заголовок, одна строка пояснения, страницы по группам и кнопка
 * «Скрыть панель раздела». Строка «Ещё N разделов вне пространства» — у рабочих разделов: «Систему»
 * пространство не сужает, и звать из неё к скрытым рабочим разделам было бы не к месту.
 */
export function SectionPanel({ def, groups, outside, pinned, pinnable, onHide }: {
  def: RailSectionDef;
  groups: PanelGroup[];
  outside: NavItem[];
  /** Группа «Закреплённые» (work-04) — первой у рабочих разделов; у «Администрирования» её нет (Р0, А). */
  pinned: PinnedEntry[];
  pinnable: (to: string) => boolean;
  onHide: () => void;
}) {
  return (
    // Полоса прокрутки — тонкая: «Администрирование» с пунктами 48 выше экрана 768, и обычная полоса
    // (15 px) отняла бы ширину у заголовка раздела — он переносился бы посреди слова.
    <nav id={PANEL_ID} data-testid="section-panel" aria-label={def.title}
      className="w-60 shrink-0 flex flex-col px-3 py-4 bg-surface-container-low overflow-y-auto [scrollbar-width:thin]">
      <div className="flex items-start pl-3 pb-3">
        <div className="flex-1 min-w-0 pt-1">
          <h2 className="text-title-medium text-on-surface break-words hyphens-auto">{def.title}</h2>
          <p className="text-body-small text-on-surface-variant">{def.hint}</p>
        </div>
        {/* Кнопка заходит в поле подпанели (`-mr-2`): знак 24 в ней и так с полями, а заголовку
            «Администрирование» (157 px) нужно место рядом с полосой прокрутки. Не поместится длиннее —
            перенос по слогам с дефисом (`hyphens-auto`, документ на ru), а не разрыв посреди слова. */}
        <IconButton label="Скрыть панель раздела" onClick={onHide} className="-mr-2"
          aria-expanded aria-controls={PANEL_ID} icon={LeftPanelClose} />
      </div>
      {!def.admin && <PinnedGroup entries={pinned} />}
      <PageList groups={groups} pinnable={pinnable} />
      {!def.admin && <OutsideNav items={outside} />}
    </nav>
  );
}

/** Кнопка «Показать панель раздела» — над страницей, пока подпанель скрыта. */
export function ShowPanelButton({ onShow }: { onShow: () => void }) {
  return (
    <IconButton label="Показать панель раздела" onClick={onShow} aria-expanded={false} icon={LeftPanelOpen} />
  );
}

/**
 * Подпанель поверх страницы — модальный drawer MD3 (costs-invoice-screen-10-layout, D-10.5, решение Э11):
 * пока страница подавляет подпанель (открыт счёт), «Показать панель» открывает её здесь, с затемнением, а
 * не в раскладке. Раскладка формы и место файла не меняются, выбор человека не пишется. Закрывается Esc,
 * щелчком вне и выбором пункта. Выбор закрывает СОСТОЯВШИЙСЯ переход, а не щелчок: охрана ухода сначала
 * спрашивает о несохранённом, и «Отмена» оставляет и счёт, и drawer.
 */
export function SectionPanelDrawer({ open, onClose, title, children }: {
  open: boolean; onClose: () => void; title: string; children: ReactNode;
}) {
  // «Назад» закрывает drawer, а не уводит со страницы под ним — с формой и набранным.
  useHistoryLayer(open, () => { onClose(); return true; });
  const { pathname, search } = useLocation();
  const at = useRef(`${pathname}${search}`);
  useEffect(() => {
    const now = `${pathname}${search}`;
    if (now !== at.current && open) onClose();
    at.current = now;
  }, [pathname, search, open, onClose]);
  return (
    <Dialog.Root open={open} onOpenChange={next => { if (!next) onClose(); }}>
      <Dialog.Portal>
        <Dialog.Overlay className={DIALOG_SCRIM} />
        <Dialog.Content aria-describedby={undefined} data-modal-state="free" data-testid="section-panel-drawer"
          className="fixed inset-y-0 left-0 z-50 flex rounded-r-lg bg-surface-container-low shadow-elevation-1 focus:outline-none">
          <Dialog.Title className="sr-only">{title}</Dialog.Title>
          {children}
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}
