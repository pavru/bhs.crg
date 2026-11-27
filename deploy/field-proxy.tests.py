"""Приёмка двух префиксов с настоящим API. Запуск: python deploy/field-proxy.tests.py <порт API>.

Требуются Docker и тестовый экземпляр с включённым Work,
доступный из контейнера по host.docker.internal. Оба nginx используют конфигурации поставки.
"""

import argparse
import json
import re
import subprocess
import sys
import tempfile
from pathlib import Path
from urllib.error import HTTPError
from urllib.request import Request, urlopen


DEPLOY = Path(__file__).resolve().parent


def docker(*args):
    return subprocess.check_output(["docker", *args], text=True).strip()


def verify(base, work_disabled, invitation):
    snapshot = json.loads((DEPLOY.parent / "docs/field/client-v1.json").read_text(encoding="utf-8"))
    for name in ("info", "ping"):
        case = snapshot["operations"][name]
        if work_disabled:
            try:
                with urlopen(base + case["request"]["path"], timeout=15):
                    raise ValueError(f"{name}: выключенный Work принял запрос")
            except HTTPError as error:
                if error.code != 501 or json.load(error).get("reason") != "module-disabled":
                    raise
            continue
        with urlopen(base + case["request"]["path"], timeout=15) as response:
            body = json.load(response)
            if response.status != case["status"]:
                raise ValueError(f"{name}: HTTP {response.status}")
        if name == "info":
            if not body["organizationName"] or 1 not in body["protocolVersions"]:
                raise ValueError("Сведения не подтверждают организацию и протокол v1")
        elif body.get("ok") is not True:
            raise ValueError("Проверка связи не подтверждена")

    with urlopen(base + "/m/probe.txt", timeout=15) as response:
        if response.read() != b"field fixture":
            raise ValueError("Префикс /m/ не отдал файл поставки")

    for path in ("/", "/api/version", "/api/work/assignments", "/api/field-suggestions", "/m/missing.apk"):
        try:
            with urlopen(base + path, timeout=15) as response:
                raise ValueError(f"{path}: HTTP {response.status}, ожидался 404")
        except HTTPError as error:
            if error.code != 404:
                raise
    if invitation is not None:
        verify_session(base, snapshot["operations"], invitation)


def verify_session(base, operations, invitation):
    def post(name, body):
        case = operations[name]
        saved = case["request"]
        request = Request(base + saved["path"], json.dumps(body).encode(),
                          {"Content-Type": "application/json"}, method=saved["method"])
        try:
            with urlopen(request, timeout=15) as response:
                received = json.load(response)
                if response.status != case["status"] or not set(case["response"]) <= received.keys():
                    raise ValueError(f"{name}: ответ не соответствует договору v1")
        except HTTPError as error:
            raise ValueError(f"{name}: HTTP {error.code}") from error
        if received["employeeId"] != invitation["employeeId"]:
            raise ValueError(f"{name}: другой сотрудник")
        if not received["accessToken"] or not received["refreshToken"]:
            raise ValueError(f"{name}: пустые учётные данные")
        return received

    joined = post("join", {"invitationCode": invitation["invitationCode"]})
    refreshed = post("refresh", {"refreshToken": joined["refreshToken"]})
    if refreshed["deviceId"] != joined["deviceId"] or refreshed["refreshToken"] == joined["refreshToken"]:
        raise ValueError("Refresh не обновил ту же сессию")
    if post("refresh", {"refreshToken": joined["refreshToken"]}) != refreshed:
        raise ValueError("Refresh не повторил результат ротации")
    request = Request(base + "/api/work/reports", headers={"Authorization": "Bearer " + refreshed["accessToken"]})
    try:
        with urlopen(request, timeout=15):
            raise ValueError("Внешний прокси пропустил полевую сессию в офисный API")
    except HTTPError as error:
        if error.code != 404:
            raise


def run(port, temp, work_disabled, invitation=None):
    dockerfile = (DEPLOY / "Dockerfile.web").read_text(encoding="utf-8")
    image = re.search(r"^FROM (nginxinc/\S+)", dockerfile, re.M)[1]
    # Меняются только адреса стенда: пара конфигураций работает в одном сетевом пространстве.
    public = (DEPLOY / "field-proxy.conf.example").read_text(encoding="utf-8").replace("listen 80;", "listen 8081;")
    web = (DEPLOY / "nginx.conf").read_text(encoding="utf-8").replace("http://api:8080", f"http://host.docker.internal:{port}")
    (temp / "public.conf").write_text(public, encoding="utf-8")
    (temp / "web.conf").write_text(web, encoding="utf-8")
    (temp / "body-size.conf").write_text("client_max_body_size 600m;\n", encoding="utf-8")
    (temp / "probe.txt").write_bytes(b"field fixture")
    args = []
    for name, path in (
        ("public.conf", "/etc/nginx/conf.d/public.conf"),
        ("web.conf", "/etc/nginx/conf.d/default.conf"),
        ("body-size.conf", "/tmp/nginx/body-size.conf"),
        ("probe.txt", "/usr/share/nginx/html/m/probe.txt"),
    ):
        args.extend(["--mount", f"type=bind,source={temp / name},target={path},readonly"])
    container = docker("run", "-d", "-p", "127.0.0.1::8081", "--add-host", "host.docker.internal:host-gateway",
                       *args, image)
    try:
        docker("exec", container, "nginx", "-t")
        address = docker("port", container, "8081/tcp")
        verify("http://" + address, work_disabled, invitation)
    finally:
        docker("rm", "-f", container)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("port", type=int, help="порт тестового API, доступный контейнеру")
    parser.add_argument("--work-disabled", action="store_true", help="проверить отказ на экземпляре без Work")
    parser.add_argument("--invitation-stdin", action="store_true",
                        help="прочитать JSON с invitationCode и employeeId для проверки подключения и refresh")
    options = parser.parse_args()
    invitation = json.load(sys.stdin) if options.invitation_stdin else None
    with tempfile.TemporaryDirectory() as directory:
        run(options.port, Path(directory), options.work_disabled, invitation)
    print("Проверены сведения, связь, выдача файлов и границы внешнего прокси.")
    if invitation is not None:
        print("Через внешний прокси проверены подключение, refresh и повтор refresh.")
