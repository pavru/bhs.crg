"""Собирает интерактивную дорожную карту из текущей копии OpenSpec."""

import json
import re
from pathlib import Path
from tempfile import NamedTemporaryFile
import sys


HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
from milestones import attach_milestones
CHANGES = HERE.parent / "changes"
OUTPUT = HERE / "dist" / "roadmap-3-4.html"
CHANGE = re.compile(r"(?:plan|work)-\d{2}-[a-z0-9-]+|core-attachment-owner")
ARCHIVE = re.compile(r"\d{4}-\d{2}-\d{2}-(.+)")
TASK = re.compile(r"^- \[([ xX])\] (\d+(?:\.\d+)*[a-zа-я]?)\.? (.+)$", re.IGNORECASE)


def change_dirs():
    """Активная папка перекрывает архив, из архивов берётся самый свежий."""
    changes = {}
    for path in sorted((CHANGES / "archive").iterdir()):
        match = ARCHIVE.fullmatch(path.name)
        if match and CHANGE.fullmatch(match[1]) and (path / "tasks.md").is_file():
            changes[match[1]] = (path, True)
    for path in CHANGES.iterdir():
        if CHANGE.fullmatch(path.name) and (path / "tasks.md").is_file():
            changes[path.name] = (path, False)
    return changes


def read_tasks(path, change_id):
    title = change_id
    sections = []
    items = []
    last = None
    for raw in path.read_text(encoding="utf-8-sig").splitlines():
        line = raw.rstrip()
        if line.startswith("# "):
            title = re.sub(r"^tasks:\s*", "", line[2:].strip(), flags=re.IGNORECASE)
            last = None
        elif line.startswith("## "):
            items = []
            sections.append({"title": line[3:].strip(), "items": items})
            last = None
        elif match := TASK.fullmatch(line):
            if not sections:
                sections.append({"title": "Задачи", "items": items})
            last = {"n": match[2], "text": match[3].strip(), "done": match[1] != " "}
            items.append(last)
        elif last is not None and line.startswith(" "):
            last["text"] += " " + line.strip()
        elif line:
            last = None
    return title, sections


def read_why(path):
    if not path.is_file():
        return None
    proposal = path.read_text(encoding="utf-8-sig")
    match = re.search(r"^## Why[ \t]*\r?\n+(.*?)(?=\n\s*\n|\n## |\Z)", proposal, re.I | re.M | re.S)
    return " ".join(line.strip() for line in match[1].splitlines()) if match else None


def theme_roles():
    """Подставляет роли живой темы в автономный HTML, без сетевых зависимостей."""
    css = (HERE.parents[1] / "src/client/src/theme/md3-tokens.css").read_text(encoding="utf-8")
    blocks = re.findall(r"[^{}]+\{([^{}]+)\}", css)
    roles = {
        "paper": "surface-container-low", "sheet": "surface", "ink": "on-surface",
        "muted": "on-surface-variant", "rule": "outline-variant", "rule-strong": "outline",
        "accent": "primary", "accent-soft": "primary-container", "done": "success",
        "done-soft": "success-container", "warn": "warning", "warn-soft": "warning-container",
        "code-bg": "surface-container",
    }
    result = []
    for block in blocks[:2]:
        values = dict(re.findall(r"--md-(?:sys|ext)-color-([\w-]+):\s*([^;]+);", block))
        result.append(" ".join(f"--{name}: {values[role]};" for name, role in roles.items()))
    if len(result) != 2:
        raise ValueError("Нужны обе схемы темы MD3")
    return result


def main():
    changes = {}
    for change_id, (path, archived) in sorted(change_dirs().items()):
        title, sections = read_tasks(path / "tasks.md", change_id)
        changes[change_id] = {
            "title": title,
            "sections": sections,
            "why": read_why(path / "proposal.md"),
            "archived": archived,
            "path": path.relative_to(HERE.parents[1]).as_posix(),
        }
    if not {"plan-00-epic", "work-00-epic"} <= changes.keys():
        raise SystemExit("В текущей копии OpenSpec нужны plan-00-epic и work-00-epic с tasks.md.")

    definitions = json.loads((HERE / "milestones.json").read_text(encoding="utf-8"))["changes"]
    attach_milestones(changes, definitions)
    history = json.loads((HERE / "claude-marks.json").read_text(encoding="utf-8"))["marks"]
    payload = json.dumps({"changes": changes, "claudeMarks": history}, ensure_ascii=False, separators=(",", ":"))
    template = (HERE / "roadmap.html").read_text(encoding="utf-8")
    page = template.replace("__ROADMAP_DATA__", payload.replace("<", "\\u003c"))
    page = page.replace("__ROADMAP_SCRIPT__", (HERE / "roadmap.js").read_text(encoding="utf-8"))
    light, dark = theme_roles()
    page = page.replace("__LIGHT_THEME__", light).replace("__DARK_THEME__", dark)
    OUTPUT.parent.mkdir(exist_ok=True)
    with NamedTemporaryFile(dir=OUTPUT.parent, delete=False) as draft:
        path = Path(draft.name)
    try:
        path.write_text(page, encoding="utf-8", newline="\n")
        path.replace(OUTPUT)
    finally:
        path.unlink(missing_ok=True)
    print(OUTPUT)


if __name__ == "__main__":
    main()
