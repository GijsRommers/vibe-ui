# Vibe UI

A desktop app for [Mistral Vibe](https://github.com/mistralai/mistral-vibe) in the spirit of
[T3 Code](https://github.com/pingdotgg/t3code), for Mistral only: projects in a sidebar, threads per
project, a chat column, a git changes pane, orchestrator threads that split a job across child
threads, and a pinned Chief of Staff that works across projects and gets briefed by the tasks it
starts. Vibe UI does not re-implement the agent: it starts Vibe's own `vibe-acp` program for each
project and talks to it over the [Agent Client Protocol](https://agentclientprotocol.com) (ACP), the
same way Zed, JetBrains and Xcode do. Mistral Vibe keeps owning your credentials, sessions, tools,
modes, MCP servers, skills and configuration in `~/.vibe`.

The server, CLI, web and mobile pieces of the earlier VibeRe project live on in
[Vibe-Re](https://github.com/GijsRommers/Vibe-Re).

## What you get

- **Projects and threads.** Add folders to the sidebar; each one runs its own `vibe-acp` process and
  keeps any number of threads. Threads survive restarts: Vibe UI resumes the Vibe session
  (`session/load`) and falls back to a saved copy of the transcript when Vibe no longer has it.
- **Chat.** Streamed replies with fenced code blocks, optional model thoughts, tool calls with their
  output, file locations and diffs, and Vibe's plan. Rename a thread from its title.
- **Permissions and modes.** Vibe's permission prompts as buttons (allow once, allow for the
  session, always, deny), driven by the session mode you pick: Ask, Plan, Accept Edits or Auto
  Approve. Model and thinking-level pickers are fed by Vibe. Token usage and cost per thread.
- **Changes pane.** The project's branch, changed files with +/− counts, and a coloured diff of the
  selected file. It refreshes after every turn. Commit all, push, open a pull request and see the
  open pull requests, through `git` and the GitHub CLI (`gh`).
- **Actions.** Commands a project declares in `vibe.re` (build, test, dev server) run from buttons in
  the thread header, with their output in a drawer above the composer. Actions marked
  `runOnWorktreeCreate` set up new task worktrees. Agents get them as `list_actions` and
  `run_action`, plus `list_pull_requests`, `pull_request_checks` and `create_pull_request` through
  `gh`.
- **Settings** (⌘,): the `vibe-acp` executable, default mode, briefing interval, Vibe updates.
- **Orchestrator threads.** A thread that gets extra tools from Vibe UI itself: `delegate_task`,
  `wait_for_task`, `check_task`, `list_tasks`, `send_to_task` and `cancel_task`. Each delegated task
  is a child thread with its own Vibe session, nested under the orchestrator in the sidebar, and can
  run in its own git worktree and branch so parallel tasks do not collide.
- **Chief of Staff.** One standing thread pinned above the projects. It has the same delegation
  tools but can start tasks in any project, and every task it starts briefs it back: a progress
  report every few minutes while the task runs and a final one when it finishes. Briefings arrive in
  its transcript as messages, so the chief can follow up, delegate more or stop a task without you
  watching. "Start over" in its context menu forgets the conversation.
- **Sign in** through Mistral AI Studio in your browser, and sign out. Vibe stores the key.
- **Workspace trust.** An untrusted folder gets a banner with a one-click "Trust this folder".
- **Shortcuts.** ⌘N new thread, ⌘⇧N new orchestrator, ⌘⇧O add project, ⌘D changes pane,
  ⌘⇧T thoughts, ⌘, settings, Esc stop the current turn. Ctrl on Windows and Linux.

## Run it

Install Mistral Vibe first (it needs Python 3.12 or later):

```sh
curl -LsSf https://mistral.ai/vibe/install.sh | bash
# or: uv tool install mistral-vibe
```

Then run Vibe UI from source with the .NET 10 SDK:

```sh
dotnet run --project VibeUi.Desktop
```

`make install` publishes a self-contained build into `~/.local/lib/vibe-ui` and links
`~/.local/bin/vibe-ui`. The **Build and test** workflow uploads `vibe-ui-win-x64`,
`vibe-ui-linux-x64` and `vibe-ui-osx-arm64` folders for every push; these are unsigned.

Vibe UI looks for `vibe-acp` on `PATH` and in `~/.local/bin`, where the Vibe installer and
`uv tool install` put it. Settings or `VIBE_ACP_EXECUTABLE` point it at a specific executable. The
Changes pane and worktrees need `git` on `PATH`; pull requests need `gh` signed in.

## How it works

```
VibeUi.Desktop  Avalonia window: shell → projects → threads, chief of staff, changes pane, MCP server
VibeUi.Acp      ACP client: JSON-RPC 2.0 over the agent's stdin/stdout, typed requests and updates
vibe-acp        Mistral Vibe's ACP entry point, one child process per project folder
```

One agent process per project (plus one for the Chief of Staff); every thread is a session on it
(`session/new`, or `session/load` when the thread comes back after a restart, which replays the
history). The flow per process is `initialize` (announcing file-system support and the
`browser-auth-delegated` meta flag so Vibe hands the sign-in URL to the window instead of opening a
browser itself), then sessions. If a session call fails with ACP error `-32000`, the window shows
the Mistral sign-in button: `authenticate` with `action: start` returns the sign-in URL, and
`action: complete` waits for the browser flow and stores the key.

Each prompt is `session/prompt`; Vibe streams `session/update` notifications
(`agent_message_chunk`, `agent_thought_chunk`, `tool_call`, `tool_call_update`, `plan`,
`usage_update`, `current_mode_update`, `config_option_update`, `session_info_update`) and asks the
window to answer `session/request_permission` and `fs/read_text_file` or `fs/write_text_file`.
Updates are routed to the thread that owns the session. Stop sends `session/cancel`. Mode, model
and thinking changes use `session/set_mode` and `session/set_config_option`. Trust, sign-out,
rename and delete use Vibe's extension methods `_trust/decision`, `_auth/signOut`,
`_session/set_title` and `_session/delete`.

### Orchestrators and the Chief of Staff

An orchestrator's session is created with one extra MCP server: a small Streamable HTTP MCP server
that Vibe UI hosts on `127.0.0.1` (`/mcp/{threadId}`). Vibe connects to it like any other MCP
server, so the model sees the delegation tools next to its own. `delegate_task` creates a child
thread in the project (optionally in a new worktree under the app data folder, on a `vibe/<slug>`
branch), opens a session for it and sends the prompt; `wait_for_task` blocks until that thread's
turn ends and returns the final reply. Children inherit the orchestrator's mode and model unless the
tool call names another model. Deleting an orchestrator deletes its tasks and removes their
worktrees when they are clean.

The Chief of Staff is the same mechanism with two differences. Its `delegate_task` takes a
`project` (name or path) because it spans projects, and its tasks brief it back instead of being
waited on: Vibe UI composes a short report from the task's transcript (status, tool calls, files
touched, latest reply) every three minutes while the task runs and once when it finishes, and sends
it to the chief as a `[Briefing]` prompt. Briefings queue while the chief is busy and are delivered
together when its turn ends. The chief runs in a `chief-of-staff` folder inside the app data
directory, so it has no code of its own to edit.

### Where things are stored

`state.json` (projects, threads, the chief, selection, preferences) and one transcript file per
thread under `threads/`, in the per-user application data folder (`~/.config/vibe-ui` on macOS and
Linux, `%APPDATA%\vibe-ui` on Windows). Worktrees go under `worktrees/` next to them. Sessions
themselves stay in Vibe's own history.

### Actions file

```jsonc
// vibe.re, in the project root
{
  "actions": [
    { "name": "restore", "command": "dotnet restore", "runOnWorktreeCreate": true },
    { "name": "build",   "command": "make build" },
    { "name": "test",    "command": "make test" },
    { "name": "run",     "command": "make run", "tool": false }   // people only, not agents
  ]
}
```

## Develop

```sh
make build
make test
VIBE_UI_INTEGRATION=1 dotnet test VibeUi.Desktop.Tests   # also talks to your installed, signed-in vibe-acp
VIBE_UI_SNAPSHOT_DIR=/tmp/shots dotnet test VibeUi.Desktop.Tests   # writes PNG screenshots of the window
```

Tests cover the JSON-RPC framing, the ACP client against a scripted in-process agent, launcher
resolution, the shell and threads against a fake agent (sign-in, resume after restart, permissions,
an orchestrator driven over real HTTP MCP calls, and the chief's delegation and briefings), git
changes and worktrees in a temporary repository, and the window in Avalonia's headless mode.

## Not yet

Listing Vibe sessions that were never opened in Vibe UI (`session/list`), terminal streaming
(`terminal/*`), images in prompts, MCP server management from the window, and a packaged installer. Vibe's CLI (`vibe`) still covers all of that.
