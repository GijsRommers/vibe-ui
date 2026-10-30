{
  // Actions for this project. Each one runs from this directory and is offered to the model as a
  // `project__{name}` tool unless `"tool": false`. `runOnWorktreeCreate` actions set up new worktrees.
  "actions": [
    {
      "name": "restore",
      "command": "dotnet restore vibe-ui.slnx",
      "description": "Restore NuGet packages.",
      "runOnWorktreeCreate": true
    },
    {
      "name": "build",
      "command": "make build",
      "description": "Build the whole solution."
    },
    {
      "name": "test",
      "command": "make test",
      "description": "Run every test project."
    },
    {
      "name": "run",
      "command": "make run",
      "description": "Run the desktop app from source.",
      "tool": false
    }
  ]
}
