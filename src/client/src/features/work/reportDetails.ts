export function detailValues(details: unknown): Record<string, unknown> {
  return details && typeof details === 'object' && !Array.isArray(details)
    ? details as Record<string, unknown> : {};
}
