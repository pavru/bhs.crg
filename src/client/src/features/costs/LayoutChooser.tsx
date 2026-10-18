import * as Popover from '@radix-ui/react-popover';
import { Check, Printer, X } from 'lucide-react';
import { useId, useRef, useState, type KeyboardEvent } from 'react';
import { Button, IconButton } from '@/shared/ui/Button';
import { Chip } from '@/shared/ui/Chip';
import type { InvoiceImage } from '@/shared/api/invoiceImage';
import { profileSummary, type PrintProfile } from '@/shared/api/printProfiles';
import { initialChoice, layoutChipLabel } from './invoiceImageLayout';

/**
 * Чип «Раскладка: {профиль}» под строкой «Вид для чтения» (issue #1456, вариант B): называет, по чему
 * разложен вид, и открывает выбор профиля печати. Обычная «Перестроить» рядом остаётся — один щелчок,
 * тот же профиль; выбор другого — здесь, явно: скрытое «перестроить по тому же» за меню путало бы.
 *
 * <p>Выбор в окне сначала только отмечается — перестраивает кнопка «Перестроить по выбранному»: стрелки
 * перебирают профили, и перестроение на каждое нажатие клавиши было бы ловушкой.</p>
 */
export function LayoutChooser({ image, profiles, rebuilding, onRebuild }: {
  image: InvoiceImage;
  profiles: readonly PrintProfile[];
  rebuilding: boolean;
  onRebuild: (profileId: string) => Promise<boolean>;
}) {
  const heading = useId();
  const [open, setOpen] = useState(false);
  const [chosen, setChosen] = useState(() => initialChoice(image, profiles));

  return (
    <Popover.Root open={open} onOpenChange={next => { setOpen(next); if (next) setChosen(initialChoice(image, profiles)); }}>
      <Popover.Trigger asChild>
        {/* Не шире панели: длинное имя профиля обрезается многоточием, целиком оно — в окне выбора. */}
        <Chip icon={Printer} menu className="max-w-full" data-testid="layout-chip">{layoutChipLabel(image)}</Chip>
      </Popover.Trigger>
      <Popover.Portal>
        <Popover.Content align="start" sideOffset={6} data-testid="layout-popover" aria-labelledby={heading}
          className="z-50 w-80 max-w-(--radix-popover-content-available-width) max-h-(--radix-popover-content-available-height) overflow-y-auto rounded-lg border border-stroke bg-surface p-4"
          style={{ boxShadow: 'var(--f-shadow28)' }}>
          <div className="flex items-center justify-between mb-1">
            <h2 id={heading} className="text-sm font-semibold text-fg1">Раскладка вида</h2>
            <Popover.Close asChild>
              <IconButton label="Закрыть" size="sm"><X size={14} /></IconButton>
            </Popover.Close>
          </div>
          <p className="text-xs text-fg3 mb-3">
            {image.profileState === 'none'
              ? 'Вид построен до профилей печати. Если автор книги не задал раскладку, вид разложится по выбранному профилю.'
              : 'Автор книги не задал раскладку — вид раскладывается по профилю печати.'}
            {' '}Выбор запоминается за этим файлом.
          </p>
          <Choices profiles={profiles} shown={image.profile?.id} value={chosen} onPick={setChosen} />
          <div className="flex justify-end gap-2 mt-3">
            <Popover.Close asChild><Button variant="text" size="sm">Закрыть</Button></Popover.Close>
            <Button variant="filled" size="sm" loading={rebuilding} disabled={!chosen}
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
 * Профили — радиогруппой по APG: одна остановка Tab, стрелки переводят выбор. Пометки: «по умолчанию»
 * — у системного, «сейчас» — у профиля показанного вида.
 */
function Choices({ profiles, shown, value, onPick }: {
  profiles: readonly PrintProfile[]; shown: string | undefined; value: string | undefined;
  onPick: (id: string) => void;
}) {
  const refs = useRef<(HTMLButtonElement | null)[]>([]);
  const onKeyDown = (e: KeyboardEvent) => {
    const step = { ArrowDown: 1, ArrowRight: 1, ArrowUp: -1, ArrowLeft: -1 }[e.key];
    if (!step) return;
    e.preventDefault();
    const at = Math.max(0, profiles.findIndex(p => p.id === value));
    const next = (at + step + profiles.length) % profiles.length;
    onPick(profiles[next].id);
    refs.current[next]?.focus();
  };
  return (
    <div role="radiogroup" aria-label="Профиль печати" onKeyDown={onKeyDown} className="-mx-2 space-y-0.5">
      {profiles.map((p, i) => {
        const checked = p.id === value;
        const marks = [p.isSystem && 'по умолчанию', p.id === shown && 'сейчас'].filter(Boolean).join(' · ');
        return (
          <button key={p.id} ref={el => { refs.current[i] = el; }} type="button" role="radio"
            aria-checked={checked} tabIndex={checked || (!value && i === 0) ? 0 : -1} onClick={() => onPick(p.id)}
            className="w-full flex items-start gap-2 px-2 py-1.5 rounded-md text-left hover:bg-black/5 dark:hover:bg-white/10 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-brand">
            <Check size={16} aria-hidden className={`shrink-0 mt-0.5 ${checked ? 'text-brand' : 'invisible'}`} />
            <span className="min-w-0 flex-1">
              <span className="flex items-baseline gap-2">
                <span className={`text-sm truncate ${checked ? 'font-medium text-fg1' : 'text-fg2'}`}>{p.name}</span>
                {marks && <span className="text-xs text-fg3 shrink-0">{marks}</span>}
              </span>
              <span className="block text-xs text-fg4 truncate">{profileSummary(p)}</span>
            </span>
          </button>
        );
      })}
    </div>
  );
}
