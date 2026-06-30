"""Tests for `lamplighter_opencode` package."""

import io
import json
import urllib.error
import urllib.parse
import urllib.request
from email.message import Message

from typer.testing import CliRunner

import lamplighter_opencode
from lamplighter_opencode.cli import app
from lamplighter_opencode.contracts.models import AgentChatHistory, AgentChatMessage, AgentSessionSpec, AgentTurnRequest
from lamplighter_opencode.runtime.workspace import (
    azure_openai_base_url,
    azure_openai_resource_name,
    azure_openai_responses_url,
    get_agent_session_history,
    isolated_opencode_environment,
    materialize_opencode_config,
    normalize_opencode_event,
    opencode_config_mode,
    opencode_environment,
    opencode_run_command,
    opencode_serve_command,
    stream_opencode_events,
    submit_turn,
)

runner = CliRunner()


def test_import() -> None:
    """Verify the package can be imported."""
    assert lamplighter_opencode


def test_cli_prints_welcome() -> None:
    """The CLI prints the Lamplighter welcome message."""
    result = runner.invoke(app)
    assert result.exit_code == 0
    assert "Welcome to Lamplighter for OpenCode" in result.stdout


def test_agent_session_spec_validation() -> None:
    """Session specs validate required launch fields."""
    spec = AgentSessionSpec.from_dict(
        {
            "agent_session_id": "session_1",
            "goal_run_id": "goal_1",
            "phase_run_id": "phase_1",
            "agent_definition_id": "opencode.default",
            "workspace_ref": "/tmp/session_1/workspace",
            "repo_ref": "lamplighter-opencode",
            "branch_name": "poc-chat-through-harness",
        }
    )

    assert spec.agent_session_id == "session_1"


def test_agent_turn_request_requires_prompt_response() -> None:
    """V1 only accepts prompt_response turns."""
    request = AgentTurnRequest.from_dict(
        {
            "id": "turn_1",
            "agent_session_id": "session_1",
            "type": "prompt_response",
            "instruction": "hello",
        }
    )

    assert request.instruction == "hello"


def test_prepare_session_command(tmp_path) -> None:
    """prepare-session writes the session workspace."""
    spec_path = tmp_path / "spec.json"
    workspace = tmp_path / ".agent-runtime" / "sessions" / "session_1" / "workspace"
    spec_path.write_text(
        json.dumps(
            {
                "agent_session_id": "session_1",
                "goal_run_id": "goal_1",
                "phase_run_id": "phase_1",
                "agent_definition_id": "opencode.default",
                "workspace_ref": str(workspace),
                "repo_ref": "lamplighter-opencode",
                "branch_name": "poc-chat-through-harness",
            }
        ),
        encoding="utf-8",
    )

    result = runner.invoke(app, ["prepare-session", "--spec", str(spec_path), "--json"])

    assert result.exit_code == 0
    assert workspace.exists()
    assert (workspace.parent / "session.json").exists()
    assert not (workspace / "opencode.json").exists()


def test_prepare_session_project_only_writes_opencode_config(tmp_path, monkeypatch) -> None:
    """project-only prepare-session writes the harness-owned OpenCode config."""
    monkeypatch.setenv("LAMPLIGHTER_OPENCODE_CONFIG_MODE", "project-only")
    spec_path = tmp_path / "spec.json"
    workspace = tmp_path / ".agent-runtime" / "sessions" / "session_1" / "workspace"
    spec_path.write_text(
        json.dumps(
            {
                "agent_session_id": "session_1",
                "goal_run_id": "goal_1",
                "phase_run_id": "phase_1",
                "agent_definition_id": "opencode.default",
                "workspace_ref": str(workspace),
                "repo_ref": "lamplighter-opencode",
                "branch_name": "poc-chat-through-harness",
            }
        ),
        encoding="utf-8",
    )

    result = runner.invoke(app, ["prepare-session", "--spec", str(spec_path), "--json"])

    assert result.exit_code == 0
    assert (workspace / "opencode.json").exists()


def test_submit_turn_command_returns_fake_result(tmp_path) -> None:
    """submit-turn returns a normalized fake result by default."""
    request_path = tmp_path / "turn.json"
    request_path.write_text(
        json.dumps(
            {
                "id": "turn_1",
                "agent_session_id": "session_1",
                "type": "prompt_response",
                "instruction": "hello",
            }
        ),
        encoding="utf-8",
    )

    result = runner.invoke(app, ["submit-turn", "--session", "session_1", "--request", str(request_path), "--json"])

    assert result.exit_code == 0
    assert "Fake OpenCode response" in result.stdout


def test_get_agent_session_history_normalizes_opencode_messages(tmp_path, monkeypatch) -> None:
    """OpenCode history becomes a stable, user-visible conversation snapshot."""
    controller_workspace = tmp_path / "controller"
    agent_root = controller_workspace / "agents" / "agent_1"
    runtime = agent_root / "runtime"
    workspace = agent_root / "workspace"
    session_root = runtime / "sessions" / "session_1"
    workspace.mkdir(parents=True)
    session_root.mkdir(parents=True)
    (runtime / "opencode-server.json").write_text(
        json.dumps(
            {
                "status": "ready",
                "endpoint": "http://127.0.0.1:4097",
                "auth": {"username": "opencode", "password": "test-password"},
            }
        ),
        encoding="utf-8",
    )
    (session_root / "session.json").write_text(
        json.dumps({"opencode_session_id": "oc_session_1"}),
        encoding="utf-8",
    )

    def fake_urlopen(http_request, timeout=60):  # noqa: ANN001, ANN202, ARG001
        assert http_request.method == "GET"
        assert http_request.headers["Authorization"].startswith("Basic ")
        return _JsonResponse(
            [
                {
                    "info": {
                        "id": "message_user",
                        "role": "user",
                        "time": {"created": 1_700_000_000_000},
                    },
                    "parts": [{"type": "text", "text": "Hello"}],
                },
                {
                    "info": {
                        "id": "message_assistant",
                        "role": "assistant",
                        "time": {
                            "created": 1_700_000_000_100,
                            "completed": 1_700_000_000_200,
                        },
                    },
                    "parts": [
                        {"type": "step-start"},
                        {"type": "text", "text": "Hi"},
                        {"type": "text", "text": "there"},
                    ],
                },
            ]
        )

    monkeypatch.setattr(urllib.request, "urlopen", fake_urlopen)

    history = get_agent_session_history(controller_workspace, "agent_1", "session_1")

    assert history.opencode_session_id == "oc_session_1"
    assert [message.source_message_id for message in history.messages] == ["message_user", "message_assistant"]
    assert history.messages[1].text == "Hi\nthere"
    assert len(history.raw_messages) == 2


def test_get_session_history_command_emits_valid_json(tmp_path, monkeypatch) -> None:
    """The runner-facing CLI command emits the normalized history contract."""
    history = AgentChatHistory(
        agent_id="agent_1",
        agent_session_id="session_1",
        opencode_session_id="oc_session_1",
        observed_at="2026-06-30T00:00:00+00:00",
        messages=[
            AgentChatMessage(
                source_message_id="message_1",
                role="user",
                text="Hello",
                created_at="2026-06-30T00:00:00+00:00",
            )
        ],
    )
    monkeypatch.setattr("lamplighter_opencode.cli.get_agent_session_history", lambda *args: history)

    result = runner.invoke(
        app,
        [
            "get-session-history",
            "--agent",
            "agent_1",
            "--session",
            "session_1",
            "--controller-workspace",
            str(tmp_path),
            "--json",
        ],
    )

    assert result.exit_code == 0
    assert json.loads(result.stdout)["messages"][0]["source_message_id"] == "message_1"


def test_start_session_command_writes_opencode_server_metadata(tmp_path, monkeypatch) -> None:
    """start-session dry-run writes endpoint metadata without launching OpenCode."""
    monkeypatch.delenv("CHAT_THROUGH_HARNESS_LOG_ROOT", raising=False)
    session_root = tmp_path / ".agent-runtime" / "sessions" / "session_1"
    (session_root / "workspace").mkdir(parents=True)

    result = runner.invoke(
        app,
        [
            "start-session",
            "--session",
            "session_1",
            "--runtime-root",
            str(tmp_path / ".agent-runtime"),
            "--host",
            "127.0.0.1",
            "--port",
            "4097",
            "--dry-run",
            "--json",
        ],
    )

    assert result.exit_code == 0
    payload = json.loads(result.stdout)
    assert payload["status"] == "planned"
    assert payload["endpoint"] == "http://127.0.0.1:4097"
    assert payload["auth"]["username"] == "opencode"
    assert payload["auth"]["password"]
    assert payload["stdout_log"] == str(tmp_path / ".agent-runtime" / "logs" / "opencode" / "session_1.stdout.log")
    assert payload["stderr_log"] == str(tmp_path / ".agent-runtime" / "logs" / "opencode" / "session_1.stderr.log")
    assert (session_root / "opencode-server.json").exists()


def test_project_only_serve_command_uses_pure_mode(tmp_path, monkeypatch) -> None:
    """project-only server launch prevents user-global plugin loading."""
    monkeypatch.setenv("LAMPLIGHTER_OPENCODE_CONFIG_MODE", "project-only")
    command = opencode_serve_command(tmp_path, "127.0.0.1", 4097)

    assert command[:2] == ["opencode", "serve"]
    assert "--pure" in command


def test_opencode_environment_inherits_global_config_by_default(tmp_path, monkeypatch) -> None:
    """Local developer execution preserves the user's global OpenCode config."""
    root = tmp_path / "session_1"
    (root / "workspace").mkdir(parents=True)
    monkeypatch.setenv("HOME", "/Users/local-dev")
    monkeypatch.delenv("LAMPLIGHTER_OPENCODE_CONFIG_MODE", raising=False)
    monkeypatch.delenv("AZURE_OPENAI_DEPLOYMENT", raising=False)

    command, observed = opencode_run_command(root, "hello")
    env = opencode_environment(root)

    assert opencode_config_mode() == "inherit-global"
    assert "--pure" not in command
    assert "--model" not in command
    assert observed == "opencode run --dir <workspace>"
    assert env["HOME"] == "/Users/local-dev"
    assert "OPENCODE_CONFIG" not in env
    assert env["OPENCODE_DISABLE_AUTOUPDATE"] == "1"


def test_project_only_opencode_environment_is_session_local(tmp_path, monkeypatch) -> None:
    """Project-only execution does not inherit the user's global OpenCode home."""
    root = tmp_path / "session_1"
    (root / "workspace").mkdir(parents=True)
    monkeypatch.setenv("LAMPLIGHTER_OPENCODE_CONFIG_MODE", "project-only")
    monkeypatch.setenv("AZURE_OPENAI_DEPLOYMENT", "deployment")
    monkeypatch.setenv("AZURE_OPENAI_API_KEY", "test-api-key")
    monkeypatch.setenv("AZURE_OPENAI_ENDPOINT", "https://example-resource.openai.azure.com/")

    config_path = materialize_opencode_config(root)
    command, observed = opencode_run_command(root, "hello")
    env = opencode_environment(root)

    assert config_path == root / "workspace" / "opencode.json"
    assert "--pure" in command
    assert "--model" in command
    assert observed == "opencode run --pure --dir <workspace> --model azure/deployment"
    assert env["HOME"] == str(root / "home")
    assert env["XDG_CONFIG_HOME"] == str(root / "xdg-config")
    assert env["XDG_DATA_HOME"] == str(root / "xdg-data")
    assert env["XDG_CACHE_HOME"] == str(root / "xdg-cache")
    assert env["OPENCODE_CONFIG"] == str(config_path)
    assert env["OPENCODE_CONFIG_DIR"] == str(root / "opencode-config")
    assert isolated_opencode_environment(root)["HOME"] == str(root / "home")


def test_submit_turn_uses_ready_opencode_server_metadata(tmp_path, monkeypatch) -> None:
    """Real backend submission uses opencode serve HTTP instead of opencode run."""
    root = tmp_path / "session_1"
    workspace = root / "workspace"
    workspace.mkdir(parents=True)
    (root / "opencode-server.json").write_text(
        json.dumps(
            {
                "status": "ready",
                "endpoint": "http://127.0.0.1:4097",
                "workspace": str(workspace),
                "auth": {"username": "opencode", "password": "test-password"},
            }
        ),
        encoding="utf-8",
    )
    request = AgentTurnRequest(
        id="turn_1",
        agent_session_id="session_1",
        type="prompt_response",
        instruction="hello",
    )
    observed_urls = []

    def fake_urlopen(http_request, timeout=60):  # noqa: ANN001, ANN202
        observed_urls.append(http_request.full_url)
        body = json.loads(http_request.data.decode("utf-8"))
        assert http_request.headers["Authorization"].startswith("Basic ")
        if http_request.full_url.startswith("http://127.0.0.1:4097/session?"):
            assert body["title"] == "Lamplighter session_1"
            return _JsonResponse({"id": "oc_session_1"})
        if http_request.full_url.startswith("http://127.0.0.1:4097/session/oc_session_1/message?"):
            assert body["parts"] == [{"type": "text", "text": "hello"}]
            return _JsonResponse({"info": {"id": "message_1"}, "parts": [{"type": "text", "text": "server reply"}]})
        raise AssertionError(f"Unexpected URL {http_request.full_url}")

    monkeypatch.setenv("LAMPLIGHTER_OPENCODE_USE_REAL_BACKEND", "1")
    monkeypatch.setattr(urllib.request, "urlopen", fake_urlopen)

    result = submit_turn(request, root)

    assert result.status == "completed"
    assert result.message == "server reply"
    assert result.commands_observed == ["POST /session", "POST /session/oc_session_1/message"]
    encoded_workspace = urllib.parse.quote(str(workspace), safe="")
    assert observed_urls == [
        f"http://127.0.0.1:4097/session?directory={encoded_workspace}",
        f"http://127.0.0.1:4097/session/oc_session_1/message?directory={encoded_workspace}",
    ]
    metadata = json.loads((root / "opencode-server.json").read_text(encoding="utf-8"))
    assert metadata["opencode_session_id"] == "oc_session_1"


def test_normalize_opencode_events_cover_core_runtime_event_types() -> None:
    """OpenCode events map to Tradecraft runtime event names and compact payloads."""
    samples = [
        (
            {
                "type": "message.part.updated",
                "properties": {
                    "delta": "hello",
                    "part": {
                        "id": "part_1",
                        "sessionID": "oc_session_1",
                        "messageID": "message_1",
                        "type": "text",
                        "text": "hello",
                    },
                },
            },
            "agent_turn.output_delta",
        ),
        (
            {
                "type": "message.part.updated",
                "properties": {
                    "part": {
                        "id": "part_2",
                        "sessionID": "oc_session_1",
                        "messageID": "message_1",
                        "type": "tool",
                        "tool": "bash",
                        "callID": "call_1",
                        "state": {"status": "running", "input": {}, "time": {"start": 1}},
                    },
                },
            },
            "agent_tool.running",
        ),
        (
            {
                "type": "message.part.updated",
                "properties": {
                    "part": {
                        "id": "part_3",
                        "sessionID": "oc_session_1",
                        "messageID": "message_1",
                        "type": "step-finish",
                        "reason": "stop",
                        "cost": 0.01,
                        "tokens": {"input": 1, "output": 2, "reasoning": 0, "cache": {"read": 0, "write": 0}},
                    },
                },
            },
            "agent_step.finished",
        ),
        (
            {"type": "permission.updated", "properties": {"id": "permission_1", "title": "Allow bash?"}},
            "agent_permission.requested",
        ),
        (
            {"type": "session.status", "properties": {"sessionID": "oc_session_1", "status": {"type": "idle"}}},
            "agent_session.idle",
        ),
        (
            {
                "type": "session.error",
                "properties": {"sessionID": "oc_session_1", "error": {"name": "ProviderAuthError"}},
            },
            "agent_turn.failed",
        ),
    ]

    event_types = [normalize_opencode_event(raw, "session_1")[0].event_type for raw, _ in samples]

    assert event_types == [expected for _, expected in samples]


def test_stream_opencode_events_appends_runtime_events(tmp_path, monkeypatch) -> None:
    """stream_opencode_events reads SSE data and appends normalized runtime events."""
    root = tmp_path / ".agent-runtime" / "sessions" / "session_1"
    workspace = root / "workspace"
    workspace.mkdir(parents=True)
    (root / "session.json").write_text(json.dumps({"agent_session_id": "session_1"}), encoding="utf-8")
    (root / "opencode-server.json").write_text(
        json.dumps(
            {
                "status": "ready",
                "endpoint": "http://127.0.0.1:4097",
                "workspace": str(workspace),
                "auth": {"username": "opencode", "password": "test-password"},
            }
        ),
        encoding="utf-8",
    )

    def fake_urlopen(http_request, timeout=60):  # noqa: ANN001, ANN202, ARG001
        assert http_request.full_url.startswith("http://127.0.0.1:4097/event?")
        assert http_request.headers["Accept"] == "text/event-stream"
        return _SseResponse(
            [
                {
                    "type": "message.part.updated",
                    "properties": {
                        "delta": "hello",
                        "part": {
                            "id": "part_1",
                            "sessionID": "oc_session_1",
                            "messageID": "message_1",
                            "type": "text",
                            "text": "hello",
                        },
                    },
                },
                {"type": "session.status", "properties": {"sessionID": "oc_session_1", "status": {"type": "idle"}}},
            ]
        )

    monkeypatch.setattr(urllib.request, "urlopen", fake_urlopen)

    events = stream_opencode_events(root, limit=2)

    assert [event.event_type for event in events] == ["agent_turn.output_delta", "agent_session.idle"]
    written = [
        json.loads(line) for line in (root / "events.jsonl").read_text(encoding="utf-8").splitlines() if line.strip()
    ]
    assert [event["event_type"] for event in written] == ["agent_turn.output_delta", "agent_session.idle"]


def test_stream_events_command_emits_json_summary(tmp_path, monkeypatch) -> None:
    """stream-events CLI command reports appended normalized event count."""
    runtime_root = tmp_path / ".agent-runtime"
    root = runtime_root / "sessions" / "session_1"
    workspace = root / "workspace"
    workspace.mkdir(parents=True)
    (root / "session.json").write_text(json.dumps({"agent_session_id": "session_1"}), encoding="utf-8")
    (root / "opencode-server.json").write_text(
        json.dumps(
            {
                "status": "ready",
                "endpoint": "http://127.0.0.1:4097",
                "workspace": str(workspace),
                "auth": {"username": "opencode", "password": "test-password"},
            }
        ),
        encoding="utf-8",
    )

    def fake_urlopen(http_request, timeout=60):  # noqa: ANN001, ANN202, ARG001
        return _SseResponse([{"type": "session.status", "properties": {"status": {"type": "idle"}}}])

    monkeypatch.setattr(urllib.request, "urlopen", fake_urlopen)

    result = runner.invoke(
        app,
        ["stream-events", "--session", "session_1", "--runtime-root", str(runtime_root), "--limit", "1", "--json"],
    )

    assert result.exit_code == 0
    payload = json.loads(result.stdout)
    assert payload["events_written"] == 1
    assert payload["events"][0]["event_type"] == "agent_session.idle"


def test_azure_openai_endpoint_is_normalized(monkeypatch) -> None:
    """Azure resource root endpoints are normalized to the OpenCode base path."""
    monkeypatch.setenv("AZURE_OPENAI_ENDPOINT", "https://example-resource.openai.azure.com/")

    assert azure_openai_base_url() == "https://example-resource.openai.azure.com/openai"
    assert azure_openai_responses_url() == "https://example-resource.openai.azure.com/openai/v1/responses"
    assert azure_openai_resource_name() == "example-resource"


def test_submit_turn_returns_failure_response_for_backend_error(tmp_path, monkeypatch) -> None:
    """OpenCode server HTTP failures become normalized failed AgentTurnResult values."""
    root = tmp_path / "session_1"
    workspace = root / "workspace"
    workspace.mkdir(parents=True)
    (root / "opencode-server.json").write_text(
        json.dumps(
            {
                "status": "ready",
                "endpoint": "http://127.0.0.1:4097",
                "workspace": str(workspace),
                "auth": {"username": "opencode", "password": "test-password"},
                "opencode_session_id": "oc_session_1",
            }
        ),
        encoding="utf-8",
    )
    request = AgentTurnRequest(
        id="turn_1",
        agent_session_id="session_1",
        type="prompt_response",
        instruction="hello",
    )

    def fake_urlopen(http_request, timeout=60):  # noqa: ANN001, ANN202, ARG001
        raise urllib.error.HTTPError(
            url=http_request.full_url,
            code=401,
            msg="Unauthorized",
            hdrs=Message(),
            fp=io.BytesIO(b"invalid api key"),
        )

    monkeypatch.setenv("LAMPLIGHTER_OPENCODE_USE_REAL_BACKEND", "1")
    monkeypatch.setattr(urllib.request, "urlopen", fake_urlopen)

    result = submit_turn(request, root)

    assert result.status == "failed"
    assert result.failure_report is not None
    assert result.failure_report["summary"] == "OpenCode server request failed."
    assert "HTTP 401" in str(result.failure_report["detail"])
    assert "invalid api key" in str(result.failure_report["detail"])


class _JsonResponse:
    def __init__(self, value: object) -> None:
        self._payload = json.dumps(value).encode("utf-8")

    def __enter__(self) -> "_JsonResponse":
        return self

    def __exit__(self, *args) -> None:  # noqa: ANN002
        return None

    def read(self) -> bytes:
        return self._payload


class _SseResponse:
    def __init__(self, values: list[object]) -> None:
        self._lines = []
        for value in values:
            self._lines.append(f"data: {json.dumps(value)}\n".encode())
            self._lines.append(b"\n")

    def __enter__(self) -> "_SseResponse":
        return self

    def __exit__(self, *args) -> None:  # noqa: ANN002
        return None

    def __iter__(self):
        return iter(self._lines)
