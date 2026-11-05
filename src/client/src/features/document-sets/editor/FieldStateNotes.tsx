import { LockedFieldIcon, LOCKED_HINT } from '../fields';
import { boundStateText } from './boundStateText';

/**
 * Подпись под запертым полем — словами, а не одним значком у заголовка.
 *
 * Значок говорит «что-то с этим полем не так, как с соседним», но не говорит ЧТО, а наведение
 * мышью — не способ узнать: читают-то глазами. Инструкция администратора обещает эту подпись во
 * всех трёх формах по схеме, и обещание должно быть верным во всех трёх (issue #958).
 *
 * У поля только для чтения те же слова — его подсказка ([UI-86]); строкой они остались у флажка и у
 * широких полей без своей подсказки (картинка, файл).
 */
export function LockedFieldNote() {
  return (
    <p className="text-label-small text-on-surface-variant mt-0.5 flex items-center gap-1">
      <LockedFieldIcon />{LOCKED_HINT}
    </p>
  );
}

/** Состояние связанного поля без значения строкой под ним — у флажка и широких полей (`boundStateText`). */
export function BoundStateHint({ loading, error }: { loading: boolean; error: boolean }) {
  return <p className="text-label-small text-on-surface-variant mt-0.5 italic">{boundStateText(loading, error)}</p>;
}
