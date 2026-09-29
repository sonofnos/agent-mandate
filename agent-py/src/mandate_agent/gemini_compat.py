"""Gemini 3's OpenAI-compatible endpoint returns tool_calls[].extra_content (a thought signature)
and rejects the next request with 400 unless it is sent back. The OpenAI SDK drops the field.
Same fix as GeminiThoughtSignatureHandler on the .NET side: remember it by tool call id, re-attach."""

from __future__ import annotations

import json
from collections import OrderedDict

import httpx2 as httpx


class ThoughtSignatureTransport(httpx.AsyncBaseTransport):
    def __init__(self, inner: httpx.AsyncBaseTransport | None = None, max_entries: int = 10_000):
        self._inner = inner or httpx.AsyncHTTPTransport()
        self._by_call_id: OrderedDict[str, object] = OrderedDict()
        self._max = max_entries

    async def handle_async_request(self, request: httpx.Request) -> httpx.Response:
        if request.headers.get("content-type", "").startswith("application/json") and request.content:
            body = json.loads(request.content)
            if self._reinject(body.get("messages") or []):
                content = json.dumps(body).encode()
                headers = {k: v for k, v in request.headers.items() if k.lower() != "content-length"}
                request = httpx.Request(request.method, request.url, headers=headers, content=content, extensions=request.extensions)

        response = await self._inner.handle_async_request(request)
        if response.status_code == 200 and response.headers.get("content-type", "").startswith("application/json"):
            raw = await response.aread()  # already decompressed
            self._remember(json.loads(raw))
            headers = {k: v for k, v in response.headers.items() if k.lower() not in ("content-encoding", "content-length", "transfer-encoding")}
            return httpx.Response(response.status_code, headers=headers, content=raw, extensions=response.extensions)
        return response

    def _reinject(self, messages: list[dict]) -> bool:
        changed = False
        for m in messages:
            for call in m.get("tool_calls") or []:
                if "extra_content" not in call and call.get("id") in self._by_call_id:
                    call["extra_content"] = self._by_call_id[call["id"]]
                    changed = True
        return changed

    def _remember(self, body: dict) -> None:
        for choice in body.get("choices") or []:
            for call in (choice.get("message") or {}).get("tool_calls") or []:
                if call.get("id") and call.get("extra_content") is not None:
                    self._by_call_id[call["id"]] = call["extra_content"]
        while len(self._by_call_id) > self._max:
            self._by_call_id.popitem(last=False)

    async def aclose(self) -> None:
        await self._inner.aclose()
