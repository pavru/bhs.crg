import { describe, expect, it } from 'vitest';
import { readFileSync, readdirSync } from 'node:fs';
import { resolve } from 'node:path';
import { createHash } from 'node:crypto';
import manifest from './mockups/manifest.json';
import { liveMockups } from './live-mockups';
import { readMockupArchive } from './mockup-assets';

const root = resolve(import.meta.dirname, '../../..');
const archivePath = resolve(import.meta.dirname, 'mockups/sources.zip');
describe('Restored Claude mockups', () => {
  it('keeps the complete, fingerprinted source for every published page', () => {
    expect(createHash('sha256').update(readFileSync(archivePath)).digest('hex')).toBe(manifest.sourceSha256);
    const archive = readMockupArchive(archivePath);
    const missing = manifest.projects.flatMap(project => project.pages.filter(page => !archive.has(`${project.id}/${page.path}`)).map(page => `${project.title}: ${page.path}`));
    expect(missing).toEqual([]);
  });
  it('keeps public aliases unambiguous', () => {
    const ids = [...liveMockups, ...manifest.projects].flatMap(project => [project.id, ...project.aliases]);
    expect(new Set(ids).size).toBe(ids.length);
  });
  it('resolves the updated documentation links to actual projects and pages', () => {
    const files = (folder: string): string[] => readdirSync(folder, { withFileTypes: true }).flatMap(entry => entry.isDirectory() ? files(resolve(folder, entry.name)) : entry.name.endsWith('.md') ? [resolve(folder, entry.name)] : []);
    const paths = [...files(resolve(root, 'docs')), ...files(resolve(root, 'openspec')), resolve(root, 'CLAUDE.md'), resolve(root, 'AGENTS.md')];
    const invalid: string[] = [];
    let checked = 0;
    for (const path of paths) for (const match of readFileSync(path, 'utf8').matchAll(/http:\/\/127\.0\.0\.1:5200\/\?section=mockups[^\s)>`,;:]+/g)) {
      if (match[0].includes('<UUID')) continue;
      const url = new URL(match[0].replace(/\.$/, ''));
      const id = url.searchParams.get('project'); if (!id) continue;
      const project = [...liveMockups, ...manifest.projects].find(project => project.id === id || project.aliases.includes(id));
      const page = url.searchParams.get('page');
      if (!project || page && !project.pages.some(item => item.path === page)) invalid.push(`${path}: ${match[0]}`);
      checked++;
    }
    expect(checked).toBeGreaterThan(140);
    expect(invalid).toEqual([]);
    expect(readFileSync(resolve(root, 'CLAUDE.md'), 'utf8')).toBe(readFileSync(resolve(root, 'AGENTS.md'), 'utf8'));
  });
});
