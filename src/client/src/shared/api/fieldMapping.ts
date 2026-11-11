import { useQuery } from '@tanstack/react-query';
import { apiClient } from './client';

/**
 * Экран «Сопоставление полей» (issue #1101): что включённые модули читают из справочников ядра и
 * чем это сейчас обеспечено. Считает сервер — тем же расчётом, которым справочник читает сам модуль;
 * клиент состояний не выводит.
 */
export interface CoverageType {
  id: string;
  name: string;
  code: string;
  kind: string;
}

export interface CoverageField {
  key: string;
  title: string;
  /** Вид поля схемы: `string`, `complex`… */
  type: string;
  /** Тип, в схеме которого поле объявлено: тэг лежит там, и запись правит ЕГО схему. */
  declaredIn: CoverageType;
  /** Вид поля подходит тэгу. Неподходящее приходит тоже — оно может нести тэг сейчас. */
  fits: boolean;
  /** Тэг потребности сейчас стоит на этом поле. */
  tagged: boolean;
}

export type CoverageState = 'mapped' | 'absent' | 'violated';

export interface CoverageRow {
  tag: string;
  label: string;
  description: string;
  required: boolean;
  /** Что не работает без поля — текст модуля. */
  withoutIt: string;
  state: CoverageState;
  /** Что именно не так. `DiffersInSubtype` лежит в схеме подтипа — здесь не чинится. */
  fault: 'None' | 'Absent' | 'Several' | 'WrongKind' | 'DiffersInSubtype';
  /** Почему не обеспечена — тем же текстом, каким отказывает модуль; null у сопоставленной. */
  reason: string | null;
  /** Ключ поля, несущего тэг целиком; null, пока строка не сопоставлена либо тэг стоит на части. */
  key: string | null;
  /** Виды полей, которые тэг допускает. Пусто — любой. */
  kinds: string[];
  fields: CoverageField[];
  /**
   * Тэг стоит на ЧАСТИ составного поля (issue #1473) — сокращённое название внутри «Наименования».
   * Такую строку экран не правит: тэг лежит в схеме составного типа, и запись поля справочника его
   * не сняла бы (ревью PR #1898). Экран называет часть и тип, в редакторе которого тэг меняется.
   */
  part?: CoveragePart | null;
}

/** Часть составного поля с тэгом: поле справочника, ключ части и тип, в схеме которого тэг стоит. */
export interface CoveragePart {
  whole: string;
  key: string;
  in: CoverageType;
}

export interface CoverageGroup {
  role: string;
  label: string;
  purpose: string;
  withoutIt: string;
  state: CoverageState;
  reason: string | null;
  /** Носитель роли; null, пока справочник не назначен. */
  type: CoverageType | null;
  /** Кого можно назначить справочником. Непусто только когда роль не несёт никто. */
  candidates: CoverageType[];
  /** Поля. Пусто, пока справочник не назначен: сопоставлять поле не в чем. */
  rows: CoverageRow[];
}

export interface ModuleCoverage {
  code: string;
  title: string;
  total: number;
  unmet: number;
  notWorking: string[];
  groups: CoverageGroup[];
}

export const FIELD_MAPPING_KEY = ['field-mapping'] as const;

export function useFieldMapping() {
  return useQuery<ModuleCoverage[]>({
    queryKey: FIELD_MAPPING_KEY,
    queryFn: () => apiClient.get('/field-mapping').then(r => r.data.modules),
  });
}
