import type { MouseEvent } from 'react';
import { Link } from 'react-router';
import { Icon } from './Icon';
import { LeftPanelClose, LeftPanelOpen } from './icons';
import { BrandLogo } from '@/shared/ui/Brand';
import { useProductName } from '@/shared/api/branding';
import { useGuardedNavClick } from '@/shared/ui/NavigationGuard';
import { OutsideNav } from '@/shared/ui/WorkspaceNav';
import { PageList } from '@/shared/ui/PageList';
import type { NavItem, RailId, RailSectionDef } from '@/shared/ui/navConfig';
import type { PanelGroup, RailSection } from '@/shared/ui/railSections';
import { IconButton } from './Button';
import { Tooltip } from './Tooltip';

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
  const work = sections.filter(s => !s.def.unscoped);
  const system = sections.filter(s => s.def.unscoped);

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
        className="group flex flex-col items-center gap-1 w-20 pb-2 text-body-small select-none focus-visible:outline-none">
        <SectionPill icon={icon} on={on} />
        <span className={on ? 'font-semibold text-on-surface' : 'font-medium text-on-surface-variant'}>{section.def.label}</span>
      </Link>
    );
  };

  return (
    <div className="w-[88px] shrink-0 flex flex-col items-center pb-3 bg-surface overflow-y-auto">
      {/* Название и логотип экземпляра (ТЗ CORE-25.1, issue #967) — не зашитые. Название видно под
          логотипом; длинное обрезается, целиком — в подсказке. */}
      <Tooltip content={productName} side="right">
        <Link to="/" onClick={guardedClick('/')} aria-label={`${productName} — на главную`}
          className="mt-3 mb-4 flex flex-col items-center gap-0.5 w-20 shrink-0 rounded-lg focus-ring">
          <BrandLogo className="w-8 h-8 text-primary" iconSize={24} />
          <span className="w-full px-1 text-label-small font-semibold text-primary text-center truncate">{productName}</span>
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
 * выглядит одинаково, где бы человек его ни увидел. `wide` — плашка нижней строки, шире под палец.
 */
export function SectionPill({ icon, on, wide = false }: { icon: RailSectionDef['icon']; on: boolean; wide?: boolean }) {
  return (
    // Слой и кольцо — у плашки, состояние — у ссылки вокруг (`group`): курсор над подписью тоже зажигает её.
    <span className={`flex items-center justify-center ${wide ? 'w-16' : 'w-14'} h-8 rounded-full ` +
      'state-layer state-layer-group group-focus-visible:focus-ring-mark ' +
      (on ? 'bg-secondary-container text-on-secondary-container' : 'text-on-surface-variant')}>
      <Icon icon={icon} size={wide ? 24 : 20} filled={on} />
    </span>
  );
}

const PANEL_ID = 'section-panel';

/**
 * Подпанель раздела (issue #1429): заголовок, одна строка пояснения, страницы по группам и кнопка
 * «Скрыть панель раздела». Строка «Ещё N разделов вне пространства» — у рабочих разделов: «Систему»
 * пространство не сужает, и звать из неё к скрытым рабочим разделам было бы не к месту.
 */
export function SectionPanel({ def, groups, outside, onHide }: {
  def: RailSectionDef;
  groups: PanelGroup[];
  outside: NavItem[];
  onHide: () => void;
}) {
  return (
    <nav id={PANEL_ID} data-testid="section-panel" aria-label={def.title}
      className="w-60 shrink-0 flex flex-col px-3 py-4 bg-surface border-l border-outline-variant overflow-y-auto">
      <div className="flex items-start gap-1 pl-3 pb-3">
        <div className="flex-1 min-w-0 pt-1">
          <h2 className="text-body-large font-semibold text-on-surface break-words">{def.title}</h2>
          <p className="text-body-small text-on-surface-variant">{def.hint}</p>
        </div>
        <IconButton label="Скрыть панель раздела" size="lg" onClick={onHide}
          aria-expanded aria-controls={PANEL_ID}>
          <Icon icon={LeftPanelClose} size={18} />
        </IconButton>
      </div>
      <PageList groups={groups} />
      {!def.unscoped && <OutsideNav items={outside} />}
    </nav>
  );
}

/** Кнопка «Показать панель раздела» — над страницей, пока подпанель скрыта. */
export function ShowPanelButton({ onShow }: { onShow: () => void }) {
  return (
    <IconButton label="Показать панель раздела" onClick={onShow} aria-expanded={false}>
      <Icon icon={LeftPanelOpen} size={18} />
    </IconButton>
  );
}
