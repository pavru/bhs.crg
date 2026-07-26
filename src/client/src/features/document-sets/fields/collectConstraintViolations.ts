import type { DocumentType, PrimitiveTypeDef } from '@/shared/api/types';
import { resolveEffectiveFields, type SchemaField } from '@/shared/api/schema';
import { validateConstraint } from './PrimitiveInput';

/**
 * Нарушения ограничений примитивов по ВСЕМУ документу, включая строки таблиц и составные поля
 * (issue #463).
 *
 * Проверка существовала и работала, но вызывалась только для полей верхнего уровня: внутри строк
 * таблицы контрол получал описание типа и рисовался правильно, а результат ввода не проверял никто.
 * Так в поле «Цело число» и оказался «3.3».
 *
 * Ключ нарушения — путь вида `Работы[3].ПорядковыйНомер`: он и адресует место, и служит ключом для
 * подсветки конкретного контрола.
 */
export function collectConstraintViolations(
  values: Record<string, unknown>,
  fields: SchemaField[],
  allDocTypes: DocumentType[],
  primitiveTypes: PrimitiveTypeDef[],
): Record<string, string> {
  const out: Record<string, string> = {};
  walk(values, fields, '', out, allDocTypes, primitiveTypes, 0);
  return out;
}

/** Защита от патологически вложенных схем — та же, что у серверного сканера. */
const MAX_DEPTH = 6;

function walk(
  obj: Record<string, unknown>,
  fields: SchemaField[],
  prefix: string,
  out: Record<string, string>,
  allDocTypes: DocumentType[],
  primitiveTypes: PrimitiveTypeDef[],
  depth: number,
) {
  if (depth >= MAX_DEPTH) return;

  for (const f of fields) {
    // Расчётное поле производное: претензия к его значению — претензия к выражению.
    if (f.computed) continue;

    const value = obj?.[f.key];
    if (value == null) continue;
    const path = prefix ? `${prefix}.${f.key}` : f.key;

    if (f.type === 'array' && Array.isArray(value)) {
      const inner = fieldsOf(f.typeId, allDocTypes);
      if (inner.length === 0) continue;
      value.forEach((row, i) => {
        if (row && typeof row === 'object')
          walk(row as Record<string, unknown>, inner, `${path}[${i}]`, out, allDocTypes, primitiveTypes, depth + 1);
      });
      continue;
    }

    if (f.type === 'complex' && typeof value === 'object' && !Array.isArray(value)) {
      const inner = fieldsOf(f.typeId, allDocTypes);
      if (inner.length > 0)
        walk(value as Record<string, unknown>, inner, path, out, allDocTypes, primitiveTypes, depth + 1);
      continue;
    }

    if (f.type !== 'primitive' || !f.typeId) continue;
    const def = primitiveTypes.find(p => p.id === f.typeId);
    if (!def) continue;

    const err = validateConstraint(value, def);
    if (err) out[path] = err;
  }
}

function fieldsOf(typeId: string | null | undefined, allDocTypes: DocumentType[]): SchemaField[] {
  if (!typeId) return [];
  const t = allDocTypes.find(x => x.id === typeId);
  return t ? resolveEffectiveFields(t, allDocTypes) : [];
}

/**
 * Путь нарушения человеческими названиями: «Адреса → Юридический адрес → Сайт» вместо
 * «ЮридическийАдрес.Web».
 *
 * Технический путь адресует место точно, но читателю не говорит ничего: ключи он не видел никогда, а
 * поле может лежать в свёрнутой группе. Заголовки — то, что у него перед глазами.
 */
export function describeViolationPath(
  path: string,
  fields: SchemaField[],
  allDocTypes: DocumentType[],
): string {
  const parts: string[] = [];
  let current = fields;

  for (const segment of path.split('.')) {
    // Индекс строки таблицы показываем номером, а не нулевой базой: в форме строки нумеруются с единицы.
    const m = /^(.+?)\[(\d+)\]$/.exec(segment);
    const key = m ? m[1] : segment;
    const f = current.find(x => x.key === key);
    if (!f) { parts.push(key); break; }

    parts.push(m ? `${f.title ?? key} №${Number(m[2]) + 1}` : (f.title ?? key));
    current = fieldsOf(f.typeId, allDocTypes);
    if (current.length === 0) break;
  }

  return parts.join(' → ');
}

/** Корневое поле нарушения — по нему находится группа формы, которую надо раскрыть. */
export function violationRootKey(path: string): string {
  return path.split(/[.[]/)[0];
}
