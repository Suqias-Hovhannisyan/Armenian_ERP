"""Exercise the first retail flow against a running API and test database."""
from concurrent.futures import ThreadPoolExecutor
import json
import sys
import urllib.error
import urllib.request
import uuid

base_url = sys.argv[1].rstrip("/") if len(sys.argv) > 1 else "http://localhost:5080"


def request(path, body=None, expected=None):
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(base_url + path, data=data, headers={"Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=20) as response:
            if expected:
                raise AssertionError(f"{path}: expected {expected}, got {response.status}")
            return json.load(response)
    except urllib.error.HTTPError as error:
        if error.code not in (expected or ()):
            raise AssertionError(f"{path}: HTTP {error.code}: {error.read().decode()}") from error


def operation(**fields):
    return {"requestId": str(uuid.uuid4()), **fields}


product = request("/api/products", operation(sku=f"SMOKE-{uuid.uuid4().hex[:12]}", name="Smoke item", unitPriceAmd=450))
product_id = product["id"]
stock_url = f"/api/products/{product_id}/stock"
assert request(stock_url)["quantity"] == 0
receipt = operation(productId=product_id, quantity=10)
assert request("/api/receipts", receipt)["stock"] == 10
sale = operation(productId=product_id, quantity=3)
with ThreadPoolExecutor(max_workers=2) as pool:
    posted_sale, concurrent_replay = list(pool.map(lambda _: request("/api/sales", sale), range(2)))
assert concurrent_replay == posted_sale
assert posted_sale["stock"] == 7 and posted_sale["totalAmd"] == 1350
assert request("/api/sales", sale) == posted_sale
request("/api/sales", {**sale, "quantity": 2}, (409,))
return_url = f"/api/sales/{posted_sale['id']}/returns"
refund = request(return_url, operation(quantity=1))
assert refund["stock"] == 8 and refund["totalAmd"] == 450
request(return_url, operation(quantity=3), (400, 409, 422))
request("/api/sales", operation(productId=product_id, quantity=9), (400, 409, 422))
request("/api/receipts", operation(productId=product_id, quantity=0), (400, 422))
assert request(stock_url)["quantity"] == 8
print(f"PASS: receipt, sale, replay, partial return and rejected changes. Product: {product_id}")
