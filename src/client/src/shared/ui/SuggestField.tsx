import type { SuggestSource } from '../api/fieldSuggestions';
import { PlainField, SuggestBox, type SuggestFieldBaseProps } from './SuggestBox';
import { useAddressFeed, useOptionsFeed } from './suggestFeeds';
import type { SuggestItem } from './suggestMatch';

/**
 * Поле с подсказкой (эпик #1527, issue #1531, требования UI-1…UI-8 и UI-55 домена `sharedui`). Значение —
 * свободный текст: список лишь предлагает. Откуда — одно из двух:
 * <ul>
 *   <li><b>`source`</b> — адрес подсказок по ранее введённому: то, что уже вводили в этом поле других
 *     записей;</li>
 *   <li><b>`options`</b> — готовый перечень, который экран уже знает (UI-55): ни запроса, ни задержки,
 *     у пунктов — подписи групп и приписки. Так подсказывает поле значения условия отбора.</li>
 * </ul>
 * Выбор ЗАПИСИ справочника сюда не относится — это `RefPickerModal` и кандидаты.
 *
 * <p>Вид — вариант А макета «Подсказка по введённому» (выбор владельца 23.10.2026): слой `Select` в
 * 4 px под полем, не уже поля. Поведение — APG «combobox, list autocomplete, manual selection»:</p>
 * <ul>
 *   <li><b>Открывается вводом, а не фокусом.</b> Tab по форме из двадцати полей не открывает двадцать
 *     списков. В форме ещё ↓, везде — Alt+↓. В ячейке сетки (`compact`) ↓ при закрытом списке — переход
 *     по строкам: сетка уступает стрелки только `aria-expanded="true"`.</li>
 *   <li><b>Принятие — только явное.</b> Ни один пункт не активен до стрелки; листание текст не меняет и
 *     `onChange` не зовёт (у форм есть «тронуто» и охрана ухода). Enter или щелчок — ровно один
 *     `onChange`. Tab закрывает без принятия: иначе Tab по форме молча подставлял бы чужие значения.</li>
 *   <li><b>Тишина вместо пустоты и отказа.</b> Пусто, 403, 503 — списка нет, без «не найдено».</li>
 *   <li><b>Не слой истории.</b> Список — часть поля, как `Select`: на телефоне первая «Назад» убирает
 *     клавиатуру, поле теряет фокус, и список закрывается вместе с ним.</li>
 * </ul>
 */
export type SuggestFieldProps = SuggestFieldBaseProps & (
  | {
    /** Чьё поле. `null` — поле не подсказывается (правило `Suggestable`): обычное поле, адрес не зовётся. */
    source: SuggestSource | null;
    options?: never;
  }
  | {
    /** Готовый перечень в порядке показа; одинаковые значения схлопывает тот, кто его собрал. */
    options: readonly SuggestItem[];
    source?: never;
  });

export function SuggestField(props: SuggestFieldProps) {
  if (props.options) {
    const { options, source: _source, ...field } = props;
    return <OptionsField {...field} options={options} />;
  }
  const { source, options: _options, ...field } = props;
  // Поле без источника — обычное: ни запроса, ни подписки на кэш (и без QueryClientProvider вокруг).
  return source ? <AddressField {...field} source={source} /> : <PlainField {...field} />;
}

function AddressField({ source, ...field }: SuggestFieldBaseProps & { source: SuggestSource }) {
  const feed = useAddressFeed(source, field.value, field.label);
  return <SuggestBox {...field} feed={feed} />;
}

function OptionsField({ options, ...field }: SuggestFieldBaseProps & { options: readonly SuggestItem[] }) {
  const feed = useOptionsFeed(options, field.value, field.label);
  return <SuggestBox {...field} feed={feed} />;
}
