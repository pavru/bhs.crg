import { Icon } from '@/shared/ui/Icon';
import { Chip } from '@/shared/ui/Chip';
import { ErrorIcon, type IconDef } from '@/shared/ui/icons';

/**
 * Индикатор БИТОЙ ссылки на поле редактора реквизитов (issue #332): целевой объект удалён.
 * Сигнал — знак `error`: от обязательного-незаполненного битая ссылка отличается анатомией — она
 * ЗАПОЛНЕНА (есть displayName), знак ошибки, нота «объект удалён» и зачёркнутый displayName как «что было».
 * Заливки нет (sharedui-md3-17): красная плашка с текстом `error` на `error-container` была плиткой руками,
 * и её классы, спрятанные в константе, правила храповика не видели.
 */
export const BROKEN_REF_MESSAGE = 'Ссылка не разрешена — целевой объект не найден или удалён';

/** Зачёркнутый displayName битой ссылки — «что было». */
export const BROKEN_LABEL = 'text-error line-through';

/**
 * Битая ссылка поля — input chip (Р4): знак ошибки вместо знака вида, щелчок — заменить, крестик — снять;
 * нота — под ним.
 */
export function BrokenRefChip({ name, onReplace, onClear }: { name: string; onReplace: () => void; onClear: () => void }) {
  return (
    <div>
      <Chip icon={ErrorIcon as IconDef} iconTone="error" iconLabel="Битая ссылка" title="Заменить ссылку"
        onClick={onReplace} onRemove={onClear} removeLabel="Снять ссылку" className="max-w-full">
        <span className="line-through">{name}</span>
      </Chip>
      <BrokenRefNote />
    </div>
  );
}

/** Нота под битой плиткой. compact — для элементов массива/вложенных строк. */
export function BrokenRefNote({ compact = false }: { compact?: boolean }) {
  return (
    <div className={`flex items-center gap-1.5 text-error ${compact ? 'text-label-small px-3 pb-1.5' : 'text-body-small mt-1'}`}>
      <Icon icon={ErrorIcon} size={18} className="shrink-0" />
      <span>{BROKEN_REF_MESSAGE}</span>
    </div>
  );
}
