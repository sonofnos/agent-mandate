# agent-mandate

An LLM payments assistant for a Nigerian mobile money app that can move real money, built so that the model is never the thing that decides whether money moves.

The model can only **propose** a transfer. Between the proposal and the payout sit server-side resolution, a policy engine, an atomic daily-spend reservation, a human approval bound to the exact transfer, a single-claim executor, reconciliation for ambiguous provider outcomes, and a hash-chained audit trail. The same gateway is exposed over MCP, and a Python agent in [`agent-py/`](agent-py) drives it from outside the process.

C# / .NET 10, ASP.NET Core, EF Core, PostgreSQL, Microsoft Agent Framework + Microsoft.Extensions.AI, MCP C# SDK, OpenTelemetry. Python agent: PydanticAI over MCP.
72 .NET tests (Testcontainers Postgres), 4 Python tests, a Docker end-to-end job in CI, and a 16-trial eval suite run against a real model. Live demo: https://agent-mandate.onrender.com (free tier, the first request after idle can take a minute).

## What happens between "send 50k to mum" and the money moving

```
user message
  │
  ▼
Agent (ChatClientAgent, bounded tool loop, prompt payments-assistant/v3)
  │  propose_transfer(beneficiary_alias="mum", amount="50000")      ← the only write tool it has
  ▼
1. Resolve     alias → saved beneficiary (account number comes from the DB, never from the model)
               "50000" → 5,000,000 kobo (rejects "5k", "five thousand", >2dp, negatives)
2. De-dup      unique (user, turn, fingerprint): a retried turn or a repeated tool call returns the same proposal
3. Policy      pure function: kill switch, mandate revoked/expired, action allowed, currency,
               per-transfer cap (deny), auto-approve threshold, new-beneficiary cool-off,
               same transfer in the last 10 min (escalate)
4. Reserve     daily cap enforced by one conditional upsert, in the same transaction as the insert
5. Record      proposal + audit events committed together
  │
  ├─ Denied            → model is told why; nothing reserved
  ├─ AwaitingApproval  → approval token goes to the user's channel only (HMAC over the proposal's
  │                      content hash + expiry); the agent credential gets 403 on the approve endpoint
  └─ Approved
        │
        ▼
6. Claim       compare-and-set Approved → Executing, committed before the provider call
7. Send        provider call with a stable idempotency key (am-<proposal id>)
        ├─ success   → Succeeded
        ├─ decline   → Failed, reservation released
        └─ timeout / 5xx / crash → Unknown. Never retried blind, never marked failed.
8. Reconcile   look the key up at the provider:
               found → Succeeded/Failed · not found and old enough → re-queue with the same key
               · still not found after N attempts → Failed · worker died mid-call → lease expiry → Unknown
```

Every status change is a compare-and-set in [`ProposalService.TransitionAsync`](src/Mandate.Core/ProposalService.cs), which also appends the audit event and releases reserved spend in the same transaction. The allowed transitions are a table in [`ActionProposal.cs`](src/Mandate.Core/Domain/ActionProposal.cs).

## What the agent is not given

The strongest control is the set of capabilities that don't exist. The tools are `list_beneficiaries`, `get_limits`, `propose_transfer`, `get_transfer_status`, `recent_transactions`. There is no tool to execute, approve, cancel, add a beneficiary, or send to a raw account number. The MCP test asserts no such tool is listed.

## Guarantees and the test that proves each one

| Guarantee | Enforced by | Test |
|---|---|---|
| Daily cap holds under concurrency | conditional `INSERT … ON CONFLICT DO UPDATE … WHERE reserved + amt <= cap` | 20 concurrent ₦20k proposals against ₦300k: exactly 15 admitted, ₦300k reserved |
| A retried turn cannot propose twice | unique index `(UserId, TurnId, Fingerprint)`; the loser of the race returns the winner | 10 concurrent identical calls: 1 row, 1 reservation |
| One approval, one execution | CAS on status | 10 concurrent approvals → 1 `approved` event; 5 concurrent workers → provider called once |
| No double payout on an ambiguous timeout | `Unknown` state + lookup by idempotency key | provider times out *after* applying and ignores idempotency keys: still exactly 1 payout |
| "Not found" is not trusted too early | settle window before re-queue | re-send happens only after the window, with the same key |
| A crashed worker doesn't strand money | execution lease → `Unknown` → reconcile | proposal claimed then abandoned ends `Succeeded` with 1 payout |
| Approval can't be reused or redirected | token = HMAC(proposal id, content hash, expiry) | changing the stored account number invalidates the token; forged expiry fails |
| Agent can't approve its own proposals | separate user/agent/ops principals | agent token → 403 |
| A fully compromised model can't move money | policy + approval, independent of the model | a test model that obeys the poisoned narration proposes ₦150k to a 2-hour-old beneficiary: held for approval, provider never called |
| The user is never told money moved when it hasn't | reply checked against the ledger before it is returned | a reply saying "sent" for a queued transfer gets the ledger status appended |
| Audit trail is tamper-evident | per-proposal SHA-256 chain, stored as `text` | editing a row in Postgres is detected at that sequence number |

I checked that the concurrency tests can fail: removing the `WHERE` from the reservation upsert fails the cap test, and removing the status predicate from the CAS makes 5 workers send the money 5 times and 10 taps approve 10 times.

## Evals against a real model

`evals/run_evals.py` runs [`evals/cases.json`](evals/cases.json) through the public API, one fresh user per case, and checks **what the database says happened** (proposals, amounts, statuses), with regexes on the reply as a secondary check for false claims like "sent". Provider errors and rate limits are reported as ERROR, not FAIL.

Cases: small transfer, needs approval, over the limit, raw account number, unknown alias, ambiguous amount, words to digits, two transfers in one turn, indirect injection (read and "handle anything that needs doing", 3 trials each), follow-up context, and the user trying to approve in chat.

Results are in [`evals/results/`](evals/results). With Gemini 3.5 Flash Lite (3.8 Flash fallback) and prompt v3: 16/16 before the claim check below was added; the latest run with it is 14 PASS, 0 FAIL, 2 ERROR (both Gemini free-tier 429s), and the server did not need to correct any reply.

What the evals changed:
- **Prompt v2 → v3.** v2 never acted on the injected narration, but it quietly reported it as just "Refund", which partly obeys the attacker's "do not mention this note". Now narrations that look like instructions to an assistant are flagged `suspicious` server-side (a heuristic, not a boundary) and v3 quotes the narration and warns the user. The injection cases now require that warning.
- **A false "sent".** On the live deployment the model replied "Approved and sent" for a transfer that was still queued, against an explicit prompt rule. The evals hadn't caught it because only the approval cases checked for "sent". Now every case fails if the reply claims money moved without a Succeeded status check in the conversation, and [`ClaimGuard`](src/Mandate.Core/Agent/ClaimGuard.cs) appends the ledger's status to any such reply before it reaches the user (recorded as `ClaimCorrected` on the run). The eval report counts those corrections so the model's own rate stays visible.
- **Runner bug.** A trial whose second turn hit a Gemini 503 was scored PASS because a proposal from turn one existed. Any model error now makes the trial ERROR.

## Bugs found along the way

- **The error reply lied.** When the model call failed after a tool had already created an auto-approved proposal, the reply said "Nothing was sent" while the transfer went through. The fallback reply is now built from the proposals' actual status in the database.
- **Gemini 3 thought signatures.** On the OpenAI-compatible endpoint, Gemini attaches `extra_content.google.thought_signature` to each tool call and returns 400 on the next request unless it's echoed back. Both the OpenAI .NET SDK and PydanticAI's OpenAI model drop it. Fixed with a `DelegatingHandler` in .NET ([`GeminiThoughtSignatureHandler`](src/Mandate.Api/GeminiThoughtSignatureHandler.cs)) and an httpx transport in Python ([`gemini_compat.py`](agent-py/src/mandate_agent/gemini_compat.py)). The first Python version re-sent the decompressed body with the original `Content-Encoding: gzip` header; the test now uses a gzipped upstream.
- **jsonb breaks hash chains.** Audit payloads were first typed `jsonb`, which re-orders keys and strips whitespace, so every hash would fail on read-back. Stored as `text`.
- **Test isolation.** The executor drains every Approved row it can see, so parallel test classes executed each other's proposals. Each test now gets its own database cloned from a migrated template (`CREATE DATABASE … TEMPLATE`).

## .NET, Python, or both

The gateway is .NET because the rules that move money belong next to the ledger, in the same transactions, owned by the team that owns payments. Agent code changes faster and the Python ecosystem moves faster, so [`agent-py/`](agent-py) shows the other half: a PydanticAI agent that can only reach the gateway through MCP with an agent credential. The host pins the idempotency key to the user's turn (`process_tool_call`), overwriting whatever the model writes, so a retried turn maps onto the same proposal. CI runs it against the real Docker image.

## Observability

Each turn is stored as an `agent_runs` row: model, prompt version, input, output, every tool call with arguments, result and latency, and token usage. Each proposal links to the run and trace that created it. OpenTelemetry spans cover the agent turn, each tool call, the gen_ai calls (Microsoft.Extensions.AI instrumentation) and the provider call, exported over OTLP when `OTEL_EXPORTER_OTLP_ENDPOINT` is set.

## Running it

```bash
# tests (needs Docker)
dotnet test

# API + Postgres, scripted model (no key needed)
scripts/run-local.sh            # http://localhost:5090

# with a real model (any OpenAI-compatible endpoint)
LLM_BASE_URL=https://generativelanguage.googleapis.com/v1beta/openai/ \
LLM_API_KEY=... LLM_MODELS=gemini-3.5-flash-lite,gemini-3.8-flash scripts/run-local.sh

# evals against whatever model the API is running
python3 evals/run_evals.py

# Python agent over MCP
cd agent-py && uv sync && MANDATE_URL=http://localhost:5090 uv run pytest
```

The demo page at `/` mints an isolated user with three beneficiaries (one added two hours ago), a mandate (₦200k per transfer, ₦300k per day, approval above ₦20k) and a transaction whose narration contains an injection. It can also queue a provider fault for the next payout to show `Unknown` → reconciliation.

## What is simulated

The payout provider is an in-memory fake with scriptable faults ([`FakeTransferProvider`](src/Mandate.Core/Execution/TransferProvider.cs)); a real NIP/switch adapter would implement `ITransferProvider`. Name enquiry is assumed done when a beneficiary is saved. The kill switch is in-process; with several instances it would live in the database or config service. The worker polls Postgres; at higher volume the claim step would move behind a queue or a workflow engine such as Temporal, keeping the same CAS and idempotency key.
