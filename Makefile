# Makefile for lamplighter-opencode
#
# Single portable entrypoint for setup, dependency installation, and CI.
# Everything runs through `uv`, which keeps all dependencies isolated (no
# system packages are mutated):
#
#   - The project's Python interpreters (3.12+) and the hooks' Python 3.10 are
#     downloaded into uv's managed cache (`uv python install`), never the
#     system Python.
#   - `pre-commit` and `just` are installed as isolated uv tools
#     (`uv tool install`) and invoked via `uvx`, so they need not be on PATH.
#   - Project dependencies live in the uv-managed `.venv` (`uv sync`).
#
# The ONLY machine-level prerequisite is `uv` itself (a self-contained binary
# in ~/.local/bin). `make install-deps` bootstraps it automatically if missing.
#
# Usage:
#   make              # same as `make help`
#   make doctor       # report which tools are present/missing (no changes)
#   make install-deps # install everything via uv (bootstraps uv if missing)
#   make setup        # install-deps + wire up the committed git hooks
#   make ci           # run the full CI check suite (what pipelines invoke)
#   make lint         # run all pre-commit hooks against all files
#   make qa           # run the Python checks (ruff format/lint, ty, pytest)
#   make test-chat-through-harness
#                     # run Python + ASP.NET + React POC checks

.DEFAULT_GOAL := help

# Python version required by the pre-commit hooks (commitizen,
# sync-pre-commit-deps). Sourced from uv's managed cache, not system Python.
HOOK_PYTHON := 3.10

# Directory of the uv-managed HOOK_PYTHON interpreter, prepended to PATH when
# running pre-commit so its `language_version: python3.10` hooks resolve to the
# uv interpreter. Evaluated lazily (only when a recipe that uses it runs) and
# silently empty if uv/Python isn't installed yet.
HOOK_PYTHON_BIN = $(shell uv python find $(HOOK_PYTHON) 2>/dev/null | xargs -I{} dirname {} 2>/dev/null)
PRECOMMIT_PATH = $(if $(HOOK_PYTHON_BIN),$(HOOK_PYTHON_BIN):$(PATH),$(PATH))

# pre-commit and just run as isolated uv tools via uvx (no PATH install needed).
PRECOMMIT := uvx pre-commit
JUST := uvx --from rust-just just

.PHONY: help doctor install-deps setup ci lint qa run-chat-through-harness test-chat-through-harness test-real-backend print-real-backend-fingerprint check-uv

help: ## Show available targets
	@grep -E '^[a-zA-Z_-]+:.*?## .*$$' $(MAKEFILE_LIST) | awk 'BEGIN {FS = ":.*?## "}; {printf "  \033[36m%-18s\033[0m %s\n", $$1, $$2}'

# ---------------------------------------------------------------------------
# DEPENDENCY MANAGEMENT (uv-isolated)
# ---------------------------------------------------------------------------
# `make doctor` only reports; it never changes the machine.
doctor: ## Report which dependencies are present or missing (makes no changes)
	@echo "Dependency check (everything is uv-managed; uv is the only machine-level prerequisite):"
	@if command -v uv > /dev/null 2>&1; then \
		echo "  [ok]      uv                $$(command -v uv)"; \
	else \
		echo "  [MISSING] uv                run 'make install-deps' (auto-bootstraps) or see https://docs.astral.sh/uv/"; \
	fi
	@command -v uv > /dev/null 2>&1 || { echo "  (install uv first; the checks below need it)"; exit 0; }
	@uv python find $(HOOK_PYTHON) > /dev/null 2>&1 \
		&& echo "  [ok]      Python $(HOOK_PYTHON) (hooks) $$(uv python find $(HOOK_PYTHON) 2>/dev/null)" \
		|| echo "  [MISSING] Python $(HOOK_PYTHON) (hooks) run 'make install-deps' (uv python install $(HOOK_PYTHON))"
	@uv python find 3.12 > /dev/null 2>&1 \
		&& echo "  [ok]      Python 3.12+      $$(uv python find 3.12 2>/dev/null)" \
		|| echo "  [MISSING] Python 3.12+      run 'make install-deps' (uv python install 3.12)"
	@uv tool list 2>/dev/null | grep -q '^pre-commit' \
		&& echo "  [ok]      pre-commit        uv tool" \
		|| echo "  [MISSING] pre-commit        run 'make install-deps' (uv tool install pre-commit)"
	@uv tool list 2>/dev/null | grep -q '^rust-just' \
		&& echo "  [ok]      just              uv tool (rust-just)" \
		|| echo "  [MISSING] just              run 'make install-deps' (uv tool install rust-just)"
	@test -d .venv \
		&& echo "  [ok]      project .venv     ./.venv" \
		|| echo "  [MISSING] project .venv     run 'make install-deps' (uv sync)"

# `make install-deps` installs everything through uv. The only thing it cannot
# do through uv is install uv itself, so it bootstraps uv first (via the
# official self-contained installer; no system packages touched).
install-deps: ## Install all dependencies via uv (bootstraps uv itself if missing)
	@command -v uv > /dev/null 2>&1 || { \
		echo 'uv not found; bootstrapping it (self-contained, installs to ~/.local/bin)...'; \
		if command -v curl > /dev/null 2>&1; then \
			curl -LsSf https://astral.sh/uv/install.sh | sh; \
		elif command -v wget > /dev/null 2>&1; then \
			wget -qO- https://astral.sh/uv/install.sh | sh; \
		else \
			echo 'Error: need curl or wget to bootstrap uv. Install uv manually: https://docs.astral.sh/uv/getting-started/installation/' 1>&2; \
			exit 1; \
		fi; \
		echo 'uv installed. If `uv` is not yet on PATH in this shell, open a new shell (or `source $$HOME/.local/bin/env`) and re-run.'; \
	}
	@command -v uv > /dev/null 2>&1 || { echo 'Error: uv still not on PATH; open a new shell and re-run `make install-deps`.' 1>&2; exit 1; }
	uv python install $(HOOK_PYTHON) 3.12
	uv tool install --quiet pre-commit
	uv tool install --quiet rust-just
	uv sync
	@echo "All dependencies installed (uv-managed). Run 'make setup' to wire up git hooks."

setup: install-deps ## Install dependencies and configure the repo to use the shared git hooks
	git config --local include.path ../.gitconfig

# ---------------------------------------------------------------------------
# CI ENTRYPOINT
# ---------------------------------------------------------------------------
# `make ci` runs the checks (lint + qa). It assumes the uv-managed
# dependencies are already provisioned; it does NOT depend on `install-deps`,
# so a developer with deps in place can re-run checks without re-provisioning.
# CI provisions first: both the GitHub Actions workflow
# (.github/workflows/ci.yml) and the Azure DevOps pipeline
# (azure-pipelines.yml) do nothing more than check out the code, install uv,
# run `make install-deps`, then `make ci`. All actual check logic lives here,
# in-repo, so it runs identically on a laptop and on every CI platform.
ci: lint qa ## Run the full CI check suite (assumes deps installed; CI runs `make install-deps` first)

lint: check-uv ## Run all pre-commit hooks against all files (same hooks as the git hooks)
	@PATH="$(PRECOMMIT_PATH)" $(PRECOMMIT) run --all-files --show-diff-on-failure

# ---------------------------------------------------------------------------
# PYTHON CHECKS
# ---------------------------------------------------------------------------
# `make qa` mirrors `just qa` so the portable CI entrypoint runs the Python
# checks (format check, lint, type check, tests) without requiring `just` on
# the runner. The day-to-day developer command is `just qa` (which also
# auto-fixes); CI runs `ruff format --check` / `ruff check` (no --fix) so it
# fails on unformatted or lint-dirty code instead of silently rewriting it.
qa: check-uv ## Run the Python checks (ruff format check, ruff lint, ty, pytest)
	uv run ruff format --check .
	uv run ruff check .
	uv run ty check --output-format=concise .
	uv run pytest

test-real-backend: check-uv ## Run the opt-in real OpenCode backend integration test
	@if [ -f .env ]; then set -a; . ./.env; set +a; fi; \
	LAMPLIGHTER_OPENCODE_USE_REAL_BACKEND=1 \
	LAMPLIGHTER_OPENCODE_CONFIG_MODE=project-only \
	uv run pytest -m integration tests/test_real_backend_e2e.py

test-chat-through-harness: check-uv ## Run Python + ASP.NET + React checks for the chat-through-harness POC
	./scripts/test-chat-through-harness.sh

run-chat-through-harness: ## Start the chat POC in three tmux panes and open the portal
	./scripts/run-chat-through-harness.sh

print-real-backend-fingerprint: check-uv ## Print non-secret fingerprints of real backend env values
	@if [ -f .env ]; then set -a; . ./.env; set +a; fi; \
	python3 -c 'import hashlib, os; [print("%s: len=%s sha256[:12]=%s" % (name, len(value), hashlib.sha256(value.encode()).hexdigest()[:12] if value else "missing")) for name in ("AZURE_OPENAI_ENDPOINT", "AZURE_OPENAI_DEPLOYMENT", "AZURE_OPENAI_API_KEY") for value in [os.environ.get(name, "")]]'

check-uv: ## Verify uv is installed (the only machine-level prerequisite)
	@command -v uv > /dev/null || { \
		echo 'Error: `uv` not found. Run `make install-deps` (it bootstraps uv) or see https://docs.astral.sh/uv/.' 1>&2; \
		exit 1; \
	}
