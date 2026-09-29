import json
import os
import urllib.request

import pytest
from pydantic_ai import models
from pydantic_ai.messages import ModelMessage, ModelResponse, TextPart, ToolCallPart, ToolReturnPart
from pydantic_ai.models.function import AgentInfo, FunctionModel

from mandate_agent import TurnDeps, build_agent, pin_idempotency_key

models.ALLOW_MODEL_REQUESTS = False
BASE = os.environ.get("MANDATE_URL", "").rstrip("/")
OPENER = urllib.request.build_opener(urllib.request.ProxyHandler({}))


class _Ctx:
    def __init__(self, turn_id: str):
        self.deps = TurnDeps(turn_id)


async def test_host_overrides_whatever_key_the_model_invents():
    seen = []

    async def call_tool(name, args):
        seen.append((name, args))
        return "ok"

    await pin_idempotency_key(_Ctx("turn-1"), call_tool, "propose_transfer", {"amount": "5000", "idempotency_key": "model-made-up"})
    await pin_idempotency_key(_Ctx("turn-1"), call_tool, "get_limits", {})
    assert seen[0][1]["idempotency_key"] == "turn-1"
    assert "idempotency_key" not in seen[1][1]


def _http(path, token=None, body=None):
    req = urllib.request.Request(BASE + path, method="POST" if body is not None else "GET",
                                 data=None if body is None else json.dumps(body).encode(),
                                 headers={"Content-Type": "application/json", **({"Authorization": f"Bearer {token}"} if token else {})})
    with OPENER.open(req, timeout=30) as r:
        return json.loads(r.read() or b"null")


def _model_that_proposes_twice():
    """A model that calls propose_transfer twice in one turn, each time inventing a new key."""

    def respond(messages: list[ModelMessage], info: AgentInfo) -> ModelResponse:
        returns = [p for m in messages for p in getattr(m, "parts", []) if isinstance(p, ToolReturnPart)]
        if len(returns) < 2:
            return ModelResponse(parts=[ToolCallPart("propose_transfer", {
                "beneficiary_alias": "mum", "amount": "5000", "narration": "from python",
                "idempotency_key": f"invented-{len(returns)}"})])
        return ModelResponse(parts=[TextPart(str(returns[-1].content))])

    return FunctionModel(respond)


@pytest.mark.skipif(not BASE, reason="set MANDATE_URL to run against a live gateway")
async def test_python_agent_goes_through_dotnet_policy_over_mcp():
    s = _http("/demo/session", body={})
    agent = build_agent(_model_that_proposes_twice(), f"{BASE}/mcp", s["agentToken"])
    async with agent:
        result = await agent.run("send 5000 to mum", deps=TurnDeps("py-turn-1"))
        await agent.run("send 5000 to mum", deps=TurnDeps("py-turn-1"))  # whole turn retried

    proposals = _http("/me/proposals", s["userToken"])
    assert len(proposals) == 1, proposals
    assert proposals[0]["to"] == "NGOZI OKAFOR" and proposals[0]["amountMinor"] == 500000
    assert "approvalToken" not in result.output


@pytest.mark.skipif(not BASE, reason="set MANDATE_URL to run against a live gateway")
async def test_agent_credential_cannot_approve():
    s = _http("/demo/session", body={})
    req = urllib.request.Request(f"{BASE}/me/proposals/00000000-0000-0000-0000-000000000000/approve", method="POST",
                                 data=b'{"token":"x"}', headers={"Content-Type": "application/json", "Authorization": f"Bearer {s['agentToken']}"})
    with pytest.raises(urllib.error.HTTPError) as e:
        OPENER.open(req, timeout=30)
    assert e.value.code == 403


async def test_thought_signature_is_echoed_back_on_the_next_request():
    import httpx2 as httpx

    from mandate_agent.gemini_compat import ThoughtSignatureTransport

    sent = []

    class Upstream(httpx.AsyncBaseTransport):
        async def handle_async_request(self, request):
            sent.append(json.loads(request.content))
            body = {"choices": [{"message": {"role": "assistant", "tool_calls": [
                {"id": "call_1", "type": "function", "function": {"name": "get_limits", "arguments": "{}"},
                 "extra_content": {"google": {"thought_signature": "sig-abc"}}}]}}]}
            import gzip
            return httpx.Response(200, headers={"content-type": "application/json", "content-encoding": "gzip"},
                                  content=gzip.compress(json.dumps(body).encode()))

    async with httpx.AsyncClient(transport=ThoughtSignatureTransport(Upstream())) as client:
        await client.post("https://x.googleapis.com/chat", json={"messages": [{"role": "user", "content": "hi"}]})
        await client.post("https://x.googleapis.com/chat", json={"messages": [
            {"role": "assistant", "tool_calls": [{"id": "call_1", "type": "function", "function": {"name": "get_limits", "arguments": "{}"}}]},
            {"role": "tool", "tool_call_id": "call_1", "content": "{}"}]})

    assert sent[1]["messages"][0]["tool_calls"][0]["extra_content"] == {"google": {"thought_signature": "sig-abc"}}
