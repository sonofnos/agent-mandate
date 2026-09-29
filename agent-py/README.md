# mandate-agent (Python)

A PydanticAI agent that talks to the .NET gateway only over MCP, with an agent credential. It cannot execute or approve anything; every proposal goes through the gateway's policy, spend reservation and approval path.

The host pins `idempotency_key` to the user's turn in `process_tool_call`, overwriting whatever the model writes, so a retried turn maps onto the same server-side proposal.

```bash
uv sync
MANDATE_URL=http://localhost:5090 uv run pytest          # live tests skip without MANDATE_URL
MANDATE_URL=http://localhost:5090 MANDATE_AGENT_TOKEN=... LLM_BASE_URL=... LLM_API_KEY=... LLM_MODELS=... \
  uv run python -m mandate_agent "send 5000 to mum"
```
