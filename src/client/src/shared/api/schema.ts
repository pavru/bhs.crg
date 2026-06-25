import type { DocumentType } from './types';

export interface SchemaField {
  key: string;
  title: string;
  /** Primitive type, 'enum' for a fixed list, 'complex' for composite type, 'primitive' for user-defined constrained type, 'array' for repeating rows, 'doc-ref'/'doc-array' for document instance links */
  type: 'string' | 'text' | 'number' | 'date' | 'boolean' | 'enum' | 'complex' | 'primitive' | 'array' | 'doc-ref' | 'doc-array' | 'image' | 'file';
  /** Set when type === 'complex', 'array', or 'primitive'. ID of the composite DocumentType or PrimitiveType. */
  typeId?: string;
  /** Set when type === 'enum'. List of allowed string values. */
  options?: string[];
  required: boolean;
  /** Pre-filled value for new entries. For 'array' fields use [] as default. */
  defaultValue?: unknown;
  /** System meta-tag: field is auto-populated after generation. Values: 'pageCount' | 'generatedAt' | 'generatedBy' */
  metaTag?: string;
}

export interface FieldGroup {
  key: string;
  title: string;
  /** Ordered list of field keys that belong to this group (inherited + own). */
  fieldKeys: string[];
}

/** A named Typst rendering function for a Composite type. */
export interface TypstRender {
  /** Display label shown in the UI, e.g. "Краткое", "Для печати" */
  name: string;
  /** Typst function name (ASCII), e.g. "render_org_short" */
  fnName: string;
  /** Complete Typst expression used as the function body, e.g. "[*#it.НаимКраткое*]" */
  block: string;
}

export interface SchemaDefinition {
  fields: SchemaField[];
  groups?: FieldGroup[];
  excludedFields?: string[];
  fieldOverrides?: Record<string, { required?: boolean; defaultValue?: unknown }>;
  /** Named Typst rendering functions for Composite types (generated as preamble before template). */
  typstRenders?: TypstRender[];
}

/**
 * Returns fields organized by their groups.
 * Fields not in any group appear first (ungrouped), then each defined group.
 * If no groups defined, returns a single ungrouped section.
 */
export function groupEffectiveFields(
  fields: SchemaField[],
  schema: Record<string, unknown>,
): Array<{ key: string; title: string | null; fields: SchemaField[] }> {
  const def = schema as unknown as SchemaDefinition;
  const groups = def.groups ?? [];
  if (groups.length === 0) return [{ key: '__all__', title: null, fields }];

  const groupedKeys = new Set(groups.flatMap(g => g.fieldKeys));
  const ungrouped = fields.filter(f => !groupedKeys.has(f.key));
  const result: Array<{ key: string; title: string | null; fields: SchemaField[] }> = [];

  if (ungrouped.length > 0) result.push({ key: '__ungrouped__', title: null, fields: ungrouped });

  for (const group of groups) {
    const gFields = group.fieldKeys
      .map(k => fields.find(f => f.key === k))
      .filter((f): f is SchemaField => f != null);
    if (gFields.length > 0) result.push({ key: group.key, title: group.title, fields: gFields });
  }
  return result;
}

/** Parses own fields from a raw schema object. */
export function parseSchemaFields(schema: Record<string, unknown>): SchemaField[] {
  const fields = schema?.fields;
  if (!Array.isArray(fields)) return [];
  return (fields as Partial<SchemaField>[]).map(f => ({
    key: f.key ?? '',
    title: f.title ?? '',
    type: (f.type as SchemaField['type']) ?? 'string',
    typeId: f.typeId,
    options: f.options,
    required: f.required ?? false,
    defaultValue: f.defaultValue,
    metaTag: f.metaTag,
  }));
}

/**
 * Resolves the effective (merged) field list for a document type,
 * walking the inheritance chain: parent fields first, then own fields.
 * Applies excludedFields and fieldOverrides from the child schema.
 */
export function resolveEffectiveFields(
  docType: DocumentType,
  allDocTypes: DocumentType[],
): SchemaField[] {
  const schema = docType.schema as unknown as SchemaDefinition;
  const ownFields = parseSchemaFields(docType.schema);

  if (!docType.parentId) return ownFields;

  const parent = allDocTypes.find(dt => dt.id === docType.parentId);
  if (!parent) return ownFields;

  const parentFields = resolveEffectiveFields(parent, allDocTypes);
  const excluded = new Set(schema.excludedFields ?? []);
  const overrides = schema.fieldOverrides ?? {};

  const inheritedFields = parentFields
    .filter(f => !excluded.has(f.key))
    .map(f => {
      const ov = overrides[f.key];
      if (!ov) return f;
      return {
        ...f,
        ...(ov.required !== undefined ? { required: ov.required } : {}),
        ...(ov.defaultValue !== undefined ? { defaultValue: ov.defaultValue } : {}),
      };
    });

  const inheritedKeys = new Set(inheritedFields.map(f => f.key));
  return [
    ...inheritedFields,
    ...ownFields.filter(f => !inheritedKeys.has(f.key)),
  ];
}

/** Returns true if childId equals parentId or has parentId anywhere in its ancestor chain. */
export function isSubtypeOf(childId: string, parentId: string, allDocTypes: DocumentType[]): boolean {
  if (childId === parentId) return true;
  const child = allDocTypes.find(t => t.id === childId);
  if (!child?.parentId) return false;
  return isSubtypeOf(child.parentId, parentId, allDocTypes);
}

/**
 * True for fields that map 1:1 to a single scalar value — i.e. can be bound to one
 * dataset column. Excludes containers (array/complex) and document references.
 */
export function isScalarField(f: SchemaField): boolean {
  return f.type !== 'array' && f.type !== 'complex' && f.type !== 'doc-ref' && f.type !== 'doc-array';
}

/** Returns a record pre-filled with defaultValue for each field that has one. */
export function getDefaultValues(fields: SchemaField[]): Record<string, unknown> {
  const result: Record<string, unknown> = {};
  for (const f of fields) {
    if (f.defaultValue !== undefined) result[f.key] = f.defaultValue;
  }
  return result;
}

/** Checks whether a field value is considered "missing" for validation purposes. */
export function isFieldMissing(field: SchemaField, value: unknown): boolean {
  if (!field.required) return false;
  if (field.type === 'boolean') return false;
  if (field.type === 'complex') {
    return value == null || (typeof value === 'object' && Object.keys(value as object).length === 0);
  }
  return value == null || String(value).trim() === '';
}
