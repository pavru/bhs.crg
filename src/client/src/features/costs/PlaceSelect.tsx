import { withArchiveWord } from '@/shared/ui/archive';
import { Select, SelectGroup, SelectItem } from '@/shared/ui/Select';
import { pickSite, REVEAL_CHOICE, useSiteChoices } from '@/shared/siteFilter/siteReveal';
import { RevealItem, SiteSelectItems } from '@/shared/siteFilter/SiteOptions';
import { choiceOf, fromChoice, placeName, type Place, type Places } from './places';

/**
 * Выбор цели разноски (задача F3, issue #1087): стройки и статьи вне строек — двумя группами, чтобы «Склад»
 * не читался как стройка. Раздел выбирают отдельным полем: он бывает только у стройки.
 *
 * <p>⚠️ Выбранная цель, которой больше нет (стройку удалили, статью убрали), остаётся пунктом с честной
 * подписью. Без него значение не совпало бы ни с одним пунктом, поле показало бы «— выберите —», а уехала бы
 * удалённая цель — и выглядело бы это как «часть без цели».</p>
 *
 * <p>Архивную статью выбор не предлагает (issue #1185) — кроме той, что стоит сейчас: она остаётся
 * пунктом «Склад — в архиве». Сменили цель — пункт исчезает. Помнить прежнее значение поле не
 * берётся: часть полей сохраняет выбор сразу, и после сохранения архивная статья у счёта уже не
 * стоит — вернуть её значило бы предложить то, что сервер отвергнет (ревью PR #1227).</p>
 *
 * <p>Стройки урезаны фильтром «Избранные стройки» (issue #1219): выход «Показать все стройки» —
 * последним пунктом, после статей. Стоящая неизбранная стройка — группой «Не в избранном», а НЕ веткой
 * потери: потеря значит «нет в справочнике», а эта стройка в нём есть. Статьи не фильтруются.</p>
 *
 * <p>Поле стоит в плотных строках разноски — компактный `Select` (issue #1353). `placeholder` —
 * пункт «значения нет», а не только подсказка: выбрав его, цель снимают.</p>
 */
export function PlaceSelect({ value, places, label, placeholder = '— выберите —', disabled, className = 'w-full', onChange }: {
  value: Place;
  places: Places;
  label: string;
  placeholder?: string;
  disabled?: boolean;
  /** Ширина поля; оформление — у `Select`. */
  className?: string;
  onChange: (place: Place) => void;
}) {
  const current = choiceOf(value);
  const articles = places.articles?.filter(a => !a.archived || a.id === value.article);
  const list = value.article ? places.articles : places.sites;
  const id = value.article ?? value.construction;
  const sites = useSiteChoices(places.sites, value.article ? null : value.construction);
  // Список ещё не пришёл — «удалена» было бы неправдой: «ещё не знаем» не то же, что «нет».
  const orphan = current === '' ? null
    : list === undefined ? '…'
      : list.some(item => item.id === id) ? null : placeName({ ...value, section: null }, places);

  return (
    <Select size="sm" value={current} aria-label={label} disabled={disabled} className={className}
      emptyItem={placeholder} stayOpenOn={REVEAL_CHOICE}
      onValueChange={choice => pickSite(choice, sites.reveal, picked => onChange(fromChoice(picked)))}>
      {orphan !== null && <SelectItem value={current}>{orphan}</SelectItem>}
      <SiteSelectItems choices={sites} valueFor={site => choiceOf({ construction: site, section: null, article: null })} />
      {!!articles?.length && (
        <SelectGroup label="Вне строек">
          {articles.map(a => (
            <SelectItem key={a.id} value={choiceOf({ construction: null, section: null, article: a.id })}>
              {withArchiveWord(a.name, a.archived)}
            </SelectItem>
          ))}
        </SelectGroup>
      )}
      <RevealItem hidden={sites.hidden} />
    </Select>
  );
}
