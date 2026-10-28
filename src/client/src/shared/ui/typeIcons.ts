import { Apartment, Build, CalendarMonth, Description, LocationOn, Package2, Person, Straighten, Widgets, type IconDef } from './icons';
import type { PickType } from './TypePicker';

// Отдельным файлом от `TypePicker` (issue #858): модуль, экспортирующий и компонент, и функцию,
// теряет горячую перезагрузку — правка компонента перезагружает страницу целиком.
//
// Иконка семейства по эвристике (имя+код). Категории из данных не выводятся (нет тегов/единого
// parentId-дерева), поэтому иконку подбираем по ключевым словам; неточное совпадение безвредно.
// Экспортируется для `TypePickerField` — закрытый триггер показывает ту же иконку, что строки пикера.
export function typeIcon(t: PickType): IconDef {
  const s = `${t.name} ${t.code}`.toLowerCase();
  if (/организ|сро|надзор|подрядчик|заказчик/.test(s)) return Apartment;
  if (/персон|фио|подписант/.test(s)) return Person;
  if (/адрес|координат|объект строит/.test(s)) return LocationOn;
  if (/работ/.test(s)) return Build;
  if (/материал/.test(s)) return Package2;
  if (/единиц|измерен|угол|лаборатори/.test(s)) return Straighten;
  if (/период|срок|дата/.test(s)) return CalendarMonth;
  if (t.section.toLowerCase().includes('документ')) return Description;
  return Widgets;
}
