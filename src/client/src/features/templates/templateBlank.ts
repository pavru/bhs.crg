import { resolveEffectiveFields } from '@/shared/api/schema';
import type { DocumentType } from '@/shared/api/types';
import type { TemplateType } from '@/shared/api/templates';
interface FlatField { path: string; segments: string[]; title: string; type: string; depth: number; }

function flattenFields(
  fields: ReturnType<typeof resolveEffectiveFields>,
  allDocTypes: DocumentType[],
  prefix: string[] = [],
  depth = 0,
  visited = new Set<string>(),
): FlatField[] {
  const result: FlatField[] = [];
  for (const f of fields) {
    const segments = [...prefix, f.key];
    result.push({ path: segments.join('.'), segments, title: f.title, type: f.type, depth });
    if (f.type === 'complex' && f.typeId && !visited.has(f.typeId)) {
      const subType = allDocTypes.find(dt => dt.id === f.typeId);
      if (subType) {
        const sub = resolveEffectiveFields(subType, allDocTypes);
        result.push(...flattenFields(sub, allDocTypes, segments, depth + 1, new Set([...visited, f.typeId])));
      }
    }
  }
  return result;
}

export function templateRequisites(docType: TemplateType, allDocTypes: DocumentType[]): FlatField[] {
  const requisites = docType.printRequisites;
  const fields = flattenFields(resolveEffectiveFields(docType, allDocTypes), allDocTypes);
  if (!requisites) return fields;

  const declared = requisites.fields.map(({ path, title, type }) =>
    ({ path: path.join('.'), segments: path, title, type, depth: 0 }));
  const customFieldsKey = requisites.customFieldsKey;
  if (!customFieldsKey) return declared;

  const excluded = new Set(requisites.excludedCustomKeys?.map(key => key.toUpperCase()));
  const excludedRoot = new Set(requisites.excludedRootCustomKeys?.map(key => key.toUpperCase()));
  const custom = fields.filter(field => !excludedRoot.has(field.segments[0].toUpperCase())
      && field.segments.every(key => !excluded.has(key.toUpperCase())))
    .map(field => {
      const segments = [customFieldsKey, ...field.segments];
      return { ...field, path: segments.join('.'), segments };
    });
  return [...declared, ...custom];
}

export function requisiteSnippet(docType: TemplateType, field: FlatField): string {
  if (!docType.printRequisites) return `#get(${JSON.stringify(field.path)})`;
  const keys = field.segments.map(segment => JSON.stringify(segment)).join(', ');
  return `#{ import "systemlib.typ": print-field; print-field((${keys},)) }`;
}

export function buildBlankTypst(name: string, docType: TemplateType, allDocTypes: DocumentType[]): string {
  const fields = templateRequisites(docType, allDocTypes);
  const scalarFields = fields.filter(f => f.type !== 'complex' && f.depth === 0);
  const fieldLines = fields
    .map(f => {
      // padEnd не гарантирует разделитель, если path уже длиннее целевой ширины
      // (обычное дело для вложенных путей) — считаем ширину так, чтобы пробел был всегда.
      const width = Math.max(32 - f.depth * 2, f.path.length + 1);
      return `//   ${'  '.repeat(f.depth)}${f.path.padEnd(width)}${f.title} (${f.type})`;
    })
    .join('\n');
  if (docType.printRequisites) {
    const example = scalarFields[0];
    return `#import "systemlib.typ": *
#import "typeblocks.typ": *

// Доступные реквизиты печатной формы
${fieldLines}

= #(${JSON.stringify(name)})
${example ? requisiteSnippet(docType, example) : ''}
`;
  }
  // First scalar field to use in the title example
  const firstField = scalarFields[0]?.path ?? 'Наименование';

  return `#import "systemlib.typ": *
#import "typeblocks.typ": *

// ════════════════════════════════════════════════════════════════════
// Шаблон : ${name}
// Тип    : ${docType.name} (${docType.code})
// Движок : Typst 0.15  https://typst.app/docs
//
// ДАННЫЕ
//   Все реквизиты доступны через словарь d (json "data.json").
//   Удобный хелпер get("Ключ.Вложенный") обрабатывает вложенность.
//
// СКАЛЯРНОЕ ПОЛЕ
//   #get("${firstField}")
//
// МАССИВ (таблица)
//   #let строки = d.at("СписокПолей", default: ())
//   #for с in строки [ #с.at("Поле") ]
//
// РАЗРЫВ СТРАНИЦЫ
//   #pagebreak()
//
// ДОСТУПНЫЕ РЕКВИЗИТЫ
${fieldLines || '//   (нет реквизитов)'}
// ════════════════════════════════════════════════════════════════════

#let d = json("data.json")

// get(path) -- доступ к вложенному полю по точечному пути
#let get(path) = {
  let keys = str(path).split(".")
  let cur = d
  for k in keys {
    cur = if type(cur) == dictionary { cur.at(k, default: none) } else { none }
  }
  if cur == none { "" }
  else if type(cur) == str { cur }
  else { repr(cur) }
}

// ── Настройки страницы ───────────────────────────────────────────────────────
// Формат/поля страницы задаются прямо здесь, в Typst-коде шаблона.

#set page(
  paper: "a4",
  margin: (top: 20mm, right: 15mm, bottom: 20mm, left: 30mm),

  // Шапка: оборачиваем в context чтобы counter(page) был доступен
  header: context [
    #set text(size: 9pt, font: "Times New Roman")
    #grid(
      columns: (1fr, auto),
      // Левая часть -- организация / объект из реквизитов
      [#get("Организация.Наименование")],
      // Правая -- номер страницы
      [стр. #counter(page).display() из #counter(page).final().first()],
    )
    #v(1pt)
    #line(length: 100%, stroke: 0.5pt)
  ],

  footer: context [
    #line(length: 100%, stroke: 0.5pt)
    #v(1pt)
    #set text(size: 8pt, font: "Times New Roman")
    #get("Объект.Наименование")
  ],
)

#set text(font: "Times New Roman", size: 12pt, lang: "ru")
#set par(justify: true, leading: 0.65em)

// ── Содержимое ───────────────────────────────────────────────────────────────

#align(center)[
  #text(weight: "bold", size: 14pt)[${docType.name.toUpperCase()}]
]

#v(5mm)

= #get("${firstField}")

Замените этот блок содержимым документа.

// ── Пример таблицы ───────────────────────────────────────────────────────────
// #let items = d.at("СписокМатериалов", default: ())
// #table(
//   columns: (auto, 1fr, auto, auto),
//   table.header([№], [Наименование], [Ед.изм.], [Кол-во]),
//   ..items.enumerate().map(((i, m)) => (
//     str(i + 1),
//     m.at("Наименование", default: ""),
//     m.at("ЕдИзм",        default: ""),
//     str(m.at("Количество", default: "")),
//   )).flatten()
// )

// ── Пример блока подписей ────────────────────────────────────────────────────
// #v(10mm)
// #grid(
//   columns: (1fr, 1fr),
//   gutter: 10mm,
//   [
//     Сдал: \
//     #v(8mm)
//     #line(length: 100%, stroke: 0.5pt)
//     #text(size: 9pt)[#get("Подрядчик.Представитель.ФИО")]
//   ],
//   [
//     Принял: \
//     #v(8mm)
//     #line(length: 100%, stroke: 0.5pt)
//     #text(size: 9pt)[#get("ТехническийНадзор.Представитель.ФИО")]
//   ],
// )
`;
}

