export interface MockupPage { path: string; title: string; width?: number; height?: number }
export interface RestoredMockup {
  id: string; title: string; kind: string; pages: MockupPage[]; aliases: string[];
  references: { file: string; line: number; publicId: string }[];
  source: { folder: string; updatedAt?: string; designId?: string | null };
}
export interface MockupState {
  ready: boolean; project: string; page: string; controls: Record<string, number>; error?: string;
}
export function mockupUrl(project: string, page: string, theme = 'light', original = false) {
  return `mockup.html?${new URLSearchParams({ project, page, theme, ...(original ? { original: '1' } : {}) })}`;
}
