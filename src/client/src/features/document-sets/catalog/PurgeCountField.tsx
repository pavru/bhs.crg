import { TextField } from '@/shared/ui/TextField';

const MISMATCH = 'Не совпадает с числом в строке «Всего ссылок».';

/**
 * Поле подтверждения очистки — число теряемых ссылок, перепечатанное из строки «Всего ссылок». Одно на очистку
 * записи и пакетную: слова, порядок проверки и объявление ошибки у них обязаны совпадать.
 *
 * <p>type=text, а не number: без стрелок и прокрутки колесом; на телефоне цифровую клавиатуру даёт inputMode.
 * Ошибка показывается после ухода из поля или Enter (`onSettle`): кнопка при несовпадении выключена, и Enter форму
 * не отправит — без этого нажатие не делало бы ничего и не объясняло почему (ревью PR #1246).</p>
 */
export function PurgeCountField({ value, disabled, mismatch, onChange, onSettle }: {
  value: string;
  disabled: boolean;
  mismatch: boolean;
  onChange: (value: string) => void;
  onSettle: () => void;
}) {
  return (
    <div className="mt-4">
      <TextField label="Число теряемых ссылок" type="text" inputMode="numeric" autoComplete="off" autoFocus
        value={value} disabled={disabled} className="tabular-nums"
        onChange={e => onChange(e.target.value)}
        onBlur={onSettle}
        onKeyDown={e => { if (e.key === 'Enter') onSettle(); }}
        error={mismatch ? MISMATCH : undefined} hint="Введите число из строки «Всего ссылок»." />
      {/* Ошибка поля приходит после ухода из него — её объявляем: текст под полем читалка сама не прочтёт. */}
      <span role="alert" className="sr-only">{mismatch ? MISMATCH : ''}</span>
    </div>
  );
}
