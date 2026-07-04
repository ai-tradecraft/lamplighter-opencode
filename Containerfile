FROM ghcr.io/astral-sh/uv:python3.12-bookworm-slim

WORKDIR /app

ENV UV_LINK_MODE=copy
ENV PATH="/app/.venv/bin:${PATH}"
ENV LAMPLIGHTER_ADAPTER_DEPLOYMENT_MODE=local_container

COPY pyproject.toml uv.lock README.md ./
COPY src ./src

RUN uv sync --frozen --no-dev

ENTRYPOINT ["lamplighter-opencode"]
