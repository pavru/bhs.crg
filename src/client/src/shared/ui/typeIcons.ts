import {
  Boxes, FileText, Building2, User, MapPin, Wrench, Package, Ruler, CalendarDays,
  type LucideIcon,
} from 'lucide-react';
import type { PickType } from './TypePicker';

// Отдельным файлом от `TypePicker` (issue #858): модуль, экспортирующий и компонент, и функцию,
// теряет горячую перезагрузку — правка компонента перезагружает страницу целиком.
//
// Иконка семейства по эвристике (имя+код). Категории из данных не выводятся (нет тегов/единого
// parentId-дерева), поэтому иконку подбираем по ключевым словам; неточное совпадение безвредно.
// Экспортируется для `TypePickerField` — закрытый триггер показывает ту же иконку, что строки пикера.
export function typeIcon(t: PickType): LucideIcon {
  const s = `${t.name} ${t.code}`.toLowerCase();
  if (/организ|сро|надзор|подрядчик|заказчик/.test(s)) return Building2;
  if (/персон|фио|подписант/.test(s)) return User;
  if (/адрес|координат|объект строит/.test(s)) return MapPin;
  if (/работ/.test(s)) return Wrench;
  if (/материал/.test(s)) return Package;
  if (/единиц|измерен|угол|лаборатори/.test(s)) return Ruler;
  if (/период|срок|дата/.test(s)) return CalendarDays;
  if (t.section.toLowerCase().includes('документ')) return FileText;
  return Boxes;
}
