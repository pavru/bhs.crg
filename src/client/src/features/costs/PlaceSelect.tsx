import { useState } from 'react';
import { withArchiveWord } from '@/shared/ui/archive';
import { choiceOf, fromChoice, placeName, type Place, type Places } from './places';

/**
 * Выбор цели разноски (задача F3, issue #1087): стройки и статьи вне строек — двумя группами, чтобы «Склад»
 * не читался как стройка. Раздел выбирают отдельным полем: он бывает только у стройки.
 *
 * <p>⚠️ Выбранная цель, которой больше нет (стройку удалили, статью убрали), остаётся пунктом с честной
 * подписью. Без него значение не совпало бы ни с одним пунктом, поле показало бы «— выберите —», а уехала бы
 * удалённая цель — и выглядело бы это как «часть без цели».</p>
 *
 * <p>Архивную статью выбор не предлагает (issue #1185) — кроме той, что уже стоит: она остаётся
 * пунктом «Склад — в архиве», и держится он за значением, с которым поле открыли, а не за текущим:
 * заменил, передумал — вернул. Сервер это примет: стоявшую цель он не перепроверяет.</p>
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
  const [opened] = useState(value.article);
  const articles = places.articles?.filter(a => !a.archived || a.id === opened || a.id === value.article);
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
      {!!articles?.length && (
        <optgroup label="Вне строек">
          {articles.map(a => (
            <option key={a.id} value={choiceOf({ construction: null, section: null, article: a.id })}>
              {withArchiveWord(a.name, a.archived)}
            </option>
          ))}
        </optgroup>
      )}
    </select>
  );
}
