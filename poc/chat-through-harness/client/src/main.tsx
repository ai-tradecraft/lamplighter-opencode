import React, { FormEvent, useEffect, useMemo, useState } from "react";
import { createRoot } from "react-dom/client";
import { HubConnectionBuilder } from "@microsoft/signalr";
import { Bot, CircleStop, Cpu, Play, Send, Terminal } from "lucide-react";
import "./styles.css";

type AgentController = {
  runnerId: string;
  status: string;
  observedAt: string;
  agents: ControllerAgent[];
};

type ControllerAgent = {
  agentSessionId: string;
  status: string;
  runtimePath: string;
  workspacePath: string;
  opencodeEndpoint?: string;
  opencodePid?: number;
  observedAt: string;
};

type Session = {
  id: string;
  controllerId?: string;
  status: string;
  runtimePath: string;
  workspacePath: string;
  backendKind: string;
  branchName: string;
  failureSummary?: string;
  diagnostics?: Diagnostics;
};

type Turn = {
  id: string;
  sessionId: string;
  prompt: string;
  status: string;
  response?: string;
  failureSummary?: string;
  diagnostics?: Diagnostics;
};

type RuntimeEvent = {
  id: string;
  sessionId: string;
  turnId?: string;
  type: string;
  createdAt: string;
};

type Diagnostics = {
  command: string;
  exitCode: number;
  stdoutTail: string;
  stderrTail: string;
  logPath: string;
};

const api = import.meta.env.VITE_API_BASE_URL ?? "";

function App() {
  const [controllers, setControllers] = useState<AgentController[]>([]);
  const [selectedControllerId, setSelectedControllerId] = useState<string | null>(null);
  const [session, setSession] = useState<Session | null>(null);
  const [turns, setTurns] = useState<Turn[]>([]);
  const [events, setEvents] = useState<RuntimeEvent[]>([]);
  const [prompt, setPrompt] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const latestDiagnostics = useMemo(() => {
    return [...turns].reverse().find((turn) => turn.diagnostics)?.diagnostics ?? session?.diagnostics;
  }, [session, turns]);
  const selectedController = useMemo(() => {
    return controllers.find((controller) => controller.runnerId === selectedControllerId) ?? null;
  }, [controllers, selectedControllerId]);

  useEffect(() => {
    refreshControllers();
    const interval = window.setInterval(refreshControllers, 5000);
    return () => window.clearInterval(interval);
  }, []);

  useEffect(() => {
    if (!session) {
      return;
    }

    const connection = new HubConnectionBuilder().withUrl(`${api}/hubs/agent-sessions`).withAutomaticReconnect().build();
    const handleEvent = (event: RuntimeEvent) => {
      setEvents((current) => [...current.filter((item) => item.id !== event.id), event]);
      if (event.type.startsWith("agent_turn.")) {
        refreshTurn(session.id, event.turnId);
      }
      if (event.type.startsWith("agent_session.")) {
        refreshSession(session.id);
        refreshControllers();
      }
    };

    [
      "agent_session.preparing",
      "agent_session.ready",
      "agent_session.failed",
      "agent_session.cancelled",
      "agent_turn.submitted",
      "agent_turn.completed",
      "agent_turn.failed"
    ].forEach((name) => connection.on(name, handleEvent));

    connection.start().then(() => connection.invoke("JoinSession", session.id)).catch((err) => setError(String(err)));
    return () => {
      connection.stop().catch(() => undefined);
    };
  }, [session?.id]);

  async function refreshControllers() {
    const response = await fetch(`${api}/api/agent-controllers`);
    if (!response.ok) {
      return;
    }
    const nextControllers = (await response.json()) as AgentController[];
    setControllers(nextControllers);
    setSelectedControllerId((current) => current ?? nextControllers[0]?.runnerId ?? null);
  }

  async function startSession() {
    if (!selectedControllerId) {
      setError("No active local controller is available.");
      return;
    }

    setBusy(true);
    setError(null);
    try {
      const response = await fetch(`${api}/api/agent-sessions`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ goal: "Chat through harness POC", controllerId: selectedControllerId })
      });
      if (!response.ok) {
        throw new Error(await response.text());
      }
      const created = (await response.json()) as Session;
      setSession(created);
      setTurns([]);
      await refreshEvents(created.id);
      await refreshControllers();
    } catch (err) {
      setError(String(err));
    } finally {
      setBusy(false);
    }
  }

  async function openAgent(agent: ControllerAgent) {
    setBusy(true);
    setError(null);
    try {
      await refreshSession(agent.agentSessionId);
      await refreshTurns(agent.agentSessionId);
      await refreshEvents(agent.agentSessionId);
    } catch (err) {
      setError(String(err));
    } finally {
      setBusy(false);
    }
  }

  async function submitPrompt(event: FormEvent) {
    event.preventDefault();
    if (!session || !prompt.trim()) {
      return;
    }

    const currentPrompt = prompt.trim();
    setPrompt("");
    setBusy(true);
    setError(null);
    try {
      const response = await fetch(`${api}/api/agent-sessions/${session.id}/turns`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ prompt: currentPrompt })
      });
      if (!response.ok) {
        throw new Error(await response.text());
      }
      const turn = (await response.json()) as Turn;
      setTurns((current) => [...current.filter((item) => item.id !== turn.id), turn]);
      await refreshEvents(session.id);
    } catch (err) {
      setError(String(err));
    } finally {
      setBusy(false);
    }
  }

  async function cancelSession() {
    if (!session) {
      return;
    }
    await fetch(`${api}/api/agent-sessions/${session.id}/cancel`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ reason: "Cancelled from UI." })
    });
    await refreshSession(session.id);
  }

  async function refreshSession(sessionId: string) {
    const response = await fetch(`${api}/api/agent-sessions/${sessionId}`);
    if (response.ok) {
      setSession((await response.json()) as Session);
    }
  }

  async function refreshTurns(sessionId: string) {
    const response = await fetch(`${api}/api/agent-sessions/${sessionId}/turns`);
    if (response.ok) {
      setTurns((await response.json()) as Turn[]);
    }
  }

  async function refreshTurn(sessionId: string, turnId?: string) {
    if (!turnId) {
      return;
    }
    const response = await fetch(`${api}/api/agent-sessions/${sessionId}/turns/${turnId}`);
    if (response.ok) {
      const turn = (await response.json()) as Turn;
      setTurns((current) => [...current.filter((item) => item.id !== turn.id), turn]);
    }
  }

  async function refreshEvents(sessionId: string) {
    const response = await fetch(`${api}/api/agent-sessions/${sessionId}/events`);
    if (response.ok) {
      setEvents((await response.json()) as RuntimeEvent[]);
    }
  }

  return (
    <main className="shell">
      <aside className="panel setup">
        <div className="panel-title">
          <Cpu size={18} />
          Controllers
        </div>
        <div className="controller-list">
          {controllers.length === 0 ? <div className="empty-list">No controllers online.</div> : null}
          {controllers.map((controller) => (
            <button
              className={`list-button ${controller.runnerId === selectedControllerId ? "selected" : ""}`}
              key={controller.runnerId}
              onClick={() => setSelectedControllerId(controller.runnerId)}
            >
              <span>{controller.runnerId}</span>
              <small>{controller.status} · {controller.agents.length}</small>
            </button>
          ))}
        </div>

        <div className="panel-title secondary">
          <Bot size={18} />
          Agents
        </div>
        <div className="agent-list">
          {!selectedController ? <div className="empty-list">Select a controller.</div> : null}
          {selectedController?.agents.length === 0 ? <div className="empty-list">No agents allocated.</div> : null}
          {selectedController?.agents.map((agent) => (
            <button
              className={`list-button ${agent.agentSessionId === session?.id ? "selected" : ""}`}
              key={agent.agentSessionId}
              onClick={() => openAgent(agent)}
            >
              <span>{agent.agentSessionId}</span>
              <small>{agent.status}</small>
            </button>
          ))}
        </div>

        <div className="button-row">
          <button onClick={startSession} disabled={busy || !selectedControllerId}>
            <Play size={16} />
            New Agent
          </button>
        </div>

        <div className="panel-title secondary">
          <Bot size={18} />
          Selected
        </div>
        <dl>
          <dt>Status</dt>
          <dd data-status={session?.status ?? "idle"}>{session?.status ?? "idle"}</dd>
          <dt>Backend</dt>
          <dd>{session?.backendKind ?? "opencode"}</dd>
          <dt>Branch</dt>
          <dd>{session?.branchName ?? "poc-chat-through-harness"}</dd>
          <dt>Workspace</dt>
          <dd>{session?.workspacePath ?? "-"}</dd>
        </dl>
        <div className="button-row">
          <button onClick={cancelSession} disabled={!session || session.status === "cancelled"}>
            <CircleStop size={16} />
            Cancel
          </button>
        </div>
      </aside>

      <section className="chat">
        <div className="transcript">
          {turns.length === 0 ? <div className="empty">Start a session and send the first prompt.</div> : null}
          {turns.map((turn) => (
            <article className="turn" key={turn.id}>
              <div className="bubble user">{turn.prompt}</div>
              <div className={`bubble agent ${turn.status}`}>{turn.response ?? turn.failureSummary ?? turn.status}</div>
            </article>
          ))}
        </div>
        <form onSubmit={submitPrompt} className="composer">
          <input
            value={prompt}
            onChange={(event) => setPrompt(event.target.value)}
            placeholder="Send a prompt to the harnessed agent"
            disabled={!session || session.status !== "ready" || busy}
          />
          <button disabled={!session || session.status !== "ready" || busy || !prompt.trim()}>
            <Send size={16} />
          </button>
        </form>
        {error ? <div className="error">{error}</div> : null}
      </section>

      <aside className="panel event-rail">
        <div className="panel-title">
          <Terminal size={18} />
          Runtime Events
        </div>
        {events.map((event) => (
          <div className="event" key={event.id}>
            <strong>{event.type}</strong>
            <span>{new Date(event.createdAt).toLocaleTimeString()}</span>
          </div>
        ))}
      </aside>

      <section className="diagnostics">
        <strong>Diagnostics</strong>
        <pre>{latestDiagnostics ? JSON.stringify(latestDiagnostics, null, 2) : "No diagnostics yet."}</pre>
      </section>
    </main>
  );
}

createRoot(document.getElementById("root")!).render(<App />);
