/** Converts ISO date (YYYY-MM-DD) → Russian display format (DD.MM.YYYY). Passes through anything else. */
export function isoToRu(iso: string | null | undefined): string {
  if (!iso) return '';
  const m = /^(\d{4})-(\d{2})-(\d{2})/.exec(iso);
  return m ? `${m[3]}.${m[2]}.${m[1]}` : iso;
}

/** Converts Russian format (DD.MM.YYYY) → ISO (YYYY-MM-DD). Passes through incomplete/other strings. */
export function ruToISO(ru: string): string {
  const m = /^(\d{2})\.(\d{2})\.(\d{4})$/.exec(ru.trim());
  return m ? `${m[3]}-${m[2]}-${m[1]}` : ru;
}
