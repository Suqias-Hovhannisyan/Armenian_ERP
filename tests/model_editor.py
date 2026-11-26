"""Verify model-editor permissions against a running API and an isolated test database."""
import argparse
import json
import os
import urllib.error
import urllib.request
import uuid


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("base_url", nargs="?", default="http://localhost:5080")
    parser.add_argument(
        "--auth-only", action="store_true",
        help="Verify API authentication and role denials that run without a database.",
    )
    args = parser.parse_args()
    base_url = args.base_url.rstrip("/")
    tokens = {
        role: os.environ.get(f"RetailAccess__{role}Token")
        for role in ("Admin", "Manager", "Cashier")
    }
    missing_roles = [role for role, token in tokens.items() if not token]
    if missing_roles:
        parser.error(f"Configure RetailAccess__RoleToken for: {', '.join(missing_roles)}.")

    def request(method, path, role=None, body=None, expected=200, token=None):
        headers = {"Content-Type": "application/json"}
        credential = token if token is not None else tokens.get(role)
        if credential:
            headers["Authorization"] = f"Bearer {credential}"
        data = json.dumps(body).encode() if body is not None else None
        req = urllib.request.Request(base_url + path, data=data, headers=headers, method=method)
        try:
            with urllib.request.urlopen(req, timeout=20) as response:
                status, raw = response.status, response.read()
        except urllib.error.HTTPError as error:
            status, raw = error.code, error.read()
        assert status == expected, f"{method} {path} ({role}): expected {expected}, got {status}: {raw!r}"
        return json.loads(raw) if raw else None

    editor = "/api/model-editor/products"
    form = "/api/products/form"
    manager_permission = f"{editor}/permissions/manager"
    for path in (editor, form):
        request("GET", path, expected=401)
        request("GET", path, expected=401, token=uuid.uuid4().hex)
    request("GET", editor, "Cashier", expected=403)
    request("PUT", manager_permission, "Manager", {"canEdit": True}, expected=403)
    request("PUT", manager_permission, "Cashier", {"canEdit": True}, expected=403)
    runtime = f"/api/products/{uuid.uuid4()}/custom-fields/qa_permission"
    request("PUT", runtime, body={"value": "denied"}, expected=401)
    request("PUT", runtime, "Cashier", {"value": "denied"}, expected=403)
    print("PASS: protected endpoints reject missing or invalid credentials and unauthorized roles.")

    if args.auth_only:
        return

    suffix = uuid.uuid4().hex[:12]
    key = f"qa_{suffix}"
    manager_key = f"manager_{suffix}"
    fields = f"{editor}/fields"
    placement = f"{fields}/{key}/placement"
    field_permission = f"{fields}/{key}/permissions/manager"
    field = {"key": key, "label": "QA stock note", "type": "text"}
    admin = request("GET", editor, "Admin")
    assert admin["canEdit"] and admin["canGrant"]
    saved_manager_right = request("GET", editor, "Manager")["canEdit"]
    created = False
    manager_created = False
    try:
        request("PUT", manager_permission, "Admin", {"canEdit": False}, expected=204)
        manager = request("GET", editor, "Manager")
        assert not manager["canEdit"] and not manager["canGrant"]
        request("POST", fields, "Manager", field, expected=403)
        request("POST", fields, "Cashier", field, expected=403)

        product = request(
            "POST", "/api/products", "Admin",
            {"requestId": str(uuid.uuid4()), "sku": f"QA-{suffix}", "name": "QA item", "unitPriceAmd": 450},
            expected=201,
        )
        values = f"/api/products/{product['id']}/custom-fields"
        value = f"{values}/{key}"

        request("POST", fields, "Admin", {**field, "key": "sku"}, expected=400)
        saved_field = request("POST", fields, "Admin", field, expected=201)
        created = True
        assert saved_field["key"] == key and not saved_field["placed"]
        request("POST", fields, "Admin", field, expected=409)
        assert key not in {field["key"] for field in request("GET", form, "Admin")}
        request("PUT", placement, "Manager", {"position": 10}, expected=403)
        request("PUT", placement, "Admin", {"position": 10}, expected=204)
        assert key in {field["key"] for field in request("GET", form, "Admin")}
        assert key not in {field["key"] for field in request("GET", form, "Manager")}
        assert key not in {field["key"] for field in request("GET", form, "Cashier")}

        request("PUT", value, "Admin", {"value": "admin value"}, expected=204)
        assert request("GET", values, "Admin")[key] == "admin value"
        assert key not in request("GET", values, "Manager")
        request("PUT", value, "Manager", {"value": "denied"}, expected=403)
        request("PUT", value, "Cashier", {"value": "denied"}, expected=403)
        request("PUT", field_permission, "Manager", {"canUse": True}, expected=403)

        request("PUT", field_permission, "Admin", {"canUse": True}, expected=204)
        assert key in {field["key"] for field in request("GET", form, "Manager")}
        assert request("GET", values, "Manager")[key] == "admin value"
        request("PUT", value, "Manager", {"value": 123}, expected=400)
        request("PUT", value, "Manager", {"value": "manager value"}, expected=204)
        assert request("GET", values, "Admin")[key] == "manager value"
        assert key not in request("GET", values, "Cashier")
        request("PUT", value, "Cashier", {"value": "denied"}, expected=403)
        request("PUT", placement, "Manager", {"position": 20}, expected=403)

        request("PUT", manager_permission, "Admin", {"canEdit": True}, expected=204)
        manager = request("GET", editor, "Manager")
        assert manager["canEdit"] and not manager["canGrant"]
        manager_field = {**field, "key": manager_key}
        request("POST", fields, "Manager", manager_field, expected=201)
        manager_created = True
        request("PUT", f"{fields}/{manager_key}/placement", "Manager", {"position": 20}, expected=204)
        request("PUT", field_permission, "Manager", {"canUse": False}, expected=403)
        request("PUT", manager_permission, "Admin", {"canEdit": False}, expected=204)
        request("PUT", placement, "Manager", {"position": 30}, expected=403)
        assert request("GET", values, "Manager")[key] == "manager value"

        request("PUT", field_permission, "Admin", {"canUse": False}, expected=204)
        assert key not in {field["key"] for field in request("GET", form, "Manager")}
        assert key not in request("GET", values, "Manager")
        request("PUT", value, "Manager", {"value": "denied"}, expected=403)
        assert request("GET", values, "Admin")[key] == "manager value"
        assert request("GET", f"/api/products/{product['id']}/stock", "Admin")["quantity"] == 0
        print("PASS: field creation, placement, role grants, value validation, and API revocation.")
    finally:
        if created:
            request("PUT", field_permission, "Admin", {"canUse": False}, expected=204)
            request("PUT", placement, "Admin", {"position": None}, expected=204)
        if manager_created:
            request("PUT", f"{fields}/{manager_key}/placement", "Admin", {"position": None}, expected=204)
        request("PUT", manager_permission, "Admin", {"canEdit": saved_manager_right}, expected=204)


if __name__ == "__main__":
    main()
