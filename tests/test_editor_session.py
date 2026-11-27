"""Check the running Admin's persistent login, antiforgery, and logout."""
import os
import sys
from html.parser import HTMLParser
from http.cookiejar import CookieJar
from urllib.error import HTTPError
from urllib.parse import urlencode
from urllib.request import HTTPCookieProcessor, Request, build_opener


class PageForms(HTMLParser):
    def __init__(self, html):
        super().__init__()
        self.forms = {}
        self.current = None
        self.feed(html)

    def handle_starttag(self, tag, attrs):
        attrs = dict(attrs)
        if tag == "form":
            self.current = self.forms.setdefault(attrs.get("action", ""), {})
        elif tag == "input" and self.current is not None and attrs.get("type") == "hidden":
            self.current[attrs["name"]] = attrs.get("value", "")

    def handle_endtag(self, tag):
        if tag == "form":
            self.current = None


def verify(base, key):
    jar = CookieJar()
    browser = build_opener(HTTPCookieProcessor(jar))
    login, logout = "/editor-session/login", "/editor-session/logout"

    def post(path, values, expected):
        try:
            response = browser.open(Request(base + path, data=urlencode(values).encode()))
        except HTTPError as error:
            response = error
        with response:
            assert response.code == expected, (path, response.code)
            return response.read().decode()

    def editor():
        with browser.open(base + "/model-editor") as response:
            return PageForms(response.read().decode()).forms

    fields = editor()[login]
    assert "__RequestVerificationToken" in fields
    post(login, {"accessKey": key}, 400)
    assert login in editor()
    post(login, {**fields, "accessKey": "wrong"}, 200)
    assert login in editor()
    html = post(login, {**fields, "accessKey": key}, 200)
    assert logout in PageForms(html).forms and key not in html
    cookie = next(item for item in jar if item.name == "Retail.EditorSession")
    assert cookie.expires and not cookie.discard
    assert cookie.has_nonstandard_attr("httponly") or cookie.has_nonstandard_attr("HttpOnly")
    assert cookie.value != key
    # Reconstruct the browser from persistent cookies, as on reopening after exit.
    reopened = CookieJar()
    for item in jar:
        if not item.discard:
            reopened.set_cookie(item)
    browser = build_opener(HTTPCookieProcessor(reopened))
    with browser.open(base + "/pos") as response:
        assert response.status == 200
    fields = editor()[logout]
    post(logout, {}, 400)
    assert logout in editor()
    post(logout, fields, 200)
    assert login in editor()
    # Logout revokes the previous server session even if an old cookie is replayed.
    reopened.set_cookie(cookie)
    assert login in editor()
    print("PASS: editor login, browser reopen, navigation, antiforgery, logout and revocation.")


if __name__ == "__main__":
    verify(sys.argv[1].rstrip("/"), os.environ["RetailAccess__AdminToken"])
