"""Exercise model definitions, records, product relations, and grants on an isolated API/SQL database."""
import argparse
import json
import os
import urllib.error
import urllib.request
import uuid


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("base_url", nargs="?", default="http://localhost:5080")
    base_url = parser.parse_args().base_url.rstrip("/")
    tokens = {role: os.environ[f"RetailAccess__{role}Token"] for role in ("Admin", "Manager", "Cashier")}

    def request(method, path, role="Admin", body=None, expected=200):
        headers = {"Content-Type": "application/json"}
        if role:
            headers["Authorization"] = "Bearer " + tokens[role]
        req = urllib.request.Request(base_url + path, method=method, headers=headers,
                                     data=None if body is None else json.dumps(body).encode())
        try:
            with urllib.request.urlopen(req, timeout=20) as response:
                status, raw = response.status, response.read()
        except urllib.error.HTTPError as error:
            status, raw = error.code, error.read()
        assert status == expected, f"{role} {method} {path}: HTTP {status}, expected {expected}; {raw!r}"
        return json.loads(raw) if raw else None

    suffix = uuid.uuid4().hex[:12]
    models = "/api/model-editor/models"
    editor = "/api/model-editor/products"
    fields = f"{editor}/fields"
    form = "/api/products/form"
    key = f"brand_{suffix}"
    other = f"supplier_{suffix}"
    field_key = f"brand_ref_{suffix}"
    records = f"{models}/{key}/records"
    grant = f"{fields}/{field_key}/permissions/manager"
    placement = f"{fields}/{field_key}/placement"
    options = f"{form}/{field_key}/options"

    for role, denied in ((None, 401), ("Manager", 403), ("Cashier", 403)):
        request("GET", models, role, expected=denied)
        request("POST", models, role, {"key": key, "label": "Brand"}, denied)

    for model_key, label in ((key, "Ապրանքանիշ"), (other, "Supplier")):
        result = request("POST", models, body={"key": model_key, "label": label}, expected=201)
        assert result["key"] == model_key and result["label"] == label
    assert key in {model["key"] for model in request("GET", models)}
    request("POST", models, body={"key": key, "label": "Duplicate"}, expected=409)
    for role, denied in ((None, 401), ("Manager", 403), ("Cashier", 403)):
        request("GET", records, role, expected=denied)
        request("POST", records, role, {"name": "Denied"}, denied)
    brand = request("POST", records, body={"name": "Ապրանքանիշ Ա"}, expected=201)
    foreign = request("POST", f"{models}/{other}/records", body={"name": "Supplier A"}, expected=201)
    assert brand in request("GET", records)
    product = request("POST", "/api/products", body={
        "requestId": str(uuid.uuid4()), "sku": "REL-" + suffix,
        "name": "QA product", "unitPriceAmd": 500,
    }, expected=201)
    values = f"/api/products/{product['id']}/custom-fields"
    write_value = values + "/" + field_key

    reference = {
        "key": field_key, "label": "Brand", "type": "reference", "targetModelKey": key,
    }
    manager_grant = f"{editor}/permissions/manager"
    prior_grant = request("GET", editor, "Manager")["canEdit"]
    try:
        request("PUT", manager_grant, body={"canEdit": True}, expected=204)
        assert request("GET", editor, "Manager")["canEdit"]
        request("POST", fields, "Manager", reference, 403)
    finally:
        request("PUT", manager_grant, body={"canEdit": prior_grant}, expected=204)

    field = request("POST", fields, body=reference, expected=201)
    try:
        assert field["targetModelKey"] == key
        request("PUT", placement, body={"position": 17}, expected=204)
        assert [r["id"] for r in request("GET", options)] == [brand["id"]]
        for role, denied in ((None, 401), ("Manager", 403), ("Cashier", 403)):
            request("GET", options, role, expected=denied)
            request("PUT", write_value, role, {"value": brand["id"]}, denied)
        request("PUT", grant, body={"canUse": True}, expected=204)
        view = next(f for f in request("GET", form, "Manager") if f["key"] == field_key)
        assert view["type"] == "reference" and view["targetModelKey"] == key
        assert request("GET", options, "Manager") == [brand]
        request("GET", records, "Manager", expected=403)
        request("PUT", write_value, "Manager", {"value": brand["id"]}, 204)
        assert request("GET", values, "Admin")[field_key] == brand["id"]
        for invalid in (foreign["id"], str(uuid.uuid4()), "wrong type"):
            request("PUT", write_value, "Manager", {"value": invalid}, 400)
            assert request("GET", values, "Manager")[field_key] == brand["id"]
        request("PUT", write_value, "Manager", {"value": None}, 204)
        assert field_key not in request("GET", values, "Admin")
        request("PUT", write_value, "Manager", {"value": brand["id"]}, 204)
        request("PUT", grant, body={"canUse": False}, expected=204)
        request("GET", options, "Manager", expected=403)
        assert field_key not in request("GET", values, "Manager")
        request("PUT", write_value, "Manager", {"value": None}, 403)
        assert request("GET", values)[field_key] == brand["id"]
        request("PUT", grant, body={"canUse": True}, expected=204)
        request("PUT", placement, body={"position": None}, expected=204)
        request("GET", options, "Manager", expected=403)
        assert field_key not in request("GET", values, "Manager")
        print("PASS: catalog and records, reference target, persistence, clear, cross-model rejection, grant and revocation.")
    finally:
        request("PUT", grant, body={"canUse": False}, expected=204)
        request("PUT", placement, body={"position": None}, expected=204)


if __name__ == "__main__":
    main()
