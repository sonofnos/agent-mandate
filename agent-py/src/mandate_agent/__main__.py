"""python -m mandate_agent "send 5000 to mum"  (needs MANDATE_URL, MANDATE_AGENT_TOKEN, LLM_* env)."""

import asyncio
import os
import sys

from .agent import TurnDeps, build_agent, model_from_env


async def main() -> None:
    base = os.environ.get("MANDATE_URL", "http://localhost:5090").rstrip("/")
    agent = build_agent(model_from_env(), f"{base}/mcp", os.environ["MANDATE_AGENT_TOKEN"])
    async with agent:
        result = await agent.run(" ".join(sys.argv[1:]), deps=TurnDeps())
    print(result.output)


asyncio.run(main())
