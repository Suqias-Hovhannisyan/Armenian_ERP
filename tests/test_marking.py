"""Run against an isolated ArmeniaRetailERP API and SQL Server test database."""
from functools import partial
import json
import os
import sys
import uuid
from urllib.error import HTTPError
from urllib.request import Request, urlopen


def gtin():
    digits = str(uuid.uuid4().int % 10**13).zfill(13)
    checksum = -sum(int(n) * (3 if i % 2 == 0 else 1) for i, n in enumerate(digits)) % 10
    return digits + str(checksum)


def api_request(base, method, path, body=None, expected=(200,), role=None):
    headers = {"Content-Type": "application/json"}
    if role:
        headers["Authorization"] = "Bearer " + os.environ[f"RetailAccess__{role}Token"]
    req = Request(base.rstrip("/") + path, method=method, headers=headers,
                  data=None if body is None else json.dumps(body).encode())
    try:
        with urlopen(req, timeout=30) as res:
            status, raw = res.status, res.read()
    except HTTPError as exc:
        status, raw = exc.code, exc.read()
    assert status in expected, f"{method} {path}: expected {expected}, got {status}: {raw!r}"
    return json.loads(raw) if raw else None


def verify(base):
    call = partial(api_request, base)

    def product():
        return call("POST", "/api/products", {
            "requestId": str(uuid.uuid4()), "sku": "QA-" + uuid.uuid4().hex,
            "name": "Marking QA", "unitPriceAmd": 450,
        }, (201,))["id"]

    def operation(quantity, codes=None, with_product=True):
        result = {"requestId": str(uuid.uuid4()), "quantity": quantity}
        if with_product:
            result["productId"] = item
        if codes is not None:
            result["markingCodes"] = codes
        return result

    item, other = product(), product()
    endpoint = f"/api/products/{item}/identifiers"
    other_endpoint = f"/api/products/{other}/identifiers"
    stock = f"/api/products/{item}/stock"
    code = gtin()
    barcode = "QA" + uuid.uuid4().hex
    settings = {"barcode": barcode, "gtin": code, "atgCode": "2201100000", "requiresMarking": True}
    invalid = (400, 409, 422)
    call("PUT", endpoint, settings, (401,))
    for role in ("Manager", "Cashier"):
        call("PUT", endpoint, settings, (403,), role)
    call("PUT", endpoint, dict(settings, atgCode="123"), invalid, "Admin")
    call("PUT", endpoint, dict(settings, gtin=code[:-1] + str((int(code[-1]) + 1) % 10)), invalid, "Admin")
    call("PUT", endpoint, settings, (200, 204), "Admin")
    assert call("GET", endpoint)["requiresMarking"]
    assert call("GET", "/api/products/by-barcode/" + barcode)["id"] == item
    call("PUT", other_endpoint, settings, invalid, "Admin")
    # A zero balance can still have an outstanding unmarked return obligation.
    unmarked = product()
    ordinary = dict(operation(1), productId=unmarked)
    call("POST", "/api/receipts", ordinary, (201,))
    old_sale = call("POST", "/api/sales", dict(ordinary, requestId=str(uuid.uuid4())), (201,))
    assert old_sale["stock"] == 0
    unmarked_setup = dict(settings, barcode="LEGACY" + barcode, gtin=gtin())
    call("PUT", f"/api/products/{unmarked}/identifiers", unmarked_setup, invalid, "Admin")
    refund = operation(1, with_product=False)
    assert call("POST", f"/api/sales/{old_sale['id']}/returns", refund, (201,))["stock"] == 1
    call("PUT", f"/api/products/{unmarked}/identifiers", unmarked_setup, invalid, "Admin")
    valid_code = gtin()
    call("PUT", other_endpoint, dict(settings, barcode="X" + barcode, gtin=valid_code),
         (200, 204), "Admin")

    def mark(serial, target=code):
        return f"01{target}21{serial}\x1d91ABCD"

    a, b, c = (mark(s) for s in ("SERIAL1", "SERIAL2", "SERIAL3"))
    receipt = "/api/receipts"
    sale = "/api/sales"
    for bad in (operation(1), operation(2, [a]), operation(2, [a, a]),
                operation(1.5, [a]), operation(1, ["BAD"]), operation(1, [mark("OTHER", valid_code)])):
        call("POST", receipt, bad, invalid)
    post = operation(2, [a, b])
    saved = call("POST", receipt, post, (201,))
    assert saved["stock"] == 2 and call("POST", receipt, post, (201,)) == saved
    # The first valid unit in a failed mixed batch must also roll back.
    call("POST", receipt, operation(2, [c, b]), invalid)
    assert call("GET", stock)["quantity"] == 2
    call("POST", receipt, operation(1, [c]), (201,))
    call("POST", receipt, dict(post, markingCodes=[a, c]), (409,))
    call("PUT", endpoint, dict(settings, requiresMarking=False), invalid, "Admin")
    call("PUT", endpoint, dict(settings, gtin=gtin()), invalid, "Admin")

    call("POST", sale, operation(1), invalid)
    call("POST", sale, operation(2, [a, mark("UNRECEIVED")]), invalid)
    assert call("GET", stock)["quantity"] == 3
    first_sale = operation(1, [a])
    first = call("POST", sale, first_sale, (201,))
    assert first["stock"] == 2 and first["totalAmd"] == 450
    assert call("POST", sale, first_sale, (201,)) == first
    call("POST", sale, operation(1, [a]), invalid)
    second = call("POST", sale, operation(1, [b]), (201,))

    def refund(sale_id, codes, expected):
        return call("POST", f"/api/sales/{sale_id}/returns",
                    operation(1, codes, with_product=False), expected)

    refund(second["id"], [a], invalid)
    refund(first["id"], None, invalid)
    assert call("GET", stock)["quantity"] == 1
    assert refund(first["id"], [a], (201,))["stock"] == 2
    refund(first["id"], [a], invalid)
    assert call("POST", sale, operation(1, [a]), (201,))["stock"] == 1
    assert call("GET", stock)["quantity"] == 1
    print("PASS: product identifier permissions, barcode lookup, marked receipt/sale/return, replay, rollback and cross-product checks.")


if __name__ == "__main__":
    verify(sys.argv[1] if len(sys.argv) > 1 else "http://localhost:5080")
