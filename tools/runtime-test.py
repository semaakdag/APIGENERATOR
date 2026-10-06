#!/usr/bin/env python3
"""Runtime CRUD test: starts a generated API and exercises the Users endpoints over HTTP.

Usage: tools/runtime-test.py <generated-solution-dir> [--swagger-only] [--expect-auth] [--composite]
"""
import glob, json, os, socket, subprocess, sys, time, urllib.error, urllib.request

target = os.path.abspath(sys.argv[1])
swagger_only = "--swagger-only" in sys.argv
expect_auth = "--expect-auth" in sys.argv
composite = "--composite" in sys.argv
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
    if status == 200 and not composite:
        paths = set(swagger.get("paths", {}))
        check("swagger lists Users routes", any("users" in p.lower() for p in paths), sorted(paths))
        users_path = next((p for p in paths if p.lower() == "/api/users"), None)
        summary = swagger["paths"].get(users_path, {}).get("get", {}).get("summary", "") if users_path else ""
        check("swagger shows XML summaries", "Users" in summary, summary)

    if expect_auth and status == 200:
        request = urllib.request.Request(base + "/api/Users")
        try:
            urllib.request.urlopen(request, timeout=10)
            check("anonymous request is challenged", False, "200 without credentials")
        except urllib.error.HTTPError as error:
            check("anonymous request is challenged", error.code == 401 and "Negotiate" in error.headers.get("WWW-Authenticate", ""),
                  (error.code, error.headers.get("WWW-Authenticate")))

    if not swagger_only and not expect_auth and not composite and status == 200:
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

        # Users.Id is not an IDENTITY column, so the client-supplied key must be kept per record.
        for user_id, name in ((21, "Grace"), (22, "Linus")):
            status, created = call("POST", "/api/Users", dict(payload, id=user_id, name=name))
            check(f"POST keeps client key {user_id}", status in (200, 201) and isinstance(created, dict) and created.get("id") == user_id, (status, created))
        for user_id, name in ((21, "Grace"), (22, "Linus")):
            status, item = call("GET", f"/api/Users/{user_id}")
            check(f"GET {user_id} returns its own record", status == 200 and isinstance(item, dict) and item.get("name") == name, (status, item))
        status, _ = call("POST", "/api/Users", dict(payload, id=21, name="Duplicate"))
        check("POST duplicate key is 409", status == 409, status)
        status, items = call("GET", "/api/Users")
        check("GET all returns both records once", status == 200 and sorted(item.get("id") for item in items) == [21, 22], (status, items))
    if composite and status == 200:
        # CompositeKey(TenantId, ItemId): rows sharing the first key column must stay addressable on their own.
        for tenant, item, name in ((1, 1, "first"), (1, 2, "second")):
            status_code, created = call("POST", "/api/CompositeKey", {"tenantId": tenant, "itemId": item, "name": name})
            check(f"POST composite {tenant}/{item}", status_code in (200, 201), (status_code, created))
        status_code, row = call("GET", "/api/CompositeKey/1/2")
        check("GET composite returns the exact row", status_code == 200 and isinstance(row, dict) and row.get("name") == "second", (status_code, row))
        status_code, _ = call("PUT", "/api/CompositeKey/1/1", {"name": "first-updated"})
        check("PUT composite updates one row", status_code == 200, status_code)
        status_code, row = call("GET", "/api/CompositeKey/1/2")
        check("other row unchanged", isinstance(row, dict) and row.get("name") == "second", row)
        status_code, _ = call("DELETE", "/api/CompositeKey/1/2")
        check("DELETE composite removes one row", status_code == 204, status_code)
        status_code, rows = call("GET", "/api/CompositeKey")
        check("one composite row remains", status_code == 200 and [r.get("name") for r in rows] == ["first-updated"], rows)
        status_code, _ = call("POST", "/api/CompositeKey", {"tenantId": 1, "itemId": 1, "name": "dup"})
        check("POST duplicate composite key is 409", status_code == 409, status_code)
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
