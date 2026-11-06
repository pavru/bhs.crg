import { Badge } from '@/shared/ui/Badge';
import { MICRO_HEADING, panelLink } from '@/shared/ui/panelLink';

export interface RailSection {
  key: string;
  title: string;
  count: number;
  /** Есть незаполненные обязательные поля (красная точка). */
  missing?: boolean;
}

/**
 * Rail разделов формы (issue #102 P3). В full-screen-редакторе (issue #189) — постоянная
 * развёрнутая панель навигации с подписями разделов, sticky слева (места достаточно).
 *
 * <p>Пункт — пилюля 48, как строка подпанели (`panelLink`, П3 = б, [UI-84]): у оглавления и подпанели одна роль —
 * «куда перейти». Текущий — `secondary-container` с `aria-current`; «не заполнено» — точка `Badge`, словом —
 * для диктора.</p>
 */
export function SectionRail({ sections, isActive, onSelect }: {
  sections: RailSection[];
  isActive: (key: string) => boolean;
  onSelect: (key: string) => void;
}) {
  return (
    <nav aria-label="Разделы" className="hidden lg:block w-56 shrink-0 self-start sticky top-0">
      <div className={MICRO_HEADING}>Разделы</div>
      {sections.map(section => (
        <button key={section.key} type="button" onClick={() => onSelect(section.key)}
          aria-current={isActive(section.key) ? 'true' : undefined}
          className={`w-full gap-2 text-left ${panelLink(isActive(section.key))}`}>
          <span className="flex-1 truncate">{section.title}</span>
          {section.missing && <><Badge className="shrink-0" /><span className="sr-only">не заполнено</span></>}
          <span className="shrink-0">{section.count}</span>
        </button>
      ))}
    </nav>
  );
}
