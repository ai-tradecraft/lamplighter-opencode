import React, { FormEvent, useEffect, useMemo, useState } from "react";
import { createRoot } from "react-dom/client";
import { HubConnectionBuilder } from "@microsoft/signalr";
import {
  Activity,
  ArrowLeft,
  Bot,
  ChevronRight,
  CircleStop,
  Clock3,
  MessageSquare,
  Play,
  Send,
  Server,
  Terminal
} from "lucide-react";
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
  openCodeEndpoint?: string;
  openCodePid?: number;
  observedAt: string;
};

type Session = {
  id: string;
  controllerId?: string;
  status: string;
  createdAt?: string;
  readyAt?: string;
  endedAt?: string;
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
  failureDetail?: string;
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

type PortalView = "controllers" | "controller" | "agent" | "session";

const api = import.meta.env.VITE_API_BASE_URL ?? "";
const hiddenAgentStatuses = new Set(["stopped", "cancelled"]);

function isActiveAgent(agent: ControllerAgent) {
  return !hiddenAgentStatuses.has(agent.status.toLowerCase());
}

type ClientLogLevel = "debug" | "information" | "warning" | "error";
type ClientLogContext = Record<string, string | number | boolean | null | undefined>;

function reportClientLog(level: ClientLogLevel, message: string, context?: ClientLogContext) {
  const normalizedContext = context
    ? Object.fromEntries(Object.entries(context).map(([key, value]) => [key, value == null ? null : String(value)]))
    : undefined;
  void fetch(`${api}/api/client-logs`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      level,
      message,
      timestamp: new Date().toISOString(),
      context: normalizedContext
    })
  }).catch(() => undefined);
}

async function apiFetch(path: string, init?: RequestInit) {
  const method = init?.method ?? "GET";
  try {
    const response = await fetch(`${api}${path}`, init);
    if (!response.ok) {
      reportClientLog("warning", "API request returned a non-success response.", {
        method,
        path,
        status: response.status
      });
    }
    return response;
  } catch (error) {
    reportClientLog("error", "API request failed.", {
      method,
      path,
      error: String(error)
    });
    throw error;
  }
}

window.addEventListener("error", (event) => {
  reportClientLog("error", "Unhandled browser error.", {
    message: event.message,
    source: event.filename,
    line: event.lineno,
    column: event.colno
  });
});
window.addEventListener("unhandledrejection", (event) => {
  reportClientLog("error", "Unhandled browser promise rejection.", {
    reason: String(event.reason)
  });
});
reportClientLog("information", "Portal client initialized.", {
  path: window.location.pathname
});

function App() {
  const [controllers, setControllers] = useState<AgentController[]>([]);
  const [selectedControllerId, setSelectedControllerId] = useState<string | null>(null);
  const [selectedAgentId, setSelectedAgentId] = useState<string | null>(null);
  const [view, setView] = useState<PortalView>("controllers");
  const [session, setSession] = useState<Session | null>(null);
  const [turns, setTurns] = useState<Turn[]>([]);
  const [events, setEvents] = useState<RuntimeEvent[]>([]);
  const [prompt, setPrompt] = useState("");
  const [showAllAgents, setShowAllAgents] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const latestDiagnostics = useMemo(() => {
    const turn = [...turns].reverse().find((item) => item.diagnostics || item.failureDetail);
    if (turn?.diagnostics) {
      return turn.diagnostics;
    }
    if (turn?.failureDetail) {
      return {
        summary: turn.failureSummary ?? "Agent turn failed.",
        detail: turn.failureDetail
      };
    }
    return session?.diagnostics;
  }, [session, turns]);
  const selectedController = useMemo(() => {
    return controllers.find((controller) => controller.runnerId === selectedControllerId) ?? null;
  }, [controllers, selectedControllerId]);
  const selectedAgent = useMemo(() => {
    return selectedController?.agents.find((agent) => agent.agentSessionId === selectedAgentId) ?? null;
  }, [selectedAgentId, selectedController]);
  const visibleAgents = useMemo(() => {
    return selectedController?.agents.filter((agent) => showAllAgents || isActiveAgent(agent)) ?? [];
  }, [selectedController, showAllAgents]);
  const chatPlaceholder = busy
    ? "Loading agent session..."
    : !session
      ? "Select a ready agent to chat"
      : session.status !== "ready"
        ? `Agent is ${session.status}; chat is unavailable`
        : "Send a prompt to the harnessed agent";

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
        refreshSession(session.id).catch((err) => setError(String(err)));
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

    connection
      .start()
      .then(() => connection.invoke("JoinSession", session.id))
      .catch((err) => {
        reportClientLog("error", "SignalR session connection failed.", {
          sessionId: session.id,
          error: String(err)
        });
        setError(String(err));
      });
    return () => {
      connection.stop().catch(() => undefined);
    };
  }, [session?.id]);

  async function refreshControllers() {
    let response: Response;
    try {
      response = await apiFetch("/api/agent-controllers");
    } catch {
      return;
    }
    if (!response.ok) {
      return;
    }
    const nextControllers = (await response.json()) as AgentController[];
    setControllers(nextControllers);
    setSelectedControllerId((current) => {
      return current && nextControllers.some((controller) => controller.runnerId === current) ? current : null;
    });
  }

  function openController(controller: AgentController) {
    setSelectedControllerId(controller.runnerId);
    setSelectedAgentId(null);
    setSession(null);
    setTurns([]);
    setEvents([]);
    setError(null);
    setView("controller");
  }

  async function startSession() {
    if (!selectedControllerId) {
      setError("No active local controller is available.");
      return;
    }

    setBusy(true);
    setError(null);
    try {
      const response = await apiFetch("/api/agent-sessions", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ goal: "Chat through harness POC", controllerId: selectedControllerId })
      });
      if (!response.ok) {
        throw new Error(await response.text());
      }
      const created = (await response.json()) as Session;
      reportClientLog("information", "Agent session creation accepted.", {
        sessionId: created.id,
        controllerId: selectedControllerId
      });
      setSession(created);
      setSelectedAgentId(created.id);
      setTurns([]);
      await refreshEvents(created.id);
      await refreshControllers();
      setView("agent");
    } catch (err) {
      setError(String(err));
    } finally {
      setBusy(false);
    }
  }

  async function openAgent(agent: ControllerAgent) {
    setBusy(true);
    setError(null);
    setSelectedAgentId(agent.agentSessionId);
    setTurns([]);
    setEvents([]);
    try {
      await refreshSession(agent.agentSessionId);
      reportClientLog("information", "Agent details opened.", {
        sessionId: agent.agentSessionId
      });
      setView("agent");
    } catch (err) {
      setError(String(err));
    } finally {
      setBusy(false);
    }
  }

  async function openChatSession(sessionId: string) {
    setBusy(true);
    setError(null);
    try {
      await refreshSession(sessionId);
      await Promise.all([refreshTurns(sessionId), refreshEvents(sessionId)]);
      reportClientLog("information", "Agent chat session opened.", { sessionId });
      setView("session");
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
      const response = await apiFetch(`/api/agent-sessions/${session.id}/turns`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ prompt: currentPrompt })
      });
      if (!response.ok) {
        throw new Error(await response.text());
      }
      const turn = (await response.json()) as Turn;
      reportClientLog("information", "Agent turn submitted.", {
        sessionId: session.id,
        turnId: turn.id
      });
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
    await apiFetch(`/api/agent-sessions/${session.id}/cancel`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ reason: "Cancelled from UI." })
    });
    await refreshSession(session.id);
  }

  async function refreshSession(sessionId: string) {
    const response = await apiFetch(`/api/agent-sessions/${sessionId}`);
    if (!response.ok) {
      throw new Error(`Agent session ${sessionId} is no longer available.`);
    }
    setSession((await response.json()) as Session);
  }

  async function refreshTurns(sessionId: string) {
    const response = await apiFetch(`/api/agent-sessions/${sessionId}/turns`);
    if (response.ok) {
      setTurns((await response.json()) as Turn[]);
    }
  }

  async function refreshTurn(sessionId: string, turnId?: string) {
    if (!turnId) {
      return;
    }
    const response = await apiFetch(`/api/agent-sessions/${sessionId}/turns/${turnId}`);
    if (response.ok) {
      const turn = (await response.json()) as Turn;
      setTurns((current) => [...current.filter((item) => item.id !== turn.id), turn]);
    }
  }

  async function refreshEvents(sessionId: string) {
    const response = await apiFetch(`/api/agent-sessions/${sessionId}/events`);
    if (response.ok) {
      setEvents((await response.json()) as RuntimeEvent[]);
    }
  }

  if (view === "controllers") {
    return (
      <main className="portal-page">
        <header className="page-heading">
          <div>
            <p className="eyebrow">Lamplighter</p>
            <h1>Controllers</h1>
          </div>
          <span className="summary-count">{controllers.length} active</span>
        </header>
        <section className="resource-list" aria-label="Active controllers">
          {controllers.length === 0 ? <div className="empty-state">No controllers online.</div> : null}
          {controllers.map((controller) => (
            <button className="resource-row" key={controller.runnerId} onClick={() => openController(controller)}>
              <span className="resource-icon"><Server size={20} /></span>
              <span className="resource-primary">
                <strong>{controller.runnerId}</strong>
                <small>Last heartbeat {new Date(controller.observedAt).toLocaleString()}</small>
              </span>
              <span className="status-value" data-status={controller.status}>{controller.status}</span>
              <span className="resource-metric">{controller.agents.filter(isActiveAgent).length} agents</span>
              <ChevronRight size={18} />
            </button>
          ))}
        </section>
      </main>
    );
  }

  if (view === "controller" && selectedController) {
    return (
      <main className="portal-page">
        <button className="back-button" onClick={() => setView("controllers")} title="Back to controllers">
          <ArrowLeft size={18} />
          Controllers
        </button>
        <header className="detail-heading">
          <div>
            <p className="eyebrow">Controller</p>
            <h1>{selectedController.runnerId}</h1>
          </div>
          <span className="status-value prominent" data-status={selectedController.status}>{selectedController.status}</span>
        </header>
        <section className="detail-band">
          <div><Clock3 size={17} /><span>Last heartbeat</span><strong>{new Date(selectedController.observedAt).toLocaleString()}</strong></div>
          <div><Activity size={17} /><span>Active agents</span><strong>{selectedController.agents.filter(isActiveAgent).length}</strong></div>
        </section>
        <section className="section-heading">
          <div>
            <h2>Agents</h2>
            <p>Provisioned on this controller</p>
          </div>
          <div className="section-actions">
            <label className="show-all-toggle">
              <input type="checkbox" checked={showAllAgents} onChange={(event) => setShowAllAgents(event.target.checked)} />
              Show all
            </label>
            <button onClick={startSession} disabled={busy}>
              <Play size={16} />
              New Agent
            </button>
          </div>
        </section>
        <section className="resource-list" aria-label="Controller agents">
          {selectedController.agents.length === 0 ? <div className="empty-state">No agents allocated.</div> : null}
          {selectedController.agents.length > 0 && visibleAgents.length === 0 ? (
            <div className="empty-state">No active agents.</div>
          ) : null}
          {visibleAgents.map((agent) => (
            <button className="resource-row" key={agent.agentSessionId} onClick={() => openAgent(agent)}>
              <span className="resource-icon"><Bot size={20} /></span>
              <span className="resource-primary">
                <strong title={agent.agentSessionId}>{agent.agentSessionId}</strong>
                <small>Observed {new Date(agent.observedAt).toLocaleString()}</small>
              </span>
              <span className="status-value" data-status={agent.status}>{agent.status}</span>
              <ChevronRight size={18} />
            </button>
          ))}
        </section>
        {error ? <div className="page-error">{error}</div> : null}
      </main>
    );
  }

  if (view === "agent" && selectedController && session) {
    return (
      <main className="portal-page">
        <button className="back-button" onClick={() => setView("controller")} title="Back to controller">
          <ArrowLeft size={18} />
          {selectedController.runnerId}
        </button>
        <header className="detail-heading">
          <div>
            <p className="eyebrow">Agent</p>
            <h1 title={session.id}>{session.id}</h1>
          </div>
          <span className="status-value prominent" data-status={session.status}>{session.status}</span>
        </header>
        <section className="agent-details">
          <dl>
            <dt>Controller</dt><dd>{selectedController.runnerId}</dd>
            <dt>Backend</dt><dd>{session.backendKind}</dd>
            <dt>Branch</dt><dd>{session.branchName}</dd>
            <dt>OpenCode</dt><dd>{selectedAgent?.openCodeEndpoint ?? "Not available"}</dd>
            <dt>Process</dt><dd>{selectedAgent?.openCodePid ?? "Not available"}</dd>
            <dt>Workspace</dt><dd title={session.workspacePath}>{session.workspacePath}</dd>
            <dt>Runtime</dt><dd title={session.runtimePath}>{session.runtimePath}</dd>
          </dl>
          <button className="secondary-button" onClick={cancelSession} disabled={session.status === "cancelled"}>
            <CircleStop size={16} />
            Cancel Agent
          </button>
        </section>
        <section className="section-heading">
          <div>
            <h2>Sessions</h2>
            <p>Conversations available for this agent</p>
          </div>
        </section>
        <section className="resource-list" aria-label="Agent sessions">
          <button className="resource-row" onClick={() => openChatSession(session.id)} disabled={busy}>
            <span className="resource-icon"><MessageSquare size={20} /></span>
            <span className="resource-primary">
              <strong title={session.id}>{session.id}</strong>
              <small>{session.createdAt ? `Created ${new Date(session.createdAt).toLocaleString()}` : "Current conversation"}</small>
            </span>
            <span className="status-value" data-status={session.status}>{session.status}</span>
            <ChevronRight size={18} />
          </button>
        </section>
        {error ? <div className="page-error">{error}</div> : null}
      </main>
    );
  }

  if (view === "session" && selectedController && session) {
    return (
      <main className="session-page">
        <header className="session-header">
          <button className="icon-button" onClick={() => setView("agent")} title="Back to agent">
            <ArrowLeft size={19} />
          </button>
          <div className="session-identity">
            <span>{selectedController.runnerId}</span>
            <ChevronRight size={14} />
            <span>{session.id}</span>
          </div>
          <span className="status-value" data-status={session.status}>{session.status}</span>
        </header>
        <div className="session-workspace">
          <section className="chat">
            <div className="transcript">
              {turns.length === 0 ? <div className="empty">Send the first prompt to this agent.</div> : null}
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
                placeholder={chatPlaceholder}
                disabled={!session || session.status !== "ready" || busy}
              />
              <button disabled={!session || session.status !== "ready" || busy || !prompt.trim()} title="Send prompt">
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
            {events.length === 0 ? <div className="empty-list">No runtime events yet.</div> : null}
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
        </div>
      </main>
    );
  }

  return (
    <main className="portal-page">
      <button className="back-button" onClick={() => setView("controllers")}>
        <ArrowLeft size={18} />
        Controllers
      </button>
      <div className="empty-state">The selected resource is no longer available.</div>
    </main>
  );
}

createRoot(document.getElementById("root")!).render(<App />);
