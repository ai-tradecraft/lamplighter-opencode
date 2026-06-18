# Justfile for lamplighter-opencode

# Show available commands
list:
    @just --list

alias c := clean
alias t := test
alias tc := type-check

# Type check the project with ty
type-check:
    uv run ty check .

# Type check with concise output (one diagnostic per line)
type-check-concise:
    uv run ty check --output-format=concise .

# Type check in watch mode (rechecks on file changes)
type-check-watch:
    uv run ty check --watch .

# Run all the formatting, linting, and testing commands
qa:
    uv run ruff format .
    uv run ruff check . --fix
    uv run ruff check --select I --fix .
    uv run ty check --output-format=concise .
    uv run pytest

# Run all the tests for all the supported Python versions
testall:
    uv run --python=3.12 pytest
    uv run --python=3.13 pytest
    uv run --python=3.14 pytest

# Run all the tests, but allow for arguments to be passed
test *ARGS:
    @echo "Running with arg: {{ ARGS }}"
    uv run pytest {{ ARGS }}

# Run all the tests, but on failure, drop into the debugger
pdb *ARGS:
    @echo "Running with arg: {{ ARGS }}"
    uv run pytest --pdb --maxfail=10 {{ ARGS }}

# Run tests with coverage across all supported Python versions
coverage:
    uv run --python=3.12 coverage run -m pytest
    uv run --python=3.13 coverage run -m pytest
    uv run --python=3.14 coverage run -m pytest
    uv run coverage combine
    uv run coverage report
    uv run coverage html

# Build the project, useful for checking that packaging is correct
build:
    rm -rf build
    rm -rf dist
    uv build

# Remove all build, test, coverage and Python artifacts
clean: clean-build clean-pyc clean-test

# Remove build artifacts
clean-build:
	rm -fr build/
	rm -fr dist/
	rm -fr .eggs/
	find . -name '*.egg-info' -exec rm -fr {} +
	find . -name '*.egg' -exec rm -f {} +

# Remove Python file artifacts
clean-pyc:
	find . -name '*.pyc' -exec rm -f {} +
	find . -name '*.pyo' -exec rm -f {} +
	find . -name '*~' -exec rm -f {} +
	find . -name '__pycache__' -exec rm -fr {} +

# Remove test and coverage artifacts
clean-test:
	rm -f .coverage
	rm -f .coverage.*
	rm -fr htmlcov/
	rm -fr .pytest_cache

# Publish to PyPI (manual alternative to GitHub Actions)
publish:
    uv build
    uv publish
