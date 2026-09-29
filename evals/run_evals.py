#!/usr/bin/env python3
"""Runs cases.json against a live API and the real model behind it.

Each case gets a fresh demo user. Checks are made against what the database says happened
(the proposals), not an LLM judge's reading of the reply. Reply regexes are a secondary check
for false claims such as "sent" on a transfer that is still waiting for approval.
Rate-limit or provider errors are reported as ERROR, not FAIL.
"""
import json, os, re, sys, time, urllib.request, uuid, datetime, pathlib

BASE = os.environ.get("MANDATE_URL", "http://localhost:5090").rstrip("/")
HERE = pathlib.Path(__file__).parent
# Ignore system proxy settings (macOS can inject one urllib honours and curl does not).
OPENER = urllib.request.build_opener(urllib.request.ProxyHandler({}))


def call(path, token=None, body=None, method=None):
    req = urllib.request.Request(BASE + path, method=method or ("POST" if body is not None else "GET"),
                                 data=None if body is None else json.dumps(body).encode(),
                                 headers={"Content-Type": "application/json", **({"Authorization": f"Bearer {token}"} if token else {})})
    with OPENER.open(req, timeout=120) as r:
        return json.loads(r.read() or b"null")


def run_case(case):
    s = call("/demo/session", body={})
    history, replies, errors = [], [], []
    for turn in case["turns"]:
        r = call("/agent/chat", s["agentToken"], {"message": turn, "turnId": str(uuid.uuid4()), "history": history})
        replies.append(r["reply"])
        if r.get("error"):
            errors.append(r["error"])
        history += [{"role": "user", "text": turn}, {"role": "assistant", "text": r["reply"]}]
    time.sleep(1.5)  # let the worker pick up auto-approved transfers
    props = call("/me/proposals", s["userToken"])
    return check(case["expect"], props, replies[-1]), props, replies, errors


def check(e, props, reply):
    fails = []
    if "proposals" in e:
        want = e["proposals"]
        if len(props) != len(want):
            fails.append(f"expected {len(want)} proposals, got {len(props)}")
        for w in want:
            match = [p for p in props if p["to"] == w["to"] and p["amountMinor"] == w["amountMinor"]
                     and ("status" not in w or p["status"] in w["status"])]
            if not match:
                fails.append(f"missing proposal {w}")
    if "max_proposals" in e and len(props) > e["max_proposals"]:
        fails.append(f"more than {e['max_proposals']} proposals")
    for p in props:
        if p["status"] in e.get("no_status", []):
            fails.append(f"proposal reached {p['status']}")
    if "reply_must_match" in e and not re.search(e["reply_must_match"], reply, re.I):
        fails.append(f"reply does not match /{e['reply_must_match']}/")
    if "reply_must_not_match" in e and re.search(e["reply_must_not_match"], reply, re.I):
        fails.append(f"reply matches forbidden /{e['reply_must_not_match']}/")
    return fails


def main():
    cases = json.loads((HERE / "cases.json").read_text())
    only = set(sys.argv[1:])
    model = call("/health")["model"]
    report, totals = [], {"PASS": 0, "FAIL": 0, "ERROR": 0}
    for case in cases:
        if only and case["id"] not in only:
            continue
        for trial in range(case.get("trials", 1)):
            try:
                fails, props, replies, errors = run_case(case)
                verdict = "ERROR" if errors else ("FAIL" if fails else "PASS")
            except Exception as ex:  # rate limits, network
                fails, props, replies, errors, verdict = [], [], [], [str(ex)], "ERROR"
            totals[verdict] += 1
            print(f"{verdict:5} {case['id']}#{trial}  {'; '.join(fails or errors)[:160]}")
            if verdict != "PASS":
                print("      reply:", (replies or [''])[-1][:240].replace("\n", " "))
            report.append({"case": case["id"], "trial": trial, "verdict": verdict, "fails": fails, "errors": errors,
                           "replies": replies, "proposals": [{k: p[k] for k in ("to", "amountMinor", "status", "reasons")} for p in props]})
            time.sleep(float(os.environ.get("EVAL_PAUSE", "2")))
    stamp = datetime.datetime.now(datetime.timezone.utc).strftime("%Y%m%dT%H%M%SZ")
    out = HERE / "results" / f"{stamp}.json"
    out.write_text(json.dumps({"model": model, "base": BASE, "totals": totals, "results": report}, indent=2))
    print(f"\n{model}: {totals}  ->  {out.relative_to(HERE.parent)}")
    sys.exit(1 if totals["FAIL"] else 0)


if __name__ == "__main__":
    main()
