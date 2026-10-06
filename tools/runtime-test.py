#!/usr/bin/env python3
"""Runtime CRUD test: starts a generated API and exercises the Users endpoints over HTTP.

Usage: tools/runtime-test.py <generated-solution-dir> [--swagger-only] [--expect-auth] [--composite] [--recipes]
"""
import glob, json, os, socket, subprocess, sys, time, urllib.error, urllib.request

target = os.path.abspath(sys.argv[1])
swagger_only = "--swagger-only" in sys.argv
expect_auth = "--expect-auth" in sys.argv
composite = "--composite" in sys.argv
recipes = "--recipes" in sys.argv
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
    if status == 200 and not composite and not recipes:
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

    if not swagger_only and not expect_auth and not composite and not recipes and status == 200:
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
    if recipes and status == 200:
        # Endpoints added with add-endpoint on tools/cases/recipes.sql.
        alpha = {"id": 1, "code": "A1", "name": "Alpha Widget", "isActive": True, "createdAt": "2026-01-05T00:00:00"}
        beta = {"id": 2, "code": "B2", "name": "Beta", "isActive": False, "createdAt": "2026-02-10T00:00:00"}
        for product in (alpha, beta):
            code, created = call("POST", "/api/Products", product)
            check(f"seed product {product['id']}", code == 201, (code, created))
        code, item = call("GET", "/api/Products/by-code/A1")
        check("GetByCode finds the record", code == 200 and item.get("name") == "Alpha Widget", (code, item))
        code, _ = call("GET", "/api/Products/by-code/ZZ")
        check("GetByCode unknown is 404", code == 404, code)
        code, items = call("GET", "/api/Products/active")
        check("GetActiveList returns active only", code == 200 and [i["id"] for i in items] == [1], (code, items))
        code, items = call("GET", "/api/Products/search-by-name?term=Widget")
        check("Search matches", code == 200 and [i["id"] for i in items] == [1], (code, items))
        code, _ = call("GET", "/api/Products/search-by-name")
        check("Search without term is 400", code == 400, code)
        code, items = call("GET", "/api/Products/by-created-at-range?from=2026-01-01&to=2026-01-31")
        check("Date range filters", code == 200 and [i["id"] for i in items] == [1], (code, items))
        code, _ = call("GET", "/api/Products/by-created-at-range?from=2026-02-01&to=2026-01-01")
        check("Date range from > to is 400", code == 400, code)
        code, created = call("POST", "/api/Products/bulk", [dict(alpha, id=3, code="C3"), dict(alpha, id=4, code="D4")])
        check("BulkInsert creates all", code == 201 and sorted(i["id"] for i in created) == [3, 4], (code, created))
        code, _ = call("POST", "/api/Products/bulk", [dict(alpha, id=5, code="E5"), dict(alpha, id=1)])
        check("BulkInsert with existing key is 409", code == 409, code)
        code, items = call("GET", "/api/Products")
        check("BulkInsert conflict inserts nothing", code == 200 and sorted(i["id"] for i in items) == [1, 2, 3, 4], (code, items))
        code, _ = call("POST", "/api/Products/bulk", [])
        check("BulkInsert empty is 400", code == 400, code)

        code, created = call("POST", "/api/Orders/bulk", [
            {"number": "N-1", "placedOn": "2026-03-01", "isOpen": True},
            {"number": "N-2", "placedOn": "2026-04-01", "isOpen": False}])
        check("BulkInsert generates identity keys", code == 201 and sorted(i["orderId"] for i in created) == [1, 2], (code, created))
        code, item = call("GET", "/api/Orders/by-number/N-2")
        check("GetByNumber on identity table", code == 200 and item.get("orderId") == 2, (code, item))
        code, items = call("GET", "/api/Orders/active")
        check("GetActiveList on IsOpen", code == 200 and [i["number"] for i in items] == ["N-1"], (code, items))
        code, items = call("GET", "/api/Orders/by-placed-on-range?from=2026-03-15&to=2026-04-30")
        check("DateOnly range", code == 200 and [i["number"] for i in items] == ["N-2"], (code, items))

        code, created = call("POST", "/api/Stock/bulk", [
            {"warehouseId": 1, "productId": 1, "quantity": 5, "countedAt": "2026-05-01T00:00:00+00:00"},
            {"warehouseId": 1, "productId": 2, "quantity": 7, "countedAt": "2026-06-01T00:00:00+00:00"}])
        check("BulkInsert on composite keys", code == 201 and len(created) == 2, (code, created))
        code, _ = call("POST", "/api/Stock/bulk", [{"warehouseId": 1, "productId": 1, "quantity": 1}])
        check("BulkInsert composite duplicate is 409", code == 409, code)
        code, items = call("GET", "/api/Stock/by-counted-at-range?from=2026-05-15T00:00:00Z&to=2026-06-15T00:00:00Z")
        check("DateTimeOffset range", code == 200 and [i["productId"] for i in items] == [2], (code, items))
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
