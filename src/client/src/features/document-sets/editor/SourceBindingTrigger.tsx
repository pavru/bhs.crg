import { Icon } from '@/shared/ui/Icon';
import { Database } from '@/shared/ui/icons';
import { IconButton } from '@/shared/ui/Button';

/**
 * Значок «линзы» у подписи поля — вход в привязку к источнику, общий для скалярного
 * (`FieldSourceBinding`) и контейнерного (`ContainerFieldBinding`) поля. Непривязанное поле
 * показывает его по наведению и при фокусе с клавиатуры; привязанное — всегда, цветом primary.
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
      size="sm"
      className={`-my-1.5 ${isBound ? '' : 'opacity-0 group-hover:opacity-100 focus-visible:opacity-100'}`}
      onClick={onOpen}>
      <Icon icon={Database} size={18} className={isBound ? 'text-primary' : undefined} />
    </IconButton>
  );
}
