"""End-to-end checks against the running Compose stack; Python 3 standard library only."""
import base64
import json
from pathlib import Path
import subprocess
import urllib.error
import urllib.request

ROOT = Path(__file__).resolve().parents[1]
BASE = "http://127.0.0.1:8090"
FIELDS = {
    "is_error", "error_code", "error_message", "elements_count", "emails_count",
    "url", "decrypted_plain_text", "elements_attr_list", "emails_list",
}
PLAINTEXT = "AES Error: Object reference not set to an instance of an object."
EMAILS = ["webmaster@rbc.ru", "privet@test.com", "hh_test_task@gmail.com",
          "letters@rbc.ru", "letters@rbc.ru"]
checks = 0


def sql(statement):
    result = subprocess.run(
        ["docker", "compose", "exec", "-T", "postgres", "psql", "-U", "testjob",
         "-d", "testjob", "-At", "-v", "ON_ERROR_STOP=1", "-c", statement],
        cwd=ROOT, check=True, capture_output=True, text=True)
    return result.stdout.strip()


def count():
    return int(sql("SELECT count(*) FROM elements"))


def post(payload=None, *, raw=None, content_type="application/json"):
    body = raw if raw is not None else json.dumps(payload).encode("utf-8")
    request = urllib.request.Request(
        BASE + "/api/process", data=body, headers={"Content-Type": content_type})
    try:
        response = urllib.request.urlopen(request, timeout=30)
    except urllib.error.HTTPError as error:
        response = error
    with response:
        text = response.read().decode("utf-8")
        data = json.loads(text)
        assert set(data) == FIELDS, data
        assert "\n  " in text, "JSON response should be indented"
        assert "application/json" in response.headers["Content-Type"]
        return response.status, data, text


def check_error(expected_code, payload=None, **kwargs):
    global checks
    before = count()
    status, data, _ = post(payload, **kwargs)
    assert status == 400, (status, data)
    assert data["is_error"] == 1 and data["error_code"] == expected_code, data
    assert data["error_message"], data
    assert data["elements_count"] == 0 and data["elements_attr_list"] == [], data
    assert count() == before, "Rejected request inserted database rows"
    checks += 1


def b64(text):
    return base64.b64encode(text.encode("utf-8")).decode("ascii")


def check_emails(original, html, expected):
    global checks
    before = count()
    status, data, _ = post({**original, "page_b64": b64(html),
                          "selector": "element-that-does-not-exist"})
    assert status == 200 and data["is_error"] == 0, (html, status, data)
    assert data["emails_list"] == expected, (html, data["emails_list"], expected)
    assert data["emails_count"] == len(expected), data
    assert count() == before, "Email-only check should not insert rows"
    checks += 1


def main():
    global checks
    original = json.loads((ROOT / "json_payload_1.txt").read_text(encoding="utf-8"))
    for number, expected_count, url in [(1, 238, "https://test.com/page1"),
                                       (2, 9, "https://test.com/page123")]:
        payload = json.loads((ROOT / f"json_payload_{number}.txt").read_text(encoding="utf-8"))
        before = count()
        status, data, text = post(payload)
        assert status == 200 and data["is_error"] == 0, (status, data)
        assert data["error_code"] == data["error_message"] == "", data
        assert data["elements_count"] == len(data["elements_attr_list"]) == expected_count
        assert data["emails_count"] == 5 and data["emails_list"] == EMAILS, data
        assert data["url"] == url and data["decrypted_plain_text"] == PLAINTEXT
        assert count() == before + expected_count
        stored = json.loads(sql(
            "SELECT json_agg(t.attribute_value ORDER BY t.id) FROM "
            f"(SELECT id, attribute_value FROM elements ORDER BY id DESC LIMIT {expected_count}) t"))
        assert stored == data["elements_attr_list"], "Saved attributes differ from response"
        (ROOT / f"json_result_{number}.txt").write_text(text + "\n", encoding="utf-8")
        checks += 1

    for field in original:
        payload = original.copy()
        del payload[field]
        check_error("MISSING_PARAMETER", payload)
        check_error("MISSING_PARAMETER", {**original, field: None})
    for field, code in [("selector", "EMPTY_SELECTOR"), ("attribute", "EMPTY_ATTRIBUTE")]:
        for value in ["", " \t\n"]:
            check_error(code, {**original, field: value})
    for field, code in [("url_b64", "URL"), ("page_b64", "PAGE"),
                        ("key_bytes_b64", "KEY"), ("encrypted_text_bytes_b64", "CIPHERTEXT")]:
        check_error(f"INVALID_{code}_BASE64", {**original, field: "%%%"})
    check_error("INVALID_URL", {**original, "url_b64": b64("not a URL")})
    check_error("INVALID_URL", {**original, "url_b64": b64("file:///etc/passwd")})
    check_error("INVALID_URL_UTF8", {**original, "url_b64": "/w=="})
    check_error("INVALID_PAGE_UTF8", {**original, "page_b64": "/w=="})
    check_error("INVALID_SELECTOR", {**original, "selector": "a["})
    check_error("INVALID_ATTRIBUTE", {**original, "attribute": "href bad"})
    check_error("INVALID_KEY_SIZE", {**original, "key_bytes_b64": b64("short")})
    check_error("INVALID_CIPHERTEXT_SIZE", {**original, "encrypted_text_bytes_b64": b64("short")})
    for raw in [b"{", b"null", b"[]", b"", b'{"selector": 12}']:
        check_error("INVALID_JSON", raw=raw)

    before = count()
    status, data, _ = post({**original, "selector": "element-that-does-not-exist"})
    assert status == 200 and data["elements_count"] == 0 and data["elements_attr_list"] == []
    assert count() == before
    checks += 1

    html = '<p data-note="Привет &amp; мир">first+tag@example.org first+tag@example.org</p><p>Без атрибута</p>'
    before = count()
    status, data, _ = post({**original, "page_b64": b64(html),
                          "selector": "p", "attribute": "data-note"})
    assert status == 200 and data["elements_attr_list"] == ["Привет & мир", ""], data
    assert data["emails_list"] == ["first+tag@example.org"] * 2, data
    assert count() == before + 2
    stored_html = json.loads(sql(
        "SELECT json_agg(t.html ORDER BY t.id) FROM "
        "(SELECT id, html FROM elements ORDER BY id DESC LIMIT 2) t"))
    assert stored_html == ['<p data-note="Привет &amp; мир">first+tag@example.org first+tag@example.org</p>',
                           '<p>Без атрибута</p>'], stored_html
    checks += 1

    for html, expected in [
        ("o'connor@example.org +tag@example.org", ["o'connor@example.org", "+tag@example.org"]),
        ("o'connor@example.org o'connor@example.org", ["o'connor@example.org"] * 2),
        ("'lead@example.org trail'@example.org", ["'lead@example.org", "trail'@example.org"]),
        ('<a href="mailto:o\'connor@example.org">mail</a>', ["o'connor@example.org"]),
        ("<span data-email='alice@example.org'>mail</span>", ["alice@example.org"]),
        ('<span data-email="o\'connor@example.org">mail</span>', ["o'connor@example.org"]),
        ("<span data-email=alice@example.org>mail</span>", ["alice@example.org"]),
        ("Contact (+tag@example.org), then Alice.Smith@sub-domain.example.org.",
         ["+tag@example.org", "Alice.Smith@sub-domain.example.org"]),
        ("alice@example..org alice@-example.org alice@example-.org", []),
        ("alice..smith@example.org .alice@example.org alice.@example.org", []),
        ("alice@@example.org alice@example.org_bad alice@example.org7", []),
        ("alice@example.org..bad alice@example.org- alice@example.org@other.org", []),
        ('"quoted local"@example.org "inner@example.org"@example.net', []),
        ("юзер@example.org alice@пример.org", []),
    ]:
        check_emails(original, html, expected)

    status, data, _ = post(original, content_type="text/plain")
    assert status == 415 and data["is_error"] == 1, (status, data)
    checks += 1
    with urllib.request.urlopen(BASE + "/api/swagger/v1/swagger.json") as response:
        document = json.load(response)
        assert "/api/process" in document["paths"]
        schema = document["components"]["schemas"]["ProcessingRequest"]
        assert set(schema["properties"]) == set(original)
        assert set(schema.get("required", [])) == set(original), schema
        for name, definition in schema["properties"].items():
            assert definition["type"] == "string" and not definition.get("nullable", False), (name, definition)
        assert document["paths"]["/api/process"]["post"]["requestBody"]["required"] is True
    checks += 1
    print(f"PASS: {checks} integration checks; both json_result files saved.")


if __name__ == "__main__":
    main()
