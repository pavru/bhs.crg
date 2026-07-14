import { List } from 'lucide-react';

export interface RailSection {
  key: string;
  title: string;
  count: number;
  /** Есть незаполненные обязательные поля (красная точка). */
  missing?: boolean;
}

/**
 * Rail разделов формы (issue #102 P3, #110 итерация): в диалогах свёрнут до узкой
 * полоски-аффорданса (иконка + точки по числу разделов) и раскрывается оверлеем по
 * наведению/фокусу — не отъедает ширину формы и не сдвигает раскладку.
 * Клавиатурой доступен: кнопки в tab-порядке, focus-within раскрывает панель.
 */
export function SectionRail({ sections, isActive, onSelect }: {
  sections: RailSection[];
  isActive: (key: string) => boolean;
  onSelect: (key: string) => void;
}) {
  return (
    <div className="group/rail relative hidden lg:block w-8 shrink-0 self-start sticky top-0">
      {/* Свёрнутый аффорданс — исчезает при раскрытии */}
      <div aria-hidden="true"
        className="flex flex-col items-center gap-1 py-1.5 rounded-lg bg-muted/60 transition-opacity
                   group-hover/rail:opacity-0 group-focus-within/rail:opacity-0">
        <List size={14} className="text-fg4" />
        {sections.map(s => (
          <span key={s.key}
            className={`w-1.5 h-1.5 rounded-full ${
              s.missing ? 'bg-danger' : isActive(s.key) ? 'bg-fg2' : 'bg-fg4/40'}`} />
        ))}
      </div>

      {/* Раскрываемая панель — оверлей, без сдвига раскладки */}
      <nav aria-label="Разделы"
        className="absolute left-0 top-0 z-20 w-52 space-y-0.5 rounded-lg border border-stroke bg-surface p-1.5
                   shadow-[var(--f-shadow4)] transition -translate-x-1 opacity-0 pointer-events-none
                   group-hover/rail:opacity-100 group-hover/rail:pointer-events-auto group-hover/rail:translate-x-0
                   group-focus-within/rail:opacity-100 group-focus-within/rail:pointer-events-auto group-focus-within/rail:translate-x-0">
        <div className="text-[10px] font-semibold uppercase tracking-wide text-fg4 px-2 pb-1">Разделы</div>
        {sections.map(section => (
          <button key={section.key} type="button" onClick={() => onSelect(section.key)}
            aria-current={isActive(section.key) ? 'true' : undefined}
            className={`w-full flex items-center gap-1.5 text-left text-xs px-2 py-1 rounded transition-colors
              ${isActive(section.key) ? 'bg-base text-fg1 font-medium' : 'text-fg3 hover:bg-base hover:text-fg1'}`}>
            <span className="flex-1 truncate">{section.title}</span>
            {section.missing && <>
              <span className="w-1.5 h-1.5 rounded-full bg-danger shrink-0" aria-hidden="true" />
              <span className="sr-only">не заполнено</span>
            </>}
            <span className="text-[10px] text-fg4 shrink-0">{section.count}</span>
          </button>
        ))}
      </nav>
    </div>
  );
}
