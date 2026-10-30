# Vibe UI

A desktop window for [Mistral Vibe](https://github.com/mistralai/mistral-vibe). Vibe UI does not
re-implement the agent: it starts Vibe's own `vibe-acp` program for the folder you pick and talks to
it over the [Agent Client Protocol](https://agentclientprotocol.com) (ACP), the same way Zed,
JetBrains and Xcode do. Mistral Vibe keeps owning your credentials, sessions, tools, modes, MCP
servers, skills and configuration in `~/.vibe`.

Vibe UI is the GUI half of the earlier VibeRe project. The server, CLI, web and mobile pieces live on
in [Vibe-Re](https://github.com/GijsRommers/Vibe-Re).

## What you get

- A chat transcript with streamed replies, optional model thoughts, tool calls with their output,
  file locations and diffs, and Vibe's plan.
- Vibe's permission prompts as buttons (allow once, allow for the session, always, deny), driven by
  the session mode you choose: Ask, Plan, Accept Edits or Auto Approve.
- Model and mode selectors fed by Vibe, token usage and cost per session, stop, new session.
- Sign in through Mistral AI Studio in your browser, and sign out. Vibe stores the resulting key.
- Workspace trust: an untrusted folder gets a banner with a one-click "Trust this folder".

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
`uv tool install` put it. `VIBE_ACP_EXECUTABLE` points it at a specific executable.

## How it works

```
VibeUi.Desktop  Avalonia window and view models
VibeUi.Acp      ACP client: JSON-RPC 2.0 over the agent's stdin/stdout, typed requests and updates
vibe-acp        Mistral Vibe's ACP entry point, started as a child process in the chosen folder
```

One agent process per window. Choosing another folder restarts it there, so Vibe picks up that
folder's `.vibe/` configuration and `AGENTS.md`.

The flow is `initialize` (announcing file-system support and the `browser-auth-delegated` meta flag
so Vibe hands the sign-in URL to the window instead of opening a browser itself), then
`session/new`. If that fails with ACP error `-32000`, the window shows the Mistral login button:
`authenticate` with `action: start` returns the sign-in URL, and `action: complete` waits for the
browser flow and stores the key. Each prompt is `session/prompt`; Vibe streams `session/update`
notifications (`agent_message_chunk`, `agent_thought_chunk`, `tool_call`, `tool_call_update`,
`plan`, `usage_update`, `current_mode_update`, `config_option_update`, `session_info_update`) and
asks the window to answer `session/request_permission` and `fs/read_text_file` or
`fs/write_text_file`. Stop sends `session/cancel`. Mode and model changes use `session/set_mode`
and `session/set_config_option`. Trust and sign-out use Vibe's extension methods
`_trust/decision` and `_auth/signOut`.

## Develop

```sh
make build
make test
VIBE_UI_INTEGRATION=1 dotnet test VibeUi.Acp.Tests   # also talks to your installed vibe-acp
```

Tests cover the JSON-RPC framing, the ACP client against a scripted in-process agent, launcher
resolution, the view model against a fake agent, and the window in Avalonia's headless mode.

## Not yet

Resuming or listing earlier Vibe sessions (`session/load`, `session/list`), terminal streaming
(`terminal/*`), images in prompts, MCP server management from the window, and a packaged installer.
Vibe's CLI (`vibe`) still covers all of that.
