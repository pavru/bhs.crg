import { Database, DocumentScanner } from '@/shared/ui/icons';
import { IconButton } from '@/shared/ui/Button';
import { ICON_BUTTON_XS } from '@/shared/ui/buttonClasses';
import { sourceOriginText } from '@/shared/ui/sourceOriginText';
import type { DataSetStaleReason } from '@/shared/api/types';

/**
 * Происхождение значения привязанного поля — для кнопки в хвосте поля ([UI-86]). `describeId` — ид
 * пояснения: поле ссылается на него `aria-describedby`, и диктор слышит происхождение вместе с полем.
 */
export interface SourceOriginMark {
  recognized: boolean;
  stale: boolean;
  staleReason?: DataSetStaleReason | null;
  describeId: string;
}

/**
 * Что кнопка сделает — по тому, есть ли у документа СВОЯ привязка поля (`isBound`), а не по тому, заполняет ли
 * поле источник: колонку полю может назначить и сам источник (`materializeMapping`), и тогда в окне привязки
 * нечего ни изменить, ни отвязать (ревью PR #1775). Одна сборка на обе кнопки — в углу и в хвосте: иначе одно
 * действие называлось бы двумя фразами.
 */
function triggerText(locked: boolean, isBound: boolean) {
  if (locked) return isBound
    ? { label: 'Снять привязку к источнику', action: 'Поле заперто модулем — привязку можно только снять' }
    : { label: 'Привязка к источнику', action: 'Поле заперто модулем — источник ему не назначается' };
  return isBound
    ? { label: 'Привязка к источнику', action: 'Изменить или отвязать' }
    : { label: 'Привязать к источнику', action: 'Привязать к источнику данных' };
}

/**
 * Значок «линзы» — вход в привязку к источнику, общий для скалярного (`FieldSourceBinding`) и контейнерного
 * (`ContainerFieldBinding`) поля. Непривязанное поле показывает его по наведению и при фокусе с клавиатуры;
 * привязанное — всегда, цветом primary. Место XS ([UI-68]): 40 накрывало кнопку календаря поля даты.
 *
 * С `origin` кнопка стоит в хвосте поля только для чтения и СЛИТА со знаком происхождения (D7
 * sharedui-md3-19): форма значка — происхождение (`DocumentScanner` со скана, `Database` из источника), цвет —
 * состояние (`primary` или `warning` «устарело»), как у `SourceOriginIcon`. Два знака рядом — линза и
 * происхождение — съели бы хвост, где ещё бывает замок. Место XS там задаёт слот хвоста, и видна она всегда:
 * значение из источника есть, даже если своей привязки у документа нет.
 */
export function SourceBindingTrigger({ locked, isBound, onOpen, origin }: {
  locked: boolean;
  isBound: boolean;
  onOpen: () => void;
  origin?: SourceOriginMark;
}) {
  const { label, action } = triggerText(locked, isBound);
  if (origin) {
    const text = sourceOriginText(origin);
    return (
      <>
        <IconButton label={label} title={`${text}. ${action}`} onClick={onOpen}
          icon={origin.recognized ? DocumentScanner : Database}
          iconClassName={origin.stale ? 'text-warning' : 'text-primary'} />
        <span id={origin.describeId} className="sr-only">{text}</span>
      </>
    );
  }
  return (
    <IconButton label={label}
      title={isBound && !locked ? `Заполняется из источника данных. ${action}` : action}
      className={`-my-1.5 ${ICON_BUTTON_XS} ${isBound ? '' : 'opacity-0 group-hover:opacity-100 focus-visible:opacity-100'}`}
      onClick={onOpen} icon={Database} iconClassName={isBound ? 'text-primary' : undefined} />
  );
}
