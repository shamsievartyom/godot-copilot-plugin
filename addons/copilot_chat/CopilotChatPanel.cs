#if TOOLS
using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using GitHub.Copilot.SDK;

[Tool]
public partial class CopilotChatPanel : Control
{
    // ── UI ──────────────────────────────────────────────────────────────────
    private RichTextLabel _history;
    private LineEdit _input;
    private Button _sendButton;
    private OptionButton _chatSelector;
    private Button _newChatButton;
    private Button _deleteChatButton;

    // ── Copilot ─────────────────────────────────────────────────────────────
    private CopilotClient _client;
    private CopilotSession _session;
    private CancellationTokenSource _cts;
    private string _bbcode = "";

    // ── Config ──────────────────────────────────────────────────────────────
    private ChatConfig _config;
    private string _projectKey;
    private bool _suppressChatSwitch;
    private bool _chatHasMessages;
    private const string ConfigFile  = "res://addons/copilot_chat/data/config.json";
    private const string CopilotModel = "gpt-5-mini";

    // ────────────────────────────────────────────────────────────────────────
    //  Config data model
    // ────────────────────────────────────────────────────────────────────────

    private class ChatEntry
    {
        [JsonPropertyName("id")]    public string Id      { get; set; }
        [JsonPropertyName("name")]  public string Name    { get; set; }
        [JsonPropertyName("ts")]    public long   Created { get; set; }
    }

    private class ProjectChats
    {
        [JsonPropertyName("lastId")] public string LastChatId { get; set; }
        [JsonPropertyName("chats")]  public List<ChatEntry> Chats { get; set; } = new();
    }

    private class ChatConfig
    {
        [JsonPropertyName("projects")]
        public Dictionary<string, ProjectChats> Projects { get; set; } = new();
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Godot lifecycle
    // ────────────────────────────────────────────────────────────────────────

    public override void _Ready()
    {
        _cts        = new CancellationTokenSource();
        _projectKey = ProjectSettings.GlobalizePath("res://").TrimEnd('/', '\\');

        CustomMinimumSize = new Vector2(250, 400);

        LoadConfig();
        BuildUI();             // build widgets
        PopulateChatSelector();// fill dropdown (before connecting ItemSelected)

        // Connect chat-switching signal after initial population to avoid
        // spurious switch during startup.
        _chatSelector.ItemSelected += OnChatSelected;

        _ = InitCopilot(_cts.Token);
    }

    public override void _ExitTree()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;

        var client = _client;
        _client  = null;
        _session = null;
        if (client != null)
            _ = System.Threading.Tasks.Task.Run(() => client.ForceStopAsync());

        base._ExitTree();
    }

    protected override void Dispose(bool disposing) => base.Dispose(disposing);

    // ────────────────────────────────────────────────────────────────────────
    //  UI building
    // ────────────────────────────────────────────────────────────────────────

    private void BuildUI()
    {
        var vbox = new VBoxContainer();
        vbox.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(vbox);

        // ── Top bar ──
        var topBar = new HBoxContainer();
        vbox.AddChild(topBar);

        _chatSelector = new OptionButton();
        _chatSelector.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        topBar.AddChild(_chatSelector);

        _newChatButton = new Button { Text = "+", TooltipText = "Новый чат" };
        topBar.AddChild(_newChatButton);

        _deleteChatButton = new Button { Text = "✕", TooltipText = "Удалить чат" };
        topBar.AddChild(_deleteChatButton);

        // ── History ──
        _history = new RichTextLabel();
        _history.BbcodeEnabled      = true;
        _history.ScrollFollowing    = true;
        _history.SizeFlagsVertical  = SizeFlags.ExpandFill;
        vbox.AddChild(_history);

        // ── Input ──
        var hbox = new HBoxContainer();
        vbox.AddChild(hbox);

        _input = new LineEdit();
        _input.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _input.PlaceholderText     = "Напишите сообщение...";
        hbox.AddChild(_input);

        _sendButton = new Button { Text = "➤" };
        hbox.AddChild(_sendButton);

        _sendButton.Pressed      += OnSendPressed;
        _input.TextSubmitted     += _ => OnSendPressed();
        _newChatButton.Pressed   += OnNewChatPressed;
        _deleteChatButton.Pressed += OnDeleteChatPressed;
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Config persistence
    // ────────────────────────────────────────────────────────────────────────

    private void LoadConfig()
    {
        var path = ProjectSettings.GlobalizePath(ConfigFile);
        try
        {
            if (File.Exists(path))
                _config = JsonSerializer.Deserialize<ChatConfig>(File.ReadAllText(path)) ?? new ChatConfig();
            else
                _config = new ChatConfig();
        }
        catch { _config = new ChatConfig(); }
    }

    private void SaveConfig()
    {
        try
        {
            var path = ProjectSettings.GlobalizePath(ConfigFile);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, JsonSerializer.Serialize(_config, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) { GD.PrintErr("Copilot: config save failed: " + e.Message); }
    }

    private ProjectChats GetOrCreateProjectChats()
    {
        if (!_config.Projects.TryGetValue(_projectKey, out var pc))
        {
            pc = new ProjectChats();
            _config.Projects[_projectKey] = pc;
        }
        return pc;
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Chat selector helpers
    // ────────────────────────────────────────────────────────────────────────

    private void PopulateChatSelector()
    {
        _suppressChatSwitch = true;

        _chatSelector.Clear();
        var pc = GetOrCreateProjectChats();

        if (pc.Chats.Count == 0)
        {
            var entry = MakeNewEntry(pc);
            pc.Chats.Add(entry);
            pc.LastChatId = entry.Id;
            SaveConfig();
        }

        foreach (var c in pc.Chats)
            _chatSelector.AddItem(c.Name);

        var idx = pc.Chats.FindIndex(c => c.Id == pc.LastChatId);
        _chatSelector.Select(idx >= 0 ? idx : 0);

        _suppressChatSwitch = false;
    }

    private ChatEntry MakeNewEntry(ProjectChats pc)
    {
        var ts    = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var label = Path.GetFileName(_projectKey.TrimEnd('/', '\\'));
        var safe  = Regex.Replace(label, @"[^a-zA-Z0-9]", "-").ToLowerInvariant().Trim('-');
        return new ChatEntry
        {
            Id      = $"godot-{safe}-{ts}",
            Name    = "Новый чат",
            Created = ts
        };
    }

    private ChatEntry CurrentChat()
    {
        var pc  = GetOrCreateProjectChats();
        var idx = _chatSelector.Selected;
        return (idx >= 0 && idx < pc.Chats.Count) ? pc.Chats[idx] : null;
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Chat switching / new / delete
    // ────────────────────────────────────────────────────────────────────────

    private void OnChatSelected(long index)
    {
        if (_suppressChatSwitch) return;
        _ = SwitchToChat((int)index, _cts.Token);
    }

    private async System.Threading.Tasks.Task SwitchToChat(int index, CancellationToken token)
    {
        var currentIdx = _chatSelector.Selected;
        if (currentIdx != index)
        {
            int dropped = DropCurrentIfEmpty();
            if (dropped > 0 && index > currentIdx)
                index--;
        }

        var pc = GetOrCreateProjectChats();
        if (index < 0 || index >= pc.Chats.Count) return;

        pc.LastChatId = pc.Chats[index].Id;
        SaveConfig();

        _bbcode  = "";
        _history.Text = "";
        _session = null;
        SetInputEnabled(false);

        await LoadOrCreateSession(pc.Chats[index].Id, token);
    }

    private void OnNewChatPressed()
    {
        _ = CreateAndSwitchToNewChat(_cts.Token);
    }

    private async System.Threading.Tasks.Task CreateAndSwitchToNewChat(CancellationToken token)
    {
        DropCurrentIfEmpty();

        var pc    = GetOrCreateProjectChats();
        var entry = MakeNewEntry(pc);
        pc.Chats.Add(entry);
        pc.LastChatId = entry.Id;
        SaveConfig();

        _suppressChatSwitch = true;
        _chatSelector.AddItem(entry.Name);
        _chatSelector.Select(pc.Chats.Count - 1);
        _suppressChatSwitch = false;

        _bbcode  = "";
        _history.Text = "";
        _session = null;
        SetInputEnabled(false);

        await LoadOrCreateSession(entry.Id, token);
    }

    private void OnDeleteChatPressed()
    {
        var pc = GetOrCreateProjectChats();
        if (pc.Chats.Count <= 1)
        {
            AppendMessage("Нельзя удалить единственный чат.", "#ffaa00");
            return;
        }

        var idx  = _chatSelector.Selected;
        if (idx < 0 || idx >= pc.Chats.Count) return;

        var deletedId = pc.Chats[idx].Id;
        pc.Chats.RemoveAt(idx);

        _ = TryDeleteSdkSession(deletedId);

        var newIdx = Math.Min(idx, pc.Chats.Count - 1);
        pc.LastChatId = pc.Chats[newIdx].Id;
        SaveConfig();

        _suppressChatSwitch = true;
        _chatSelector.Clear();
        foreach (var c in pc.Chats)
            _chatSelector.AddItem(c.Name);
        _chatSelector.Select(newIdx);
        _suppressChatSwitch = false;

        _ = SwitchToChat(newIdx, _cts.Token);
    }

    // Returns 1 if the current chat was removed (so callers can adjust target indices).
    private int DropCurrentIfEmpty()
    {
        if (_chatHasMessages) return 0;
        var pc  = GetOrCreateProjectChats();
        var idx = _chatSelector.Selected;
        if (idx < 0 || idx >= pc.Chats.Count) return 0;

        var deletedId = pc.Chats[idx].Id;
        pc.Chats.RemoveAt(idx);
        _ = TryDeleteSdkSession(deletedId);

        _suppressChatSwitch = true;
        _chatSelector.RemoveItem(idx);
        _suppressChatSwitch = false;

        return 1;
    }

    private async System.Threading.Tasks.Task TryDeleteSdkSession(string sessionId)
    {
        try { if (_client != null) await _client.DeleteSessionAsync(sessionId); }
        catch { /* best-effort */ }
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Copilot init & session management
    // ────────────────────────────────────────────────────────────────────────

    private async System.Threading.Tasks.Task InitCopilot(CancellationToken token)
    {
        try
        {
            _client = new CopilotClient(new CopilotClientOptions { UseStdio = true });

            var chat = CurrentChat();
            await LoadOrCreateSession(chat?.Id, token);
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            AppendMessage("🔴 Ошибка запуска: " + e.Message, "#ff4444");
        }
    }

    private async System.Threading.Tasks.Task LoadOrCreateSession(string sessionId, CancellationToken token)
    {
        if (_client == null) return;

        try
        {
            AppendMessage("⌛ Подключение...", "#888888");

            CopilotSession session = null;

            // Try to resume an existing persistent session first.
            if (!string.IsNullOrEmpty(sessionId))
            {
                try
                {
                    session = await _client.ResumeSessionAsync(sessionId, new ResumeSessionConfig
                    {
                        Model               = CopilotModel,
                        OnPermissionRequest = PermissionHandler.ApproveAll
                    }, token);
                }
                catch (OperationCanceledException) { throw; }
                catch
                {
                    // Session not found on disk – will create below.
                    session = null;
                }
            }

            // Create a fresh session (with the same ID so it becomes persistent).
            if (session == null)
            {
                session = await _client.CreateSessionAsync(new SessionConfig
                {
                    SessionId           = sessionId,
                    Model               = CopilotModel,
                    OnPermissionRequest = PermissionHandler.ApproveAll
                }, token);
            }

            _session = session;

            // Replay stored conversation history.
            _bbcode = "";
            _history.Text = "";
            _chatHasMessages = false;
            await LoadHistory(session);

            _bbcode = _bbcode.Replace("[color=#888888]⌛ Подключение...[/color]\n\n", "");
            _history.Text = _bbcode;
            SetInputEnabled(true);
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            AppendMessage("🔴 Ошибка: " + e.Message, "#ff4444");
        }
    }

    private async System.Threading.Tasks.Task LoadHistory(CopilotSession session)
    {
        try
        {
            var messages = await session.GetMessagesAsync();
            foreach (var evt in messages)
            {
                switch (evt)
                {
                    case UserMessageEvent u:
                        AppendMessage(u.Data.Content, "#88ccff");
                        _chatHasMessages = true;
                        break;
                    case AssistantMessageEvent a:
                        AppendMessage(a.Data.Content, "#ffffff");
                        break;
                }
            }
        }
        catch { /* History unavailable – start fresh. */ }
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Message sending
    // ────────────────────────────────────────────────────────────────────────

    private async void OnSendPressed()
    {
        var text = _input.Text.Trim();
        if (string.IsNullOrEmpty(text)) return;

        if (_session == null)
        {
            AppendMessage("⏳ Copilot ещё не подключён, подождите...", "#ffaa00");
            return;
        }

        _input.Clear();
        SetInputEnabled(false);
        bool isFirstMessage = !_chatHasMessages;

        AppendMessage(text, "#88ccff");
        AppendMessage("Copilot печатает...", "#aaaaaa");

        if (isFirstMessage)
        {
            var preview = text.Length > 50 ? text[..50].TrimEnd() + "…" : text;
            RenameCurrentChat(preview);
            _ = GenerateAndApplyTitle(text, _cts.Token);
        }

        try
        {
            var response = await _session.SendAndWaitAsync(
                new MessageOptions { Prompt = text }, null, _cts.Token);

            _bbcode = _bbcode.Replace("[color=#aaaaaa]Copilot печатает...[/color]\n\n", "");
            _history.Text = _bbcode;
            AppendMessage(response?.Data.Content, "#ffffff");
            _chatHasMessages = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            AppendMessage("Ошибка: " + e.Message, "#ff4444");
        }
        finally
        {
            if (IsInsideTree())
            {
                SetInputEnabled(true);
                _input.GrabFocus();
            }
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Helpers
    // ────────────────────────────────────────────────────────────────────────

    private async System.Threading.Tasks.Task GenerateAndApplyTitle(string firstMessage, CancellationToken token)
    {
        try
        {
            var titleSession = await _client.CreateSessionAsync(new SessionConfig
            {
                Model               = CopilotModel,
                OnPermissionRequest = PermissionHandler.ApproveAll
            }, token);

            var response = await titleSession.SendAndWaitAsync(new MessageOptions
            {
                Prompt =
                    "You are an expert in crafting pithy titles for chatbot conversations.\n\n" +
                    "Please write a brief title for the following request:\n\n" +
                    firstMessage + "\n\n" +
                    "The title should not be wrapped in quotes. It should be about 8 words or fewer."
            }, null, token);

            var title = response?.Data.Content?.Trim();
            if (string.IsNullOrEmpty(title)) return;

            // Strip surrounding quotes if the model added them anyway
            if (System.Text.RegularExpressions.Regex.IsMatch(title, "^\".*\"$"))
                title = title[1..^1].Trim();

            // Ignore refusals
            if (title.Contains("can't assist with that", StringComparison.OrdinalIgnoreCase)) return;

            RenameCurrentChat(title);
        }
        catch { /* best-effort, title stays as "Новый чат" */ }
    }

    private void RenameCurrentChat(string title)
    {
        var pc  = GetOrCreateProjectChats();
        var idx = _chatSelector.Selected;
        if (idx < 0 || idx >= pc.Chats.Count) return;

        pc.Chats[idx].Name = title;
        SaveConfig();

        _suppressChatSwitch = true;
        _chatSelector.SetItemText(idx, title);
        _suppressChatSwitch = false;
    }

    private void AppendMessage(string text, string color = "#ffffff")
    {
        _bbcode      += $"[color={color}]{text}[/color]\n\n";
        _history.Text = _bbcode;
    }

    private void SetInputEnabled(bool enabled)
    {
        _input.Editable       = enabled;
        _sendButton.Disabled  = !enabled;
    }
}
#endif
