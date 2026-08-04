import type { DocumentType } from '@/shared/api/types';
import { resolveEffectiveFields, type SchemaField } from '@/shared/api/schema';
import type { FieldTypeDefs } from '@/shared/utils/fieldDisplay';
import type { RecognitionFieldReq } from '@/shared/api/qualityDocs';

/**
 * Что распознаванию рассказывают про поля типа и как читают его ответ (issue #654).
 *
 * Прежде уходило `{ path, title, type, options: f.options }`, и двум видам полей этого не хватало:
 *
 * 1. **Перечисление из реестра** (#59). Варианты живут в `EnumType`, у поля в схеме только `typeId`,
 *    а `options` там пусто — клиентский `resolveEffectiveFields` варианты не подставляет. Промпт
 *    оставался без строки «(варианты: …)», и модель отвечала свободной формулировкой вроде
 *    «Сертификат соответствия». В реквизиты попадала ПОДПИСЬ вместо КОДА: `Select` показывал
 *    плейсхолдер (значение не совпадало ни с одним пунктом), человек видел пустое обязательное поле,
 *    а в данных лежала подпись и сохранялась дальше — в том числе по безголовому пути импорта, где
 *    формы нет вовсе. В PDF это не всплывало: резолвер меток не трогает значения вне списка кодов.
 * 2. **Примитив** (#60). Уходило `type: 'primitive'` — по нему не понять, дата за ним, число или
 *    строка с шаблоном, и единственным ориентиром оставалась общая фраза «Даты возвращай в ISO».
 *
 * Модель видит ПОДПИСИ (их же она прочитает в скане), а в данные кладём КОДЫ — обратное отображение
 * и есть вторая половина контракта.
 */
export interface RecognitionPlan {
  fields: RecognitionFieldReq[];
  /**
   * Путь поля → «нормализованная подпись → код». Только для перечислений; пусто, если их нет.
   * Коды тоже кладём ключами: модель вправе ответить и кодом, и его же подписью.
   */
  enumCodes: Record<string, Record<string, string>>;
  /**
   * Пути полей, у которых объявлен тип-перечисление, но его определения в `defs` не оказалось —
   * реестр не загрузился, запрос упал или вызывающий его вовсе не передал.
   *
   * Молчать об этом нельзя: без определения промпт уходит без вариантов, модель отвечает свободной
   * формулировкой, и отобразить её обратно в код нечем — то есть ровно дефект #654 возвращается,
   * причём тихо. Вызывающий обязан это увидеть и не запускать распознавание вслепую.
   */
  unresolvedEnums: string[];
}

const SKIPPED_TYPES = new Set(['array', 'doc-ref', 'doc-array', 'image', 'file']);

/** Разворачивает поля типа в плоские «листья» (путь через точку) для распознавания. */
export function buildRecognitionFields(
  fields: SchemaField[], allDocTypes: DocumentType[], defs: FieldTypeDefs = {},
  prefix = '', depth = 0,
): RecognitionPlan {
  if (depth > 3) return { fields: [], enumCodes: {}, unresolvedEnums: [] };

  const out: RecognitionFieldReq[] = [];
  const enumCodes: RecognitionPlan['enumCodes'] = {};
  const unresolvedEnums: string[] = [];

  for (const f of fields) {
    const path = prefix ? `${prefix}.${f.key}` : f.key;

    if (f.type === 'complex' && f.typeId) {
      const ct = allDocTypes.find(d => d.id === f.typeId);
      if (!ct) continue;
      const inner = buildRecognitionFields(
        resolveEffectiveFields(ct, allDocTypes), allDocTypes, defs, path, depth + 1);
      out.push(...inner.fields);
      Object.assign(enumCodes, inner.enumCodes);
      unresolvedEnums.push(...inner.unresolvedEnums);
      continue;
    }

    if (SKIPPED_TYPES.has(f.type)) continue;

    if (f.type === 'enum') {
      // Тип из реестра объявлен, а определения нет — говорим об этом вслух, а не подставляем
      // молча пустой список (тогда поле выглядело бы обычным, а вернулось бы подписью).
      if (f.typeId && !defs.enumTypes?.some(et => et.id === f.typeId)) unresolvedEnums.push(path);
      const options = enumOptionsOf(f, defs);
      // Модели показываем подписи — их она и увидит в скане; пустой список промпт всё равно
      // пропустит, и поле останется без подсказки, как было.
      out.push({ path, title: f.title, type: f.type, options: options.map(o => o.label) });
      if (options.length > 0) enumCodes[path] = codeIndex(options);
      continue;
    }

    if (f.type === 'primitive') {
      // Базовый тип вместо бесполезного «primitive»: за ним стоит дата, число или строка, и общая
      // подсказка про формат становится предметной.
      const def = defs.primitiveTypes?.find(pt => pt.id === f.typeId);
      out.push({ path, title: f.title, type: def?.baseType ?? f.type });
      continue;
    }

    out.push({ path, title: f.title, type: f.type, options: f.options });
  }

  return { fields: out, enumCodes, unresolvedEnums };
}

/**
 * Ответ модели → значения для реквизитов: подписи перечислений заменяются кодами.
 *
 * Неопознанное значение оставляем КАК ЕСТЬ, а не выбрасываем: распознавание — единственное, что
 * прочитало скан, и терять его результат из-за расхождения формулировок хуже, чем показать
 * расхождение. Его и покажут — форма подсветит несоответствие типу, аудит значений (#644) найдёт
 * его же в сохранённой записи.
 */
export function codesFromLabels(
  values: Record<string, string>, enumCodes: RecognitionPlan['enumCodes'],
): Record<string, string> {
  const out: Record<string, string> = {};
  for (const [path, value] of Object.entries(values)) {
    const index = enumCodes[path];
    out[path] = index ? index[normalize(value)] ?? value : value;
  }
  return out;
}

/** Варианты перечисления: из реестра типов (#59), иначе из legacy-списка в самой схеме. */
function enumOptionsOf(f: SchemaField, defs: FieldTypeDefs): { code: string; label: string }[] {
  const def = f.typeId ? defs.enumTypes?.find(et => et.id === f.typeId) : undefined;
  if (def) return def.values.map(v => ({ code: v.code, label: v.label }));
  return (f.options ?? []).filter(o => o !== '').map(o => ({ code: o, label: o }));
}

/** Подпись И код ведут к коду: модель вправе ответить любым из них. Подпись выигрывает при совпадении. */
function codeIndex(options: { code: string; label: string }[]): Record<string, string> {
  const index: Record<string, string> = {};
  for (const o of options) index[normalize(o.code)] = o.code;
  for (const o of options) index[normalize(o.label)] = o.code;
  return index;
}

function normalize(s: string): string {
  return s.trim().toLowerCase().replace(/\s+/g, ' ').replace(/ё/g, 'е');
}
