import type { Template } from '@/shared/api/types';
import { Checkbox } from '@/shared/ui/Checkbox';
import { ListItem } from '@/shared/ui/ListItem';

/**
 * Шаблоны генерации документа — строки `ListItem` ([UI-84], sharedui-md3-18, Р8). Флажок — участие шаблона в
 * генерации, сосед цели (`leadingAction`); сама строка выбирает шаблон, параметры которого показаны ниже
 * (`selected` вместо прежней черты `border-l-2`). Своё у каждого: флажок выбранный шаблон не меняет, строка не
 * меняет флажок — решение владельца 05.11.2026 вместо поведения #316 («щелчок по всей строке переключает участие»).
 */
export function GenerationTemplateRows({ templates, participating, focusedId, disabled, onToggle, onFocus }: {
  templates: Template[];
  /** Шаблоны, отмеченные флажком. */
  participating: string[];
  /** Шаблон, параметры которого показаны. */
  focusedId: string | null;
  disabled: boolean;
  onToggle: (id: string, on: boolean) => void;
  onFocus: (id: string) => void;
}) {
  return (
    <div className="rounded-sm border border-outline overflow-hidden">
      {templates.map(t => (
        <ListItem key={t.id} onClick={() => onFocus(t.id)} selected={t.id === focusedId}
          headline={`${t.isDefault ? '★ ' : ''}${t.name}`} trailing={`v${t.version}`}
          leadingAction={(
            <Checkbox checked={participating.includes(t.id)} disabled={disabled} onChange={v => onToggle(t.id, v)}
              aria-label={`Использовать шаблон «${t.name}» для генерации`} />
          )} />
      ))}
    </div>
  );
}
