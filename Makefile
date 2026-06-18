# Makefile for git-template
#
# Provides one-command setup for this repository's shared git configuration
# and pre-commit hooks.
#
# Usage:
#   make            # same as `make help`
#   make help       # list available targets
#   make setup      # configure the repo to use shared git config + hooks
#   make ci         # run the full CI check suite (the command pipelines invoke)
#   make lint       # run all pre-commit hooks against all files
#
# Requirements:
#   - The `pre-commit` tool must be installed and on your PATH.
#     Install with: `pipx install pre-commit` or `brew install pre-commit`
#   - A Python 3.10 interpreter must be installed (required by commitizen and
#     sync-pre-commit-deps). It need not be your default `python3`; it just
#     needs to be discoverable as `python3.10` (Unix) or `py -3.10` (Windows).
#
# How it works:
#   `make setup` runs `git config --local include.path ../.gitconfig`, which
#   makes this repo's local config include the committed `.gitconfig`. That in
#   turn sets `core.hooksPath = .githooks/`, activating the committed hook
#   scripts (pre-commit and commit-msg) without needing `pre-commit install`.

.DEFAULT_GOAL := help

.PHONY: setup ci lint check-pre-commit check-python help

help: ## Show available targets
	@grep -E '^[a-zA-Z_-]+:.*?## .*$$' $(MAKEFILE_LIST) | awk 'BEGIN {FS = ":.*?## "}; {printf "  \033[36m%-18s\033[0m %s\n", $$1, $$2}'

setup: check-pre-commit check-python ## Configure the repo to use the shared git config and pre-commit hooks
	git config --local include.path ../.gitconfig

# ---------------------------------------------------------------------------
# CI ENTRYPOINT
# ---------------------------------------------------------------------------
# `make ci` is the SINGLE command CI/CD pipelines invoke. Both the GitHub
# Actions workflow (.github/workflows/ci.yml) and the Azure DevOps pipeline
# (azure-pipelines.yml) do nothing more than: check out the code, install the
# prerequisites, and run `make ci`. All actual logic lives here, in-repo, so
# it runs identically on a developer laptop and on every CI platform.
#
# Projects built from this template extend `ci` by adding their own build/test
# steps (e.g. `dotnet test`, `npm test`) as dependencies or extra recipe lines.
ci: check-pre-commit lint ## Run the full CI check suite (what pipelines invoke)

lint: check-pre-commit ## Run all pre-commit hooks against all files (same hooks as the git hooks)
	pre-commit run --all-files --show-diff-on-failure

check-pre-commit: ## Verify the pre-commit tool is installed
	@command -v pre-commit > /dev/null || { \
		echo 'Error: `pre-commit` not found. Install it with `pipx install pre-commit` or `brew install pre-commit`.' 1>&2; \
		exit 1; \
	}

check-python: ## Verify a Python 3.10 interpreter is available for the hooks
	@if command -v python3.10 > /dev/null 2>&1; then \
		exit 0; \
	elif command -v py > /dev/null 2>&1 && py -3.10 --version > /dev/null 2>&1; then \
		exit 0; \
	else \
		echo 'Error: a Python 3.10 interpreter is required by the pre-commit hooks (commitizen, sync-pre-commit-deps).' 1>&2; \
		echo '       It need not be your default python3, but must be installed as `python3.10` (Unix) or `py -3.10` (Windows).' 1>&2; \
		echo '       macOS:        brew install python@3.10' 1>&2; \
		echo '       Debian/Ubuntu: sudo apt install python3.10' 1>&2; \
		echo '       pyenv:        pyenv install 3.10' 1>&2; \
		echo '       Windows:      install from https://www.python.org/downloads/' 1>&2; \
		exit 1; \
	fi
