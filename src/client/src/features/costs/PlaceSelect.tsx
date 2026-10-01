import { choiceOf, fromChoice, placeName, type Place, type Places } from './places';

/**
 * Выбор цели разноски (задача F3, issue #1087): стройки и статьи вне строек — двумя группами, чтобы «Склад»
 * не читался как стройка. Раздел выбирают отдельным полем: он бывает только у стройки.
 *
 * <p>⚠️ Выбранная цель, которой больше нет (стройку удалили, статью убрали), остаётся пунктом с честной
 * подписью. Без него значение не совпало бы ни с одним пунктом, поле показало бы «— выберите —», а уехала бы
 * удалённая цель — и выглядело бы это как «часть без цели».</p>
 */
export function PlaceSelect({ value, places, label, placeholder = '— выберите —', disabled, className, onChange }: {
  value: Place;
  places: Places;
  label: string;
  placeholder?: string;
  disabled?: boolean;
  className: string;
  onChange: (place: Place) => void;
}) {
  const current = choiceOf(value);
  const list = value.article ? places.articles : places.sites;
  const id = value.article ?? value.construction;
  // Список ещё не пришёл — «удалена» было бы неправдой: «ещё не знаем» не то же, что «нет».
  const orphan = current === '' ? null
    : list === undefined ? '…'
      : list.some(item => item.id === id) ? null : placeName({ ...value, section: null }, places);

  return (
    <select value={current} aria-label={label} disabled={disabled} className={className}
      onChange={e => onChange(fromChoice(e.target.value))}>
      <option value="">{placeholder}</option>
      {orphan !== null && <option value={current}>{orphan}</option>}
      {!!places.sites?.length && (
        <optgroup label="Стройки">
          {places.sites.map(s => <option key={s.id} value={choiceOf({ construction: s.id, section: null, article: null })}>{s.name}</option>)}
        </optgroup>
      )}
      {!!places.articles?.length && (
        <optgroup label="Вне строек">
          {places.articles.map(a => <option key={a.id} value={choiceOf({ construction: null, section: null, article: a.id })}>{a.name}</option>)}
        </optgroup>
      )}
    </select>
  );
}
