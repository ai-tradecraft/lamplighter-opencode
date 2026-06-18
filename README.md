# git-template

My baseline git template. It ships a shared git configuration and a set of
[pre-commit](https://pre-commit.com) hooks so every repo started from this
template gets consistent commit hygiene and commit-message formatting out of
the box.

## Requirements

- [`pre-commit`](https://pre-commit.com) installed and on your `PATH`:
  ```sh
  pipx install pre-commit   # or: brew install pre-commit
  ```
- A **Python 3.10** interpreter installed on your system (required by the
  [commitizen](https://commitizen-tools.github.io/commitizen/) and
  [sync-pre-commit-deps](https://github.com/pre-commit/sync-pre-commit-deps)
  hooks). Install instructions per platform:

  | Platform | Install command |
  | --- | --- |
  | macOS (Homebrew) | `brew install python@3.10` |
  | Linux (Debian/Ubuntu) | `sudo apt install python3.10` |
  | Linux (any, via pyenv) | `pyenv install 3.10` |
  | Windows | Download from [python.org](https://www.python.org/downloads/) (the installer registers it with the `py` launcher as `py -3.10`). |

  > **Why exactly 3.10?** Some hooks require Python `>=3.10`, but pre-commit
  > otherwise uses each hook's own default interpreter (`python3` in their
  > manifests) — which on many systems (notably macOS) is an older 3.9 that
  > fails to build the hook environments with `requires a different Python`. The
  > template therefore sets `language_version: python3.10` explicitly on the
  > affected hooks in [`.pre-commit-config.yaml`](.pre-commit-config.yaml).
  > (A top-level `default_language_version` would *not* work here: pre-commit
  > only applies it to hooks that don't already declare their own
  > `language_version`, and these hooks do.) `python3.10` is a version request
  > that pre-commit resolves to a real interpreter on Windows, Linux, and macOS
  > (on Windows through the `py` launcher). A Python **3.10** interpreter
  > therefore needs to be installed, though it does not need to be your default
  > `python3`. To standardize on a newer version, change the `language_version`
  > values in [`.pre-commit-config.yaml`](.pre-commit-config.yaml) (see
  > [Troubleshooting](#troubleshooting)).

## Setup

From the repository root:

```sh
make setup
```

This runs:

```sh
git config --local include.path ../.gitconfig
```

which makes the repo's local config include the committed [`.gitconfig`](.gitconfig).
That config sets `core.hooksPath = .githooks/`, activating the committed hook
scripts. Because the hooks live in `.githooks/` and are wired up through
`include.path`, **`pre-commit install` is not required**.

Run `make help` (or just `make`) to list the available targets.

## What gets configured

| File | Purpose |
| --- | --- |
| [`.gitconfig`](.gitconfig) | Sets `core.hooksPath = .githooks/` so the committed hooks are used. |
| [`.githooks/pre-commit`](.githooks/pre-commit) | Runs the `pre-commit`-stage hooks (formatting, secret detection, etc.). |
| [`.githooks/commit-msg`](.githooks/commit-msg) | Runs commitizen to enforce [Conventional Commits](https://www.conventionalcommits.org/) message format. |
| [`.pre-commit-config.yaml`](.pre-commit-config.yaml) | Declares the hook repos and versions, and pins the Python interpreter (`language_version: python3.10`) on the Python hooks that require it. |

### Hooks included

- **pre-commit-hooks**: trailing whitespace, end-of-file fixer, YAML checks,
  large-file guard (blocks files over 500 kB by default), case-conflict
  detection (catches filename collisions on case-insensitive filesystems like
  macOS/Windows), illegal Windows names, merge-conflict markers, private-key
  detection, byte-order-marker fix, and mixed line endings.
- **gitleaks**: scans for hardcoded secrets.
- **commitizen** (`commit-msg` stage): validates commit messages follow the
  Conventional Commits format, e.g.:
  ```
  feat: add user login
  fix(api): handle null response
  chore: bump dependencies
  ```
- **sync-pre-commit-deps**: keeps hook dependency versions in sync.

## CI/CD integration

This template is designed so that wiring it into **any** CI/CD platform is
simple and deterministic. It follows the industry-standard *thin wrapper*
pattern (see [Martin Fowler on Continuous
Integration](https://martinfowler.com/articles/continuousIntegration.html#AutomateTheBuild)):
all the actual check logic lives **in the repository** behind a single command,
and each CI platform's config does nothing more than check out the code,
install prerequisites, and run that one command.

```
make ci                       ← single source of truth (runs locally too)
  └── pre-commit run --all-files
        └── hooks in .pre-commit-config.yaml

.github/workflows/ci.yml      ← thin stub: checkout → setup → `make ci`
azure-pipelines.yml           ← thin stub: checkout → setup → `make ci`
```

The same `make ci` a developer runs on their laptop is exactly what runs on
GitHub Actions and Azure DevOps. To change *what* CI does, edit the
[`Makefile`](Makefile) and [`.pre-commit-config.yaml`](.pre-commit-config.yaml)
— **not** the platform YAML.

| File | Purpose |
| --- | --- |
| [`Makefile`](Makefile) (`make ci`) | The portable entrypoint. All check logic lives here. Extend it with your project's build/test commands. |
| [`.github/workflows/ci.yml`](.github/workflows/ci.yml) | Thin GitHub Actions stub that runs `make ci`. |
| [`azure-pipelines.yml`](azure-pipelines.yml) | Thin Azure DevOps stub that runs `make ci`. |

### Extending `make ci` for your project

Add your build/test steps to the `ci` target in the [`Makefile`](Makefile). For
example, for a .NET project:

```make
ci: check-pre-commit lint test ## Run the full CI check suite

test: ## Run the test suite
	dotnet test
```

Because the logic is in the Makefile, those steps run identically locally and
on every CI platform — no YAML changes required.

### What you still configure per platform (and why)

The thin-wrapper pattern minimizes platform-specific config but cannot
eliminate it. Each platform requires its own small YAML stub, and a few
concerns are inherently platform-specific and **cannot** be pushed into a
portable script:

- **The stub file itself** — GitHub needs `.github/workflows/*.yml`; Azure
  DevOps needs `azure-pipelines.yml`. The template ships both, pre-wired to
  `make ci`.
- **Triggers** (which branches/events run CI) — expressed differently on each
  platform. Both stubs ship pre-configured to run CI on:
  - **pushes** to `main` and `develop`, and
  - **pull requests** targeting `develop`.

  Adjust the `on`/`trigger`/`pr` sections in
  [`.github/workflows/ci.yml`](.github/workflows/ci.yml) and
  [`azure-pipelines.yml`](azure-pipelines.yml) to change this.
- **Secrets, service connections, OIDC, and permissions** — managed in each
  platform's settings/YAML, never in the repo.
- **Runner/agent image** and **prerequisite installation** (Python,
  `pre-commit`).

Everything else — the actual checks — is shared via `make ci`.

### Determinism

- Hook versions are pinned via `rev` in
  [`.pre-commit-config.yaml`](.pre-commit-config.yaml).
- The Python interpreter is pinned to **3.10** in both CI stubs (matching the
  per-hook `language_version`; see [Requirements](#requirements)).
- Both stubs cache pre-commit hook environments keyed on the config file, so
  unchanged hooks are not rebuilt.

Bump these versions deliberately when you want to upgrade.

## Make targets

| Target | Description |
| --- | --- |
| `make help` | Show available targets (default when running `make`). |
| `make setup` | Configure the repo to use the shared git config and pre-commit hooks. Runs the preflight checks first. |
| `make ci` | Run the full CI check suite — the single command CI/CD pipelines invoke. Runs identically locally. |
| `make lint` | Run all pre-commit hooks against all files. |
| `make check-pre-commit` | Verify the `pre-commit` tool is installed. |
| `make check-python` | Verify a Python 3.10 interpreter is available for the hooks. |

## Troubleshooting

- **`` `pre-commit` not found ``** — install the tool (see [Requirements](#requirements)).
- **commitizen / sync-pre-commit-deps fails to build / `requires a different Python`** —
  a Python 3.10 interpreter could not be found. Install it (see
  [Requirements](#requirements)) and re-run `make check-python` to confirm it is
  detected. The interpreter is pinned per-hook via `language_version: python3.10`
  in [`.pre-commit-config.yaml`](.pre-commit-config.yaml); to standardize on a
  different version, change those `language_version` values (e.g. to
  `python3.11`) and make sure that interpreter is installed.
- **Hook is ignored / not running** — confirm `make setup` has been run
  (`git config --get include.path` should print `../.gitconfig`) and that the
  hook scripts in `.githooks/` are executable.
