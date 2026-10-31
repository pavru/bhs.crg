import { Database } from '@/shared/ui/icons';
import { IconButton } from '@/shared/ui/Button';
import { ICON_BUTTON_XS } from '@/shared/ui/buttonClasses';

/**
 * Значок «линзы» у подписи поля — вход в привязку к источнику, общий для скалярного
 * (`FieldSourceBinding`) и контейнерного (`ContainerFieldBinding`) поля. Непривязанное поле
 * показывает его по наведению и при фокусе с клавиатуры; привязанное — всегда, цветом primary.
 * Стоит в углу поля — место XS ([UI-68]): 40 накрывало кнопку календаря поля даты.
 */
export function SourceBindingTrigger({ locked, isBound, onOpen }: {
  locked: boolean;
  isBound: boolean;
  onOpen: () => void;
}) {
  return (
    <IconButton label={locked ? 'Снять привязку к источнику'
          : isBound ? 'Привязка к источнику' : 'Привязать к источнику'}
      title={locked ? 'Поле заперто модулем — привязку можно только снять'
        : isBound ? 'Заполняется из источника данных — изменить/отвязать' : 'Привязать к источнику данных'}
      className={`-my-1.5 ${ICON_BUTTON_XS} ${isBound ? '' : 'opacity-0 group-hover:opacity-100 focus-visible:opacity-100'}`}
      onClick={onOpen} icon={Database} iconClassName={isBound ? 'text-primary' : undefined} />
  );
}
