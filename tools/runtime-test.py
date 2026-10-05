#!/usr/bin/env python3
"""Runtime CRUD test: starts a generated API and exercises the Users endpoints over HTTP.

Usage: tools/runtime-test.py <generated-solution-dir> [--swagger-only]
"""
import glob, json, os, socket, subprocess, sys, time, urllib.error, urllib.request

target = os.path.abspath(sys.argv[1])
swagger_only = "--swagger-only" in sys.argv
project = glob.glob(os.path.join(target, "src", "*.Api", "*.Api.csproj"))[0]

with socket.socket() as probe:
    probe.bind(("127.0.0.1", 0))
    port = probe.getsockname()[1]
base = f"http://127.0.0.1:{port}"
env = dict(os.environ, ASPNETCORE_ENVIRONMENT="Development", ASPNETCORE_URLS=base)
server = subprocess.Popen(["dotnet", "run", "--no-build", "--no-launch-profile", "--project", project],
                          env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)

def call(method, path, body=None):
    data = json.dumps(body).encode() if body is not None else None
    request = urllib.request.Request(base + path, data=data, method=method,
                                     headers={"Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(request, timeout=10) as response:
            text = response.read().decode()
            return response.status, json.loads(text) if text else None
    except urllib.error.HTTPError as error:
        return error.code, error.read().decode()

failures = []
def check(name, condition, detail=""):
    print(("ok:   " if condition else "FAIL: ") + name + ("" if condition else f" -> {detail}"))
    if not condition:
        failures.append(name)

status, swagger = None, None
try:
    for _ in range(120):
        try:
            status, swagger = call("GET", "/swagger/v1/swagger.json")
            if status == 200:
                break
        except (urllib.error.URLError, ConnectionError):
            pass
        if server.poll() is not None:
            break
        time.sleep(0.5)
    check("swagger.json served", status == 200, status)
    if status == 200:
        paths = set(swagger.get("paths", {}))
        check("swagger lists Users routes", any("users" in p.lower() for p in paths), sorted(paths))

    if not swagger_only and status == 200:
        payload = {"id": 7, "name": "Ada", "email": "ada@example.com", "createdDate": "2026-01-01T00:00:00"}
        status, created = call("POST", "/api/Users", payload)
        check("POST creates", status in (200, 201), (status, created))
        key = (created or {}).get("id", 7) if isinstance(created, dict) else 7
        status, item = call("GET", f"/api/Users/{key}")
        check("GET by id returns created item", status == 200 and isinstance(item, dict) and item.get("email") == "ada@example.com", (status, item))
        status, items = call("GET", "/api/Users")
        check("GET all contains item", status == 200 and isinstance(items, list) and len(items) >= 1, (status, items))
        status, updated = call("PUT", f"/api/Users/{key}", dict(payload, id=key, name="Ada L."))
        check("PUT updates", status in (200, 204), (status, updated))
        status, item = call("GET", f"/api/Users/{key}")
        check("GET reflects update", status == 200 and isinstance(item, dict) and item.get("name") == "Ada L.", (status, item))
        status, _ = call("DELETE", f"/api/Users/{key}")
        check("DELETE removes", status in (200, 204), status)
        status, _ = call("GET", f"/api/Users/{key}")
        check("GET after delete is 404", status == 404, status)
        status, _ = call("DELETE", f"/api/Users/{key}")
        check("DELETE missing is 404", status == 404, status)
        status, _ = call("PUT", "/api/Users/999", dict(payload, id=999))
        check("PUT missing is 404", status == 404, status)
finally:
    server.terminate()
    try:
        output = server.communicate(timeout=10)[0]
    except subprocess.TimeoutExpired:
        server.kill(); output = server.communicate()[0]
    if failures:
        print(output[-3000:])

print("RUNTIME FAILED" if failures else "RUNTIME OK")
sys.exit(1 if failures else 0)
