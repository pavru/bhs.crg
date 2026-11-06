import * as Popover from '@radix-ui/react-popover';
import { Close, Print } from '@/shared/ui/icons';
import { useId, useState } from 'react';
import { RadioGroup } from '@/shared/ui/RadioGroup';
import { Button, IconButton } from '@/shared/ui/Button';
import { Chip } from '@/shared/ui/Chip';
import { Tooltip } from '@/shared/ui/Tooltip';
import type { InvoiceImage } from '@/shared/api/invoiceImage';
import { profileSummary, type PrintProfile } from '@/shared/api/printProfiles';
import { initialChoice, layoutChipHint, layoutChipParts, layoutState } from './invoiceImageLayout';
import { LAYER_SURFACE } from '@/shared/ui/menuClasses';

/**
 * Чип «Раскладка: {профиль}» в строке «Вид для чтения» (issue #1456, вариант B): называет, по чему
 * разложен вид, и открывает выбор профиля печати. Обычная «Перестроить» рядом остаётся — один щелчок,
 * тот же профиль; выбор другого — здесь, явно: скрытое «перестроить по тому же» за меню путало бы.
 *
 * <p>Выбор в окне сначала только отмечается — перестраивает кнопка «Перестроить по выбранному»: стрелки
 * перебирают профили, и перестроение на каждое нажатие клавиши было бы ловушкой.</p>
 *
 * <p>Состояние профиля (`notice`) и пометка о раскладке строками не стоят, пока чип показан (issue #1495):
 * они в его подсказке — и первым абзацем окна, потому что на телефоне подсказка не открывается. В окне
 * состояние сказано без отсылки к «Перестроить» (`layoutState`): действие там своё.</p>
 */
export function LayoutChooser({ image, notice, profiles, rebuilding, onRebuild }: {
  image: InvoiceImage;
  notice: string | null;
  profiles: readonly PrintProfile[];
  rebuilding: boolean;
  onRebuild: (profileId: string) => Promise<boolean>;
}) {
  const heading = useId();
  const [open, setOpen] = useState(false);
  const [chosen, setChosen] = useState(() => initialChoice(image, profiles));
  const chipLabel = layoutChipParts(image);
  // В окне — состояние без «Перестроить построит…»: кнопка окна строит по отмеченному профилю.
  const state = layoutState(image);

  return (
    <Popover.Root open={open} onOpenChange={next => { setOpen(next); if (next) setChosen(initialChoice(image, profiles)); }}>
      <Tooltip content={layoutChipHint(image, notice)}>
        <Popover.Trigger asChild>
          {/* Не шире строки: длинное имя профиля обрезается многоточием и не выталкивает «Перестроить»,
              целиком оно — в окне выбора. Слово состояния не обрезается никогда: ради него оно в подписи. */}
          <Chip icon={Print} menu className="min-w-0 max-w-full" data-testid="layout-chip">
            <span className="flex min-w-0">
              <span className="truncate" data-testid="layout-chip-lead">{chipLabel.lead}</span>
              {chipLabel.state && <span className="shrink-0 whitespace-pre" data-testid="layout-chip-state">{chipLabel.state}</span>}
            </span>
          </Chip>
        </Popover.Trigger>
      </Tooltip>
      <Popover.Portal>
        <Popover.Content align="start" sideOffset={6} data-testid="layout-popover" aria-labelledby={heading}
          className={`z-50 w-80 max-w-(--radix-popover-content-available-width) max-h-(--radix-popover-content-available-height) overflow-y-auto rounded-md p-4 ${LAYER_SURFACE}`}>
          <div className="flex items-center justify-between mb-1">
            <h2 id={heading} className="text-title-small text-on-surface">Раскладка вида</h2>
            <Popover.Close asChild>
              <IconButton label="Закрыть" icon={Close} />
            </Popover.Close>
          </div>
          {state && <p className="text-body-small text-on-surface-variant mb-2" data-testid="layout-notice">{state}</p>}
          {image.layoutNote && <p className="text-body-small text-on-surface-variant mb-2" data-testid="layout-note">{image.layoutNote}</p>}
          <p className="text-body-small text-on-surface-variant mb-3">
            {/* Состояние уже сказано абзацем выше — общая фраза повторила бы его. */}
            {state || image.layoutNote ? '' : 'Автор книги не задал раскладку — вид раскладывается по профилю печати. '}
            Выбор запоминается за этим файлом.
          </p>
          <Choices profiles={profiles} shown={image.profile?.id} value={chosen} onPick={setChosen} />
          <div className="flex justify-end gap-2 mt-3">
            <Popover.Close asChild><Button variant="text">Закрыть</Button></Popover.Close>
            <Button variant="filled" loading={rebuilding} disabled={!chosen}
              onClick={async () => { if (chosen && await onRebuild(chosen)) setOpen(false); }}>
              Перестроить по выбранному
            </Button>
          </div>
        </Popover.Content>
      </Popover.Portal>
    </Popover.Root>
  );
}

/**
 * Профили — `RadioGroup` MD3 ([UI-69], Э17): одна остановка Tab, стрелки переводят выбор. Пометки: «по
 * умолчанию» — у системного, «сейчас» — у профиля показанного вида; под подписью — что задаёт профиль.
 */
function Choices({ profiles, shown, value, onPick }: {
  profiles: readonly PrintProfile[]; shown: string | undefined; value: string | undefined;
  onPick: (id: string) => void;
}) {
  return (
    <RadioGroup aria-label="Профиль печати" value={value} onChange={onPick}
      items={profiles.map(p => {
        const marks = [p.isSystem && 'по умолчанию', p.id === shown && 'сейчас'].filter(Boolean).join(' · ');
        return {
          value: p.id,
          label: p.name,
          description: <span className="block truncate">{profileSummary(p)}</span>,
          trailing: marks ? <span className="shrink-0 pl-2 text-body-small text-on-surface-variant">{marks}</span> : undefined,
        };
      })} />
  );
}
