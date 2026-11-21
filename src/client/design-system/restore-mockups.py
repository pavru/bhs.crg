"""Import BHS.CRG design sources from the owner's read-only Claude export.

Run from the repository root: python -X utf8 src/client/design-system/restore-mockups.py
The ZIP is a reproducible source fixture; the Vite plugin serves its files locally.
"""
from pathlib import Path
import argparse
import collections
import hashlib
import html
import json
import re
import subprocess
import uuid
import zipfile

parser = argparse.ArgumentParser()
parser.add_argument('--export', default='E:/Claude')
args = parser.parse_args()
repo = Path(__file__).resolve().parents[3]
target = Path(__file__).resolve().parent / 'mockups'
target.mkdir(exist_ok=True)
source = Path(args.export) / 'Artifacts_restored'
group = Path(args.export) / 'Востановление проектов/BHS.CRG/Артефакты'
alphabet = '123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz'


def decode(value):
    try:
        number = 0
        for char in value:
            number = number * 58 + alphabet.index(char)
        return str(uuid.UUID(int=number))
    except (ValueError, OverflowError):
        return ''


def read_json(path):
    return json.loads(path.read_text('utf-8-sig')) if path.exists() else {}


url_pattern = re.compile(r'https://claude\.ai/artifact/([A-Za-z0-9]+)')
# This commit changed public IDs only. Match old and new URLs in each diff hunk.
diff = subprocess.check_output(['git', '-c', 'core.fsmonitor=false', 'show', '--format=', '07f23278'], cwd=repo, encoding='utf-8')
aliases = collections.defaultdict(set)
previous_manifest = read_json(target / 'manifest.json')
for previous in previous_manifest.get('projects', []):
    aliases[previous['id']].update(previous.get('aliases', []))
    aliases[previous['id']].update(ref['publicId'] for ref in previous.get('references', []) if len(ref['publicId']) < 36)
old = []
for line in diff.splitlines():
    if line.startswith('@@'):
        old = []
    if line.startswith('-') and not line.startswith('---'):
        old.extend(url_pattern.findall(line))
    if line.startswith('+') and not line.startswith('+++'):
        for new_id in url_pattern.findall(line):
            if old:
                old_id = old.pop(0)
                aliases[decode(old_id)].update([old_id, new_id])

docs = subprocess.check_output(['git', '-c', 'core.fsmonitor=false', 'ls-files', '-z', '*.md'], cwd=repo, encoding='utf-8').strip('\0').split('\0')
references = collections.defaultdict(list)
for file in docs:
    for line_number, line in enumerate((repo / file).read_text('utf-8-sig').splitlines(), 1):
        for public_id in url_pattern.findall(line):
            archive_id = next((key for key, values in aliases.items() if public_id in values), decode(public_id))
            references[archive_id].append({'file': file, 'line': line_number, 'publicId': public_id})
        for archive_id in re.findall(r'http://127\.0\.0\.1:5200/\?section=mockups&project=([0-9a-f-]{36})', line):
            references[archive_id].append({'file': file, 'line': line_number, 'publicId': archive_id})

group_ids = {p.name.rsplit('_', 1)[-1] for p in group.iterdir() if p.is_dir()}
records = []
zip_entries = {}
export_zips = [zipfile.ZipFile(Path(args.export) / name) for name in ['frames-000.zip', 'frames-001.zip']]
for folder in sorted(source.iterdir()):
    metadata = read_json(folder / 'artifact.json')
    if not metadata:
        continue
    archive_id = metadata['id']
    if archive_id[:8] not in group_ids and archive_id not in references:
        continue
    title = metadata.get('versions', [{}])[0].get('title', folder.name)
    if title == 'BHS.CRG UI':
        continue  # The live library is already restored from the current code.
    project = folder / 'project'
    compat = read_json(project / 'compat.json')
    canvas = read_json(project / 'canvas.json')
    pages = []
    if compat.get('pages'):
        pages = [{'path': page['path'], 'title': page.get('title', page['path'])} for page in compat['pages'] if page['path'].endswith('.html')]
    elif canvas.get('boards'):
        pages = [{'path': path, 'title': board.get('title', path), 'width': board.get('w', 1366), 'height': board.get('h', 900)} for path, board in canvas['boards'].items() if (project / path).is_file()]
    elif project.is_dir():
        pages = [{'path': p.name, 'title': p.stem} for p in sorted(project.glob('*.html'))]
    kind = 'design' if pages else 'reference'
    if title.startswith(('Анализ UI', 'Анализ дизайна')):
        kind = 'reference'
    if not pages:
        pictures = [p for p in sorted(folder.iterdir()) if p.suffix.lower() in ['.png', '.jpg', '.jpeg', '.webp']]
        original = folder / 'index.html'
        if pictures:
            markup = '<!doctype html><html lang="ru"><head><meta charset="utf-8"><title>' + html.escape(title) + '</title></head><body><h1>' + html.escape(title) + '</h1>'
            markup += ''.join('<figure><figcaption>' + html.escape(p.name) + '</figcaption><img style="max-width:100%;height:auto" src="' + html.escape(p.name) + '"></figure>' for p in pictures) + '</body></html>'
            zip_entries[f'{archive_id}/index.html'] = markup.encode('utf-8')
            for picture in pictures:
                zip_entries[f'{archive_id}/{picture.name}'] = picture.read_bytes()
            pages = [{'path': 'index.html', 'title': 'Снимки из архива'}]
        elif original.is_file():
            # Keep standalone review/reference artifacts, without calling them live screens.
            zip_entries[f'{archive_id}/index.html'] = original.read_bytes()
            pages = [{'path': 'index.html', 'title': 'Разбор из архива'}]
        else:
            # Some old HTML artifacts were not extracted by the previous recovery.
            entry = f"artifacts/{archive_id}/versions/{metadata.get('active_version')}.html"
            for exported in export_zips:
                if entry in exported.namelist():
                    zip_entries[f'{archive_id}/index.html'] = exported.read(entry)
                    pages = [{'path': 'index.html', 'title': 'Снимки из исходного экспорта'}]
                    break
    if not pages:
        continue
    prefix = f"artifacts/{archive_id}/versions/{metadata.get('active_version')}/"
    for exported in export_zips:
        for entry in exported.infolist():
            if not entry.filename.startswith(prefix):
                continue
            relative = entry.filename[len(prefix):]
            if relative.startswith(('artifact-type/', '_ds/')) or entry.is_dir():
                continue
            if relative.startswith('project/'):
                relative = relative[len('project/'):]
            if relative.startswith('_ds/'):
                continue
            if Path(relative).suffix.lower() in ['.png', '.jpg', '.jpeg', '.webp', '.svg', '.woff', '.woff2', '.css', '.js'] and Path(relative).name not in ['support.js', 'lucide.min.js']:
                zip_entries.setdefault(f'{archive_id}/{relative}', exported.read(entry))
    if project.is_dir():
        for file in sorted(project.rglob('*')):
            if not file.is_file() or file.name in ['support.js', 'lucide.min.js']:
                continue
            path = file.relative_to(project).as_posix()
            zip_entries[f'{archive_id}/{path}'] = file.read_bytes()
        # Review pages sometimes use images stored next to project/, not inside it.
        for file in folder.iterdir():
            if file.is_file() and file.suffix.lower() in ['.png', '.jpg', '.jpeg', '.webp']:
                zip_entries[f'{archive_id}/{file.name}'] = file.read_bytes()
    records.append({
        'id': archive_id, 'title': title, 'kind': kind, 'pages': pages,
        'aliases': sorted(aliases[archive_id] | {ref['publicId'] for ref in references[archive_id] if len(ref['publicId']) < 36}), 'references': references[archive_id],
        'source': {'folder': folder.name, 'updatedAt': metadata.get('updated_at'), 'designId': compat.get('source', {}).get('projectId')},
    })

with zipfile.ZipFile(target / 'sources.zip', 'w', zipfile.ZIP_DEFLATED, compresslevel=9) as bundle:
    for name, content in sorted(zip_entries.items()):
        entry = zipfile.ZipInfo(name, date_time=(2026, 11, 16, 0, 0, 0))
        entry.compress_type = zipfile.ZIP_DEFLATED
        bundle.writestr(entry, content)
manifest = {'version': 1, 'origin': 'Claude export, 2026-11-16', 'sourceSha256': hashlib.sha256((target / 'sources.zip').read_bytes()).hexdigest(), 'projects': records}
(target / 'manifest.json').write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + '\n', 'utf-8')
print(json.dumps({'projects': len(records), 'designs': sum(r['kind'] == 'design' for r in records), 'pages': sum(len(r['pages']) for r in records), 'files': len(zip_entries), 'bytes': sum(map(len, zip_entries.values())), 'references': sum(len(r['references']) for r in records)}, ensure_ascii=False))
print('Unmatched document IDs:', sorted({ref['publicId'] for key, refs in references.items() if key not in {r['id'] for r in records} for ref in refs}))
