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

type SessionSummary = {
  sessionId: string;
  status: string;
  runtimePath: string;
  openCodeSessionId?: string;
  observedAt: string;
};

type ControllerAgent = {
  agentId?: string;
  agentSessionId: string;
  status: string;
  runtimePath: string;
  workspacePath: string;
  openCodeEndpoint?: string;
  openCodePid?: number;
  observedAt: string;
  sessions?: SessionSummary[];
};

type AgentController = {
  runnerId: string;
  status: string;
  observedAt: string;
  agents: ControllerAgent[];
};

type Agent = {
  id: string;
  controllerId: string;
  status: string;
  createdAt: string;
  readyAt?: string;
  endedAt?: string;
  runtimePath: string;
  workspacePath: string;
  backendKind: string;
  branchName: string;
  openCodeEndpoint?: string;
  openCodePid?: number;
};

type Session = {
  id: string;
  agentId?: string;
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

type Route =
  | { kind: "controllers" }
  | { kind: "controller"; controllerId: string }
  | { kind: "agent"; agentId: string }
  | { kind: "session"; agentId: string; sessionId: string };

const api = import.meta.env.VITE_API_BASE_URL ?? "";
const hiddenAgentStatuses = new Set(["stopped", "cancelled", "failed"]);
const hiddenSessionStatuses = new Set(["cancelled", "failed"]);

function agentIdentifier(agent: ControllerAgent) {
  return agent.agentId ?? agent.agentSessionId;
}

function isActiveAgent(agent: ControllerAgent) {
  return !hiddenAgentStatuses.has(agent.status.toLowerCase());
}

function isActiveSession(session: Session) {
  return !hiddenSessionStatuses.has(session.status.toLowerCase());
}

function parseRoute(pathname: string): Route {
  const parts = pathname.split("/").filter(Boolean).map(decodeURIComponent);
  if (parts[0] === "controllers" && parts.length === 2) {
    return { kind: "controller", controllerId: parts[1] };
  }
  if (parts[0] === "agents" && parts.length === 2) {
    return { kind: "agent", agentId: parts[1] };
  }
  if (parts[0] === "agents" && parts[2] === "sessions" && parts.length === 4) {
    return { kind: "session", agentId: parts[1], sessionId: parts[3] };
  }
  return { kind: "controllers" };
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
    body: JSON.stringify({ level, message, timestamp: new Date().toISOString(), context: normalizedContext })
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
    reportClientLog("error", "API request failed.", { method, path, error: String(error) });
    throw error;
  }
}

async function responseError(response: Response, fallback: string) {
  const body = (await response.text()).trim();
  if (body) {
    try {
      const payload = JSON.parse(body) as { message?: string };
      if (payload.message) {
        return payload.message;
      }
    } catch {
      return body;
    }
    return body;
  }
  return `${fallback} (HTTP ${response.status}). The portal and API may be running different versions; restart the POC services.`;
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
  reportClientLog("error", "Unhandled browser promise rejection.", { reason: String(event.reason) });
});
reportClientLog("information", "Portal client initialized.", { path: window.location.pathname });

function App() {
  const [route, setRoute] = useState<Route>(() => parseRoute(window.location.pathname));
  const [controllers, setControllers] = useState<AgentController[]>([]);
  const [agent, setAgent] = useState<Agent | null>(null);
  const [sessions, setSessions] = useState<Session[]>([]);
  const [session, setSession] = useState<Session | null>(null);
  const [turns, setTurns] = useState<Turn[]>([]);
  const [events, setEvents] = useState<RuntimeEvent[]>([]);
  const [prompt, setPrompt] = useState("");
  const [showAllAgents, setShowAllAgents] = useState(false);
  const [showAllSessions, setShowAllSessions] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const selectedController = useMemo(() => {
    const controllerId = route.kind === "controller"
      ? route.controllerId
      : agent?.controllerId ?? session?.controllerId;
    return controllers.find((controller) => controller.runnerId === controllerId) ?? null;
  }, [agent?.controllerId, controllers, route, session?.controllerId]);
  const visibleAgents = useMemo(() => {
    return selectedController?.agents.filter((item) => showAllAgents || isActiveAgent(item)) ?? [];
  }, [selectedController, showAllAgents]);
  const visibleSessions = useMemo(() => {
    return sessions.filter((item) => showAllSessions || isActiveSession(item));
  }, [sessions, showAllSessions]);
  const latestDiagnostics = useMemo(() => {
    const turn = [...turns].reverse().find((item) => item.diagnostics || item.failureDetail);
    if (turn?.diagnostics) {
      return turn.diagnostics;
    }
    if (turn?.failureDetail) {
      return { summary: turn.failureSummary ?? "Agent turn failed.", detail: turn.failureDetail };
    }
    return session?.diagnostics;
  }, [session, turns]);
  const chatPlaceholder = busy
    ? "Loading agent session..."
    : !session
      ? "Select a session to chat"
      : session.status !== "ready"
        ? `Session is ${session.status}; chat is unavailable`
        : "Send a prompt to the harnessed agent";

  function navigate(path: string, replace = false) {
    if (replace) {
      window.history.replaceState(null, "", path);
    } else {
      window.history.pushState(null, "", path);
    }
    setRoute(parseRoute(path));
    setError(null);
  }

  useEffect(() => {
    if (window.location.pathname === "/") {
      navigate("/controllers", true);
    }
    const handlePopState = () => setRoute(parseRoute(window.location.pathname));
    window.addEventListener("popstate", handlePopState);
    return () => window.removeEventListener("popstate", handlePopState);
  }, []);

  useEffect(() => {
    void verifyApiContract().then((compatible) => {
      if (compatible) {
        void refreshControllers();
      }
    });
    const interval = window.setInterval(() => void refreshControllers(), 5000);
    return () => window.clearInterval(interval);
  }, []);

  useEffect(() => {
    if (route.kind === "agent" || route.kind === "session") {
      void loadAgent(route.agentId);
      const interval = window.setInterval(() => void loadAgent(route.agentId), 5000);
      return () => window.clearInterval(interval);
    }
    setAgent(null);
    setSessions([]);
  }, [route.kind === "agent" ? route.agentId : route.kind === "session" ? route.agentId : null]);

  useEffect(() => {
    if (route.kind === "session") {
      void openChatSession(route.sessionId);
    } else {
      setSession(null);
      setTurns([]);
      setEvents([]);
    }
  }, [route.kind === "session" ? route.sessionId : null]);

  useEffect(() => {
    if (!session) {
      return;
    }
    const connection = new HubConnectionBuilder().withUrl(`${api}/hubs/agent-sessions`).withAutomaticReconnect().build();
    const handleEvent = (runtimeEvent: RuntimeEvent) => {
      setEvents((current) => [...current.filter((item) => item.id !== runtimeEvent.id), runtimeEvent]);
      if (runtimeEvent.type.startsWith("agent_turn.")) {
        void refreshTurn(session.id, runtimeEvent.turnId);
      }
      if (runtimeEvent.type.startsWith("agent_session.")) {
        void refreshSession(session.id);
        if (session.agentId) {
          void loadAgent(session.agentId);
        }
      }
    };
    [
      "agent_session.preparing",
      "agent_session.ready",
      "agent_session.created",
      "agent_session.failed",
      "agent_session.cancelled",
      "agent_turn.submitted",
      "agent_turn.completed",
      "agent_turn.failed"
    ].forEach((name) => connection.on(name, handleEvent));
    connection
      .start()
      .then(() => connection.invoke("JoinSession", session.id))
      .catch((err) => setError(String(err)));
    return () => {
      void connection.stop();
    };
  }, [session?.id]);

  async function refreshControllers() {
    try {
      const response = await apiFetch("/api/agent-controllers");
      if (response.ok) {
        setControllers((await response.json()) as AgentController[]);
      }
    } catch {
      return;
    }
  }

  async function verifyApiContract() {
    try {
      const response = await apiFetch("/api/system/info");
      if (!response.ok) {
        setError(await responseError(response, "The API does not expose the required system contract"));
        return false;
      }
      const system = (await response.json()) as { contractVersion?: number };
      if (system.contractVersion !== 2) {
        setError(
          `Portal/API contract mismatch: expected version 2, received ${system.contractVersion ?? "unknown"}. Restart the POC services.`
        );
        return false;
      }
      return true;
    } catch (err) {
      setError(String(err));
      return false;
    }
  }

  async function loadAgent(agentId: string) {
    try {
      const [agentResponse, sessionsResponse] = await Promise.all([
        apiFetch(`/api/agents/${agentId}`),
        apiFetch(`/api/agents/${agentId}/sessions`)
      ]);
      if (!agentResponse.ok) {
        throw new Error(await responseError(agentResponse, `Agent ${agentId} is no longer available`));
      }
      setAgent((await agentResponse.json()) as Agent);
      if (sessionsResponse.ok) {
        setSessions((await sessionsResponse.json()) as Session[]);
      }
    } catch (err) {
      setError(String(err));
    }
  }

  async function createAgent() {
    if (route.kind !== "controller") {
      return;
    }
    setBusy(true);
    setError(null);
    try {
      const response = await apiFetch(`/api/agent-controllers/${route.controllerId}/agents`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({})
      });
      if (!response.ok) {
        throw new Error(await responseError(response, "Agent creation failed"));
      }
      const created = (await response.json()) as Agent;
      reportClientLog("information", "Agent creation accepted.", {
        agentId: created.id,
        controllerId: route.controllerId
      });
      navigate(`/agents/${encodeURIComponent(created.id)}`);
    } catch (err) {
      setError(String(err));
    } finally {
      setBusy(false);
    }
  }

  async function createSession() {
    if (!agent) {
      return;
    }
    setBusy(true);
    setError(null);
    try {
      const response = await apiFetch(`/api/agents/${agent.id}/sessions`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ goal: "Chat through harness POC" })
      });
      if (!response.ok) {
        throw new Error(await responseError(response, "Session creation failed"));
      }
      const created = (await response.json()) as Session;
      setSessions((current) => [created, ...current.filter((item) => item.id !== created.id)]);
      reportClientLog("information", "Agent session creation accepted.", {
        agentId: agent.id,
        sessionId: created.id
      });
    } catch (err) {
      setError(String(err));
    } finally {
      setBusy(false);
    }
  }

  async function stopAgent() {
    if (!agent) {
      return;
    }
    const response = await apiFetch(`/api/agents/${agent.id}/stop`, { method: "POST" });
    if (response.ok) {
      setAgent((await response.json()) as Agent);
    } else {
      setError(await responseError(response, "Stopping the agent failed"));
    }
  }

  async function openChatSession(sessionId: string) {
    setBusy(true);
    setError(null);
    try {
      await Promise.all([refreshSession(sessionId), refreshTurns(sessionId), refreshEvents(sessionId)]);
      reportClientLog("information", "Agent chat session opened.", { sessionId });
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
    const response = await apiFetch(`/api/agent-sessions/${session.id}/cancel`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ reason: "Cancelled from UI." })
    });
    if (response.ok) {
      await refreshSession(session.id);
    } else {
      setError(await response.text());
    }
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

  if (route.kind === "controllers") {
    return (
      <main className="portal-page">
        <header className="page-heading">
          <div><p className="eyebrow">Lamplighter</p><h1>Controllers</h1></div>
          <span className="summary-count">{controllers.length} active</span>
        </header>
        <section className="resource-list" aria-label="Active controllers">
          {controllers.length === 0 ? <div className="empty-state">No controllers online.</div> : null}
          {controllers.map((controller) => (
            <button
              className="resource-row"
              key={controller.runnerId}
              onClick={() => navigate(`/controllers/${encodeURIComponent(controller.runnerId)}`)}
            >
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

  if (route.kind === "controller" && selectedController) {
    return (
      <main className="portal-page">
        <button className="back-button" onClick={() => navigate("/controllers")} title="Back to controllers">
          <ArrowLeft size={18} /> Controllers
        </button>
        <header className="detail-heading">
          <div><p className="eyebrow">Controller</p><h1>{selectedController.runnerId}</h1></div>
          <span className="status-value prominent" data-status={selectedController.status}>{selectedController.status}</span>
        </header>
        <section className="detail-band">
          <div><Clock3 size={17} /><span>Last heartbeat</span><strong>{new Date(selectedController.observedAt).toLocaleString()}</strong></div>
          <div><Activity size={17} /><span>Active agents</span><strong>{selectedController.agents.filter(isActiveAgent).length}</strong></div>
        </section>
        <section className="section-heading">
          <div><h2>Agents</h2><p>Provisioned on this controller</p></div>
          <div className="section-actions">
            <label className="show-all-toggle">
              <input type="checkbox" checked={showAllAgents} onChange={(event) => setShowAllAgents(event.target.checked)} />
              Show all
            </label>
            <button onClick={createAgent} disabled={busy}><Play size={16} /> New Agent</button>
          </div>
        </section>
        <section className="resource-list" aria-label="Controller agents">
          {selectedController.agents.length === 0 ? <div className="empty-state">No agents allocated.</div> : null}
          {selectedController.agents.length > 0 && visibleAgents.length === 0 ? <div className="empty-state">No active agents.</div> : null}
          {visibleAgents.map((item) => {
            const id = agentIdentifier(item);
            return (
              <button className="resource-row" key={id} onClick={() => navigate(`/agents/${encodeURIComponent(id)}`)}>
                <span className="resource-icon"><Bot size={20} /></span>
                <span className="resource-primary">
                  <strong title={id}>{id}</strong>
                  <small>{item.sessions?.length ?? 0} sessions · observed {new Date(item.observedAt).toLocaleString()}</small>
                </span>
                <span className="status-value" data-status={item.status}>{item.status}</span>
                <ChevronRight size={18} />
              </button>
            );
          })}
        </section>
        {error ? <div className="page-error">{error}</div> : null}
      </main>
    );
  }

  if (route.kind === "agent" && agent) {
    return (
      <main className="portal-page">
        <button
          className="back-button"
          onClick={() => navigate(`/controllers/${encodeURIComponent(agent.controllerId)}`)}
          title="Back to controller"
        >
          <ArrowLeft size={18} /> {agent.controllerId}
        </button>
        <header className="detail-heading">
          <div><p className="eyebrow">Agent</p><h1 title={agent.id}>{agent.id}</h1></div>
          <span className="status-value prominent" data-status={agent.status}>{agent.status}</span>
        </header>
        <section className="agent-details">
          <dl>
            <dt>Controller</dt><dd>{agent.controllerId}</dd>
            <dt>Backend</dt><dd>{agent.backendKind}</dd>
            <dt>Branch</dt><dd>{agent.branchName}</dd>
            <dt>OpenCode</dt><dd>{agent.openCodeEndpoint || "Not available"}</dd>
            <dt>Process</dt><dd>{agent.openCodePid ?? "Not available"}</dd>
            <dt>Workspace</dt><dd title={agent.workspacePath}>{agent.workspacePath || "Pending allocation"}</dd>
            <dt>Runtime</dt><dd title={agent.runtimePath}>{agent.runtimePath || "Pending allocation"}</dd>
          </dl>
          <button className="secondary-button" onClick={stopAgent} disabled={agent.status === "stopped"}>
            <CircleStop size={16} /> Stop Agent
          </button>
        </section>
        <section className="section-heading">
          <div><h2>Sessions</h2><p>Conversations available for this agent</p></div>
          <div className="section-actions">
            <label className="show-all-toggle">
              <input type="checkbox" checked={showAllSessions} onChange={(event) => setShowAllSessions(event.target.checked)} />
              Show all
            </label>
            <button onClick={createSession} disabled={busy || agent.status !== "ready"}>
              <MessageSquare size={16} /> New Session
            </button>
          </div>
        </section>
        <section className="resource-list" aria-label="Agent sessions">
          {sessions.length === 0 ? <div className="empty-state">No sessions created.</div> : null}
          {sessions.length > 0 && visibleSessions.length === 0 ? <div className="empty-state">No active sessions.</div> : null}
          {visibleSessions.map((item) => (
            <button
              className="resource-row"
              key={item.id}
              onClick={() => navigate(`/agents/${encodeURIComponent(agent.id)}/sessions/${encodeURIComponent(item.id)}`)}
            >
              <span className="resource-icon"><MessageSquare size={20} /></span>
              <span className="resource-primary">
                <strong title={item.id}>{item.id}</strong>
                <small>{item.createdAt ? `Created ${new Date(item.createdAt).toLocaleString()}` : "Current conversation"}</small>
              </span>
              <span className="status-value" data-status={item.status}>{item.status}</span>
              <ChevronRight size={18} />
            </button>
          ))}
        </section>
        {error ? <div className="page-error">{error}</div> : null}
      </main>
    );
  }

  if (route.kind === "session" && session) {
    return (
      <main className="session-page">
        <header className="session-header">
          <button
            className="icon-button"
            onClick={() => navigate(`/agents/${encodeURIComponent(route.agentId)}`)}
            title="Back to agent"
          >
            <ArrowLeft size={19} />
          </button>
          <div className="session-identity">
            <span>{route.agentId}</span><ChevronRight size={14} /><span>{session.id}</span>
          </div>
          <div className="header-actions">
            <span className="status-value" data-status={session.status}>{session.status}</span>
            <button
              className="icon-button danger"
              onClick={cancelSession}
              disabled={session.status === "cancelled"}
              title="Cancel session"
            >
              <CircleStop size={18} />
            </button>
          </div>
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
                disabled={session.status !== "ready" || busy}
              />
              <button disabled={session.status !== "ready" || busy || !prompt.trim()} title="Send prompt">
                <Send size={16} />
              </button>
            </form>
            {error ? <div className="error">{error}</div> : null}
          </section>
          <aside className="panel event-rail">
            <div className="panel-title"><Terminal size={18} /> Runtime Events</div>
            {events.length === 0 ? <div className="empty-list">No runtime events yet.</div> : null}
            {events.map((runtimeEvent) => (
              <div className="event" key={runtimeEvent.id}>
                <strong>{runtimeEvent.type}</strong>
                <span>{new Date(runtimeEvent.createdAt).toLocaleTimeString()}</span>
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
      <button className="back-button" onClick={() => navigate("/controllers")}>
        <ArrowLeft size={18} /> Controllers
      </button>
      <div className="empty-state">{error ?? "Loading selected resource..."}</div>
    </main>
  );
}

createRoot(document.getElementById("root")!).render(<App />);
