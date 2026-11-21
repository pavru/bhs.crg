"""Replace only identified design/reference URLs; keep unrelated Claude documents intact."""
from pathlib import Path
from urllib.parse import parse_qs, urlencode, urlparse
import json
import re
import subprocess
import uuid

repo = Path(__file__).resolve().parents[3]
manifest = json.loads((Path(__file__).parent / 'mockups/manifest.json').read_text('utf-8'))
alphabet = '123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz'
projects = {project['id']: project for project in manifest['projects']}
aliases = {alias: project for project in manifest['projects'] for alias in project['aliases']}
base = 'http://127.0.0.1:5200/'
changed = []
missing = set()


def replace(match):
    url = match.group(0)
    suffix = '.' if url.endswith('.') else ''
    if suffix:
        url = url[:-1]
    public_id = urlparse(url).path.rsplit('/', 1)[-1]
    if public_id == 'DckGDGRbsYEtJn6rr2ibMN':
        return base + '?section=library' + suffix
    try:
        number = 0
        for char in public_id:
            number = number * 58 + alphabet.index(char)
        archive_id = str(uuid.UUID(int=number))
    except (ValueError, OverflowError):
        return url
    project = aliases.get(public_id) or projects.get(archive_id)
    if not project:
        missing.add(public_id)
        return url
    query = parse_qs(urlparse(url).query)
    file = query.get('file', [None])[0]
    page = next((page for page in project['pages'] if page['path'] == file), None)
    params = {'section': 'mockups', 'project': project['id']}
    if page:
        params['page'] = page['path']
    elif file:
        raise ValueError(f'Page missing for {project["title"]}: {file}')
    return base + '?' + urlencode(params) + suffix


files = subprocess.check_output(['git', '-c', 'core.fsmonitor=false', 'ls-files', '-z', '*.md'], cwd=repo, encoding='utf-8').strip('\0').split('\0')
pattern = re.compile(r'https://claude\.ai/artifact/[A-Za-z0-9]+(?:\?[^\s)>`,]+)?')
for file in files:
    path = repo / file
    text = path.read_text('utf-8-sig')
    updated = pattern.sub(replace, text)
    if text != updated:
        path.write_text(updated, 'utf-8')
        changed.append(file)
print(json.dumps({'changed': len(changed), 'files': changed, 'unmatched': sorted(missing)}, ensure_ascii=False, indent=2))
