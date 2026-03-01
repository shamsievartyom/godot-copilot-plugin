# 🤖 Godot Copilot Plugin

A **GitHub Copilot chat plugin** for the Godot 4 editor, built with C# and the official [GitHub Copilot SDK](https://www.nuget.org/packages/GitHub.Copilot.SDK).

Chat with GitHub Copilot directly inside the Godot editor — ask questions about your code, get help with GDScript, C#, game design, and more — without ever leaving the editor.

![Godot](https://img.shields.io/badge/Godot-4.x-blue?logo=godot-engine)
![C#](https://img.shields.io/badge/C%23-.NET-purple?logo=csharp)
![GitHub Copilot](https://img.shields.io/badge/GitHub-Copilot-black?logo=github)

---

## ✨ Features

- 💬 Chat with GitHub Copilot directly inside the Godot editor
- 🎨 Built-in dock panel — no external windows needed
- ⚡ Powered by `GPT-4.1` model via the official Copilot SDK
- 🔌 Connects to the local Copilot CLI server

---

## 📋 Requirements

- [Godot 4.x](https://godotengine.org/) with **Mono / .NET** support
- [GitHub CLI](https://cli.github.com/) installed and authenticated
- GitHub Copilot subscription
- .NET SDK

---

## 🚀 Installation

### 1. Clone the repository

```bash
git clone https://github.com/shamsievartyom/godot-copilot-plugin.git
cd godot-copilot-plugin
```

### 2. Open in Godot

Open the project folder in **Godot 4** (Mono version).

### 3. Restore NuGet packages

The project uses `GitHub.Copilot.SDK`. Godot will restore it automatically on first build, or run manually:

```bash
dotnet restore
```

### 4. Enable the plugin

In Godot, go to:

**Project → Project Settings → Plugins → Copilot Chat → Enable**

---

## ▶️ Usage

### Step 1 — Start the Copilot CLI server

Before opening Godot, run this in a terminal and **keep it running**:

```bash
gh copilot --headless
```

You will see:
```
CLI server listening on port XXXXX
```

> **Note:** The port changes every time. Currently the port is hardcoded in `CopilotChatPanel.cs` — see [Known Issues](#-known-issues).

### Step 2 — Open Godot

Open the project in Godot. The **Copilot Chat** dock panel will appear on the right side of the editor (next to Inspector / Signals tabs).

### Step 3 — Chat!

- Type your message in the input field at the bottom
- Press **Enter** or click **➤** to send
- Wait for the response from Copilot

---

## 📁 Project Structure

```
godot-copilot-plugin/
├── addons/
│   └── copilot_chat/
│       ├── plugin.cfg            # Plugin metadata
│       ├── CopilotChatPlugin.cs  # EditorPlugin — registers the dock
│       └── CopilotChatPanel.cs   # Chat UI + Copilot SDK integration
├── TestNode.cs                   # Test script (SDK connection test)
├── project.godot                 # Godot project file
└── copilot-plugin.csproj         # .NET project file
```

---

## ⚠️ Known Issues

- **Hardcoded port** — The CLI server port (`59707`) is hardcoded in `CopilotChatPanel.cs`. If `gh copilot --headless` starts on a different port, update it manually:
  ```csharp
  CliUrl = "localhost:YOUR_PORT"
  ```
- **CLI must be running** — The plugin will show a connection error if `gh copilot --headless` is not running before opening Godot.

---

## 🗺️ Roadmap

- [ ] Auto-detect CLI server port
- [ ] Auto-start CLI server from the plugin
- [ ] Persistent chat history
- [ ] Streaming responses (token by token)
- [ ] Code context awareness (send selected code to Copilot)
- [ ] Multiple chat sessions

---

## 📄 License

MIT License — feel free to use, modify, and distribute.