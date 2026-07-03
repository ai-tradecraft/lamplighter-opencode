# Lamplighter for OpenCode

A Python CLI for Lamplighter for OpenCode. Running it prints:

```
Welcome to Lamplighter for OpenCode
```

The project layout and Python tooling follow the
[cookiecutter-pypackage](https://github.com/audreyfeldroy/cookiecutter-pypackage)
template (uv + just + ruff + ty + pytest, `src/` layout, Typer CLI). It is
layered on top of a pre-existing git-template baseline (shared git hooks and a
portable `make ci` CI entrypoint), which has been preserved and reconciled with
the pypackage approach — see [Tooling reconciliation](#tooling-reconciliation).

## Requirements

The **only** machine-level prerequisite is [`uv`](https://docs.astral.sh/uv/)
(plus `make` and `git`, which you already have). Everything else — the
project's Python (3.12+), the Python 3.10 the git hooks need, `pre-commit`, and
`just` — is provisioned by `uv` into its own isolated caches and tool
environments. **No system packages are mutated.**

You don't even have to install `uv` yourself: `make install-deps` will
bootstrap it (via uv's self-contained installer into `~/.local/bin`) if it's
missing, then install everything else. If you'd rather install `uv` up front:

```sh
brew install uv   # or: curl -LsSf https://astral.sh/uv/install.sh | sh
```

Run `make doctor` at any time to see what's present or missing (it makes no
changes).

## Quickstart

```sh
git clone <this-repo>
cd lamplighter-opencode

make install-deps             # uv-provisions Python, pre-commit, just, deps
                              # (bootstraps uv itself if it's missing)
make setup                    # install-deps + wire up the committed git hooks

uv run lamplighter-opencode   # -> Welcome to Lamplighter for OpenCode
```

`make setup` depends on `make install-deps`, so a single `make setup` is enough
for a fresh clone.

## Chat Through Harness POC

The end-to-end ASP.NET + React POC lives in
[`poc/chat-through-harness`](poc/chat-through-harness/README.md). Its README
documents local `.env` usage, production secret injection expectations, and the
OpenCode configuration modes used for local and deterministic runs.

### Running the CLI

After dependencies are installed, the CLI can be run three ways:

```sh
uv run lamplighter-opencode        # via the entry point ([project.scripts])
uv run lamplighter-opencode --help # show help
uv run python -m lamplighter_opencode  # via __main__.py
```

The Lamplighter Controller calls this package through the provider-neutral
adapter command:

```sh
uv run lamplighter-opencode adapter-operation --operation operation.json --json
```

`operation.json` is an `adapter.operation` envelope from
`tradecraft-contracts/contracts/agent-runtime/v1/schemas/runtime-adapter-message.schema.json`.
The command returns an `adapter.operation_result` envelope; OpenCode-specific
work remains an implementation detail behind that boundary.

## Project layout

```
.
├── src/
│   └── lamplighter_opencode/
│       ├── __init__.py
│       ├── __main__.py     # enables `python -m lamplighter_opencode`
│       ├── cli.py          # Typer CLI (prints the welcome message)
│       └── py.typed        # PEP 561 type-annotation marker
├── tests/
│   └── test_lamplighter_opencode.py
├── pyproject.toml          # package metadata + tool config (ruff, ty, pytest, coverage)
├── justfile                # day-to-day task runner (`just qa`, `just test`, ...)
├── .python-version         # default interpreter for uv (3.12)
├── Makefile                # portable CI entrypoint (`make ci`) + git-hook setup
├── .pre-commit-config.yaml # commit-hygiene / secret / commit-message hooks
├── .gitconfig / .githooks/ # committed git hooks (activated via `make setup`)
├── .github/workflows/ci.yml
└── azure-pipelines.yml
```

The project uses a **`src/` layout** so tests import the installed package
rather than accidentally importing local code — a common source of subtle bugs.

## Development workflow

`just` is installed as a uv tool by `make install-deps`. `just qa` is the daily
driver (it auto-fixes). If `just` isn't on your PATH, run it via uv with
`uvx --from rust-just just <cmd>`:

```sh
just qa          # ruff format + ruff lint --fix + ty + pytest
just test        # run the tests
just type-check  # type-check with ty
just --list      # show all available commands
```

The same Python checks also run via `make qa` (CI mode — no auto-fix, fails on
unformatted/lint-dirty code). This is what `make ci` uses, so it never requires
`just`:

```sh
make qa
```

## Tooling reconciliation

This repo started from a language-agnostic **git-template** baseline. The
Python tooling from cookiecutter-pypackage was layered on top, and where the
two overlapped the **pypackage approach was preferred**. Nothing from the
baseline was discarded — the pieces compose:

| Concern | Baseline | pypackage | How it's reconciled |
| --- | --- | --- | --- |
| Task runner | `Makefile` | `justfile` | **Both.** `just qa` is the day-to-day driver; `make ci` is the portable CI entrypoint and delegates the Python checks to `make qa` (a mirror of `just qa`) so CI does not require `just`. |
| Python (project) | n/a | **3.12+** | The project targets 3.12+ via `uv` / `.python-version` (pypackage preferred). |
| Python (hooks) | system 3.10 | n/a | The hooks still need 3.10, but it's now sourced from uv's managed cache (`uv python install 3.10`) and put on `PATH` only while pre-commit runs — no system Python 3.10 required. |
| Lint / format | pre-commit hooks | **ruff + ty** | **Both, by responsibility.** pre-commit handles commit hygiene, secret scanning, and commit-message format; ruff/ty handle the Python source and run inside `make ci` via `make qa`. |
| Tool installation | manual (`pipx`/`brew`) | `uv` | **uv for everything.** `make install-deps` provisions Python, `pre-commit`, and `just` as isolated uv tools/interpreters; `uv` is the only thing installed at the machine level (and it self-bootstraps). |
| CI shape | thin `make ci` wrapper | full GitHub Actions suite | **Thin wrapper kept** (the baseline's deliberate, platform-portable pattern), extended so `make ci` also runs the Python checks. The CI stubs install only `uv`, then `make install-deps` + `make ci`. |

### CI/CD integration (thin-wrapper pattern)

All check logic lives **in the repository** behind a single command, and each
CI platform's config does nothing more than check out the code, install `uv`,
and run `make install-deps` + `make ci` (see
[Martin Fowler on Continuous Integration](https://martinfowler.com/articles/continuousIntegration.html#AutomateTheBuild)).

```
make ci                          ← single source of truth (runs locally too)
  ├── lint  → uvx pre-commit run --all-files   ← hooks in .pre-commit-config.yaml
  │          (uv-managed Python 3.10 on PATH for the 3.10 hooks)
  └── qa    → uv run ruff/ty/pytest            ← mirrors `just qa`

.github/workflows/ci.yml         ← thin stub: checkout → install uv → make install-deps → make ci
azure-pipelines.yml              ← thin stub: checkout → install uv → make install-deps → make ci
```

The same `make ci` a developer runs locally is exactly what runs on GitHub
Actions and Azure DevOps. To change *what* CI does, edit the
[`Makefile`](Makefile), [`justfile`](justfile), and
[`.pre-commit-config.yaml`](.pre-commit-config.yaml) — **not** the platform YAML.

Triggers ship pre-configured to run CI on pushes to `main`/`develop` and PRs
targeting `develop`; adjust the `on`/`trigger`/`pr` sections in the stubs to
change this.

## Git hooks

`make setup` runs `git config --local include.path ../.gitconfig`, which makes
the repo's local config include the committed [`.gitconfig`](.gitconfig). That
sets `core.hooksPath = .githooks/`, activating the committed hook scripts.
Because the hooks live in `.githooks/` and are wired up through `include.path`,
**`pre-commit install` is not required**.

Hooks included:

- **pre-commit-hooks**: trailing whitespace, end-of-file fixer, YAML checks,
  large-file guard, case-conflict detection, illegal Windows names,
  merge-conflict markers, private-key detection, byte-order-marker fix, and
  mixed line endings.
- **gitleaks**: scans for hardcoded secrets.
- **commitizen** (`commit-msg` stage): enforces
  [Conventional Commits](https://www.conventionalcommits.org/), e.g.
  `feat: add user login`, `fix(cli): handle empty input`.
- **sync-pre-commit-deps**: keeps hook dependency versions in sync.

### Why 3.10 for the hooks?

Some hooks (commitizen, sync-pre-commit-deps) require Python `>=3.10`, but
pre-commit otherwise uses each hook's own default interpreter (`python3`) —
which on many systems (notably macOS) is an older 3.9 that fails with
`requires a different Python`. The hooks therefore pin `language_version:
python3.10` in [`.pre-commit-config.yaml`](.pre-commit-config.yaml).

You do **not** need a system Python 3.10. `make install-deps` runs
`uv python install 3.10`, and `make lint` / `make ci` prepend that uv-managed
interpreter's directory to `PATH` only while pre-commit runs, so the `python3.10`
hooks resolve to uv's copy. To standardize on a newer version, change those
`language_version` values (and the `HOOK_PYTHON` variable in the
[`Makefile`](Makefile)).

## Make targets

| Target | Description |
| --- | --- |
| `make help` | Show available targets (default when running `make`). |
| `make doctor` | Report which dependencies are present or missing. Makes no changes. |
| `make install-deps` | Install everything via `uv` (Python interpreters, `pre-commit`, `just`, project deps). Bootstraps `uv` itself if missing. |
| `make setup` | `install-deps` + configure the repo to use the shared git config and hooks. |
| `make ci` | Run the full CI check suite (pre-commit hooks + Python checks). Runs identically locally. |
| `make qa` | Run the Python checks (ruff format check, ruff lint, ty, pytest). |
| `make lint` | Run all pre-commit hooks against all files. |
| `make check-uv` | Verify `uv` is installed (the only machine-level prerequisite). |

## Troubleshooting

- **`` `uv` not found ``** — run `make install-deps` (it bootstraps `uv`), or
  install it yourself (see [Requirements](#requirements)). If `make install-deps`
  just installed it but it isn't on PATH yet, open a new shell (or
  `source $HOME/.local/bin/env`) and re-run.
- **Want to see what's missing?** — run `make doctor` (read-only).
- **commitizen / sync-pre-commit-deps fails / `requires a different Python`** —
  the uv-managed Python 3.10 wasn't found. Run `make install-deps` to provision
  it (`uv python install 3.10`) and confirm with `make doctor`.
- **Hook is ignored / not running** — confirm `make setup` has been run
  (`git config --get include.path` should print `../.gitconfig`) and that the
  hook scripts in `.githooks/` are executable.
