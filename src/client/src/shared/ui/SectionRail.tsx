import type { MouseEvent } from 'react';
import { Link } from 'react-router';
import { PanelLeftClose, PanelLeftOpen } from 'lucide-react';
import { BrandLogo } from '@/shared/ui/Brand';
import { useProductName } from '@/shared/api/branding';
import { useGuardedNavClick } from '@/shared/ui/NavigationGuard';
import { OutsideNav } from '@/shared/ui/WorkspaceNav';
import { PageList, RailFooter } from '@/shared/ui/AppRail';
import type { NavItem, RailId, RailSectionDef } from '@/shared/ui/navConfig';
import type { PanelGroup, RailSection } from '@/shared/ui/railSections';
import { IconButton } from './Button';
import { Tooltip } from './Tooltip';

/**
 * Рейл разделов рабочего стола (issue #1429), по образцу MD3 navigation rail: значок и под ним
 * короткая подпись. Сверху вниз — рабочие разделы, черта, «Система».
 *
 * «Система» стоит сразу под разделами, а не прижата к низу: на невысоком экране прижатый пункт
 * уходит из виду (эпик #1428, «Правила оболочки»). Внизу — только то, что уйдёт в шапку (#1430).
 */
export function SectionRail({ sections, active, panelHidden, onShowPanel, onChangePassword }: {
  sections: RailSection[];
  active: RailId | null;
  /** Подпанель есть у этого адреса, но скрыта человеком: щелчок по своему разделу её возвращает. */
  panelHidden: boolean;
  onShowPanel: () => void;
  onChangePassword: () => void;
}) {
  const productName = useProductName();
  const guardedClick = useGuardedNavClick();
  const work = sections.filter(s => !s.def.unscoped);
  const system = sections.filter(s => s.def.unscoped);

  // Свой раздел при скрытой подпанели — «покажи страницы раздела», а не переход на его первую
  // страницу: человек, открывший «Накладные», уходил бы на «Счета», хотя просил меню.
  const guardedSection = (section: RailSection) => (e: MouseEvent) => {
    if (section.def.id === active && panelHidden) { e.preventDefault(); onShowPanel(); return; }
    guardedClick(section.home)(e);
  };

  const item = (section: RailSection) => {
    const on = section.def.id === active;
    const Icon = section.def.icon;
    return (
      <Link key={section.def.id} to={section.home} onClick={guardedSection(section)}
        aria-current={on ? 'true' : undefined}
        className="group flex flex-col items-center gap-1 w-20 pb-2 text-xs select-none focus-visible:outline-none">
        <span className={'flex items-center justify-center w-14 h-8 rounded-full transition-colors ' +
          'group-focus-visible:ring-2 group-focus-visible:ring-brand ' +
          (on ? 'bg-tonal text-on-tonal' : 'text-fg3 group-hover:bg-black/5 dark:group-hover:bg-white/10')}>
          <Icon size={20} />
        </span>
        <span className={on ? 'font-semibold text-fg1' : 'font-medium text-fg2'}>{section.def.label}</span>
      </Link>
    );
  };

  return (
    <div className="w-[88px] shrink-0 flex flex-col items-center bg-surface overflow-y-auto">
      {/* Название и логотип экземпляра (ТЗ CORE-25.1, issue #967) — не зашитые. В 88 px названию
          места нет: оно в подсказке и в имени ссылки. */}
      <Tooltip content={productName} side="right">
        <Link to="/" onClick={guardedClick('/')} aria-label={`${productName} — на главную`}
          className="mt-3 mb-5 flex items-center justify-center w-10 h-10 rounded-xl shrink-0 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-brand">
          <BrandLogo className="w-8 h-8 text-brand" iconSize={24} />
        </Link>
      </Tooltip>

      <nav id="app-rail-nav" aria-label="Разделы" className="flex flex-col items-center gap-1">
        {work.map(item)}
        {system.length > 0 && (
          <>
            {work.length > 0 && <div aria-hidden="true" className="w-10 h-px bg-stroke mt-2 mb-3" />}
            {system.map(item)}
          </>
        )}
      </nav>

      <div className="flex-1 min-h-6" />
      <RailFooter compact onChangePassword={onChangePassword} />
    </div>
  );
}

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
    <nav data-testid="section-panel" aria-label={def.title}
      className="w-60 shrink-0 flex flex-col px-3 py-4 bg-surface border-l border-stroke overflow-y-auto">
      <div className="flex items-start gap-1 pl-3 pb-3">
        <div className="flex-1 min-w-0 pt-1">
          <h2 className="text-base font-semibold text-fg1 break-words">{def.title}</h2>
          <p className="text-xs text-fg3">{def.hint}</p>
        </div>
        <IconButton label="Скрыть панель раздела" size="lg" onClick={onHide}>
          <PanelLeftClose size={18} />
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
    <IconButton label="Показать панель раздела" onClick={onShow}>
      <PanelLeftOpen size={18} />
    </IconButton>
  );
}
