export interface RailSection {
  key: string;
  title: string;
  count: number;
  /** Есть незаполненные обязательные поля (красная точка). */
  missing?: boolean;
}

/**
 * Rail разделов формы (issue #102 P3). В full-screen-редакторе (issue #189) — постоянная
 * развёрнутая панель навигации с подписями разделов, sticky слева (места достаточно). Раньше
 * (в тесной модалке, #110) была свёрнута до полоски-с-точками с раскрытием по наведению —
 * теперь развёрнута обратно.
 */
export function SectionRail({ sections, isActive, onSelect }: {
  sections: RailSection[];
  isActive: (key: string) => boolean;
  onSelect: (key: string) => void;
}) {
  return (
    <nav aria-label="Разделы" className="hidden lg:block w-56 shrink-0 self-start sticky top-0 space-y-0.5">
      <div className="text-xs font-medium text-fg4 px-2.5 pb-1">Разделы</div>
      {sections.map(section => (
        <button key={section.key} type="button" onClick={() => onSelect(section.key)}
          aria-current={isActive(section.key) ? 'true' : undefined}
          className={`w-full flex items-center gap-2 text-left text-sm px-2.5 py-1.5 rounded-lg transition-colors
            ${isActive(section.key) ? 'bg-surface text-fg1 font-medium' : 'text-fg3 hover:bg-surface hover:text-fg1'}`}>
          <span className="flex-1 truncate">{section.title}</span>
          {section.missing && <>
            <span className="w-1.5 h-1.5 rounded-full bg-danger shrink-0" aria-hidden="true" />
            <span className="sr-only">не заполнено</span>
          </>}
          <span className="text-xs text-fg4 shrink-0">{section.count}</span>
        </button>
      ))}
    </nav>
  );
}
