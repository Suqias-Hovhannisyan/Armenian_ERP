"""Integration test for a mixed POS sale against an isolated API and SQL Server."""
from concurrent.futures import ThreadPoolExecutor
from functools import partial
import sys
import uuid

from test_marking import api_request, gtin


def request_body(**values):
    return {"requestId": str(uuid.uuid4()), **values}


def verify(base):
    call = partial(api_request, base)

    def product(name, price):
        body = request_body(sku="POS-" + uuid.uuid4().hex, name=name, unitPriceAmd=price)
        return call("POST", "/api/products", body, (201,))["id"]

    def sell(body, status=201):
        return call("POST", "/api/sales/cart", body, (status,))

    plain = product("Weighted ordinary item", 151)
    marked = product("Marked item", 450)
    marked_gtin = gtin()
    call("PUT", f"/api/products/{marked}/identifiers", {
        "barcode": "POS" + uuid.uuid4().hex, "gtin": marked_gtin,
        "atgCode": "2201100000", "requiresMarking": True,
    }, (200, 204), "Admin")

    def unit(serial):
        return f"01{marked_gtin}21{serial}\x1d91ABCD"

    first, second, third = (unit(serial) for serial in ("A", "B", "C"))
    call("POST", "/api/receipts", request_body(productId=plain, quantity=5), (201,))
    call("POST", "/api/receipts", request_body(productId=marked, quantity=3,
                                              markingCodes=[first, second, third]), (201,))

    def stocks():
        return tuple(call("GET", f"/api/products/{p}/stock")["quantity"] for p in (plain, marked))

    plain_line = {"productId": plain, "quantity": 1.5}
    marked_line = {"productId": marked, "quantity": 2, "markingCodes": [first, second]}
    for bad_line, status in (
        (dict(marked_line, markingCodes=[first, unit("UNKNOWN")]), 409),
        (dict(marked_line, markingCodes=[first, first]), 400),
        (dict(marked_line, markingCodes=[first]), 400),
    ):
        sell(request_body(lines=[plain_line, bad_line]), status)
        assert stocks() == (5, 3), "A rejected basket changed stock"

    basket = request_body(lines=[plain_line, marked_line])
    with ThreadPoolExecutor(max_workers=2) as pool:
        receipt, replay = pool.map(sell, (basket, basket))
    assert replay == receipt and sell(basket) == receipt
    assert receipt["totalAmd"] == 1127  # 1.5 x 151 rounds to 227, plus 2 x 450.
    ordinary_sale, marked_sale = receipt["lines"]
    assert len({receipt["id"], ordinary_sale["id"], marked_sale["id"]}) == 3
    for line, expected in (
        (ordinary_sale, (plain, 1.5, 3.5, 151, 227)),
        (marked_sale, (marked, 2, 1, 450, 900)),
    ):
        assert tuple(line[k] for k in ("productId", "quantity", "stock", "unitPriceAmd", "totalAmd")) == expected
    assert stocks() == (3.5, 1)
    sell(dict(basket, lines=[marked_line]), 409)
    sell(request_body(lines=[plain_line, dict(marked_line, quantity=1, markingCodes=[first])]), 409)
    assert stocks() == (3.5, 1)

    def refund(line, body, status):
        return call("POST", f"/api/sales/{line['id']}/returns", body, (status,))

    refund(marked_sale, request_body(quantity=1, markingCodes=[third]), 409)
    refund(marked_sale, request_body(quantity=1, markingCodes=[first]), 201)
    assert stocks() == (3.5, 2)
    refund(ordinary_sale, request_body(quantity=1), 201)
    assert stocks() == (4.5, 2)
    print("PASS: mixed receipt (1,127 AMD), independent stock and returns, atomic rejection, concurrent retry.")


if __name__ == "__main__":
    verify(sys.argv[1] if len(sys.argv) > 1 else "http://localhost:5080")
