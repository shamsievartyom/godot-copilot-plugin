#if TOOLS
using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Linq;
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
    private OptionButton _modelSelector;
    private Button _newChatButton;
    private Button _deleteChatButton;
    private TextEdit _systemPromptEdit;
    private Button _systemPromptToggle;
    private PanelContainer _systemPromptPanel;

    // ── Plugin ref ──────────────────────────────────────────────────────────
    private readonly EditorPlugin _editorPlugin;

    // ── Model list ──────────────────────────────────────────────────────────
    private List<string> _modelIds = new();
    private List<ModelInfo> _models = new();
    private bool _suppressModelSwitch;
    private OptionButton _reasoningSelector;
    private bool _suppressReasoningSwitch;
    private Label _quotaLabel;
    private GodotTools _godotTools;

    public CopilotChatPanel(EditorPlugin editorPlugin)
    {
        _editorPlugin = editorPlugin;
    }

    // ── Copilot ─────────────────────────────────────────────────────────────
    private CopilotClient _client;
    private CopilotSession _session;
    private CancellationTokenSource _cts;
    private string _bbcode = "";
    private IDisposable _usageSubscription;

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
        [JsonPropertyName("lastId")]          public string  LastChatId          { get; set; }
        [JsonPropertyName("lastModel")]       public string  LastModel           { get; set; }
        [JsonPropertyName("lastReasoning")]   public string? LastReasoningEffort  { get; set; }
        [JsonPropertyName("modelReasoning")]  public Dictionary<string, string> ModelReasoningEfforts { get; set; } = new();
        [JsonPropertyName("chats")]           public List<ChatEntry> Chats        { get; set; } = new();
        [JsonPropertyName("systemPrompt")]    public string? SystemPrompt         { get; set; }
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
        _godotTools = new GodotTools(_editorPlugin);

        CustomMinimumSize = new Vector2(250, 400);

        LoadConfig();
        BuildUI();             // build widgets
        PopulateChatSelector();// fill dropdown (before connecting ItemSelected)
        LoadSystemPromptUI();

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

        _usageSubscription?.Dispose();
        _usageSubscription = null;

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

        // ── Model bar (model selector+multiplier overlay | reasoning selector) ──
        var modelBar = new HBoxContainer();
        vbox.AddChild(modelBar);

        var modelLabel = new Label { Text = "Model: " };
        modelBar.AddChild(modelLabel);

        // Wrapper so the multiplier label can be overlaid inside the selector
        var modelWrapper = new Control();
        modelWrapper.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        modelBar.AddChild(modelWrapper);

        _modelSelector = new OptionButton();
        _modelSelector.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _modelSelector.Disabled = true; // enabled after models are loaded
        _modelSelector.AddItem("Loading...");
        modelWrapper.AddChild(_modelSelector);

        _modelSelector.ItemSelected += OnModelSelected;

        _reasoningSelector = new OptionButton();
        _reasoningSelector.CustomMinimumSize = new Vector2(90, 0);
        _reasoningSelector.Disabled = true;
        _reasoningSelector.AddItem("—");
        modelBar.AddChild(_reasoningSelector);

        _reasoningSelector.ItemSelected += OnReasoningSelected;

        _quotaLabel = new Label { Text = "" };
        _quotaLabel.CustomMinimumSize = new Vector2(55, 0);
        _quotaLabel.HorizontalAlignment = HorizontalAlignment.Right;
        _quotaLabel.VerticalAlignment = VerticalAlignment.Center;
        modelBar.AddChild(_quotaLabel);

        // ── System prompt bar ──
        var spBar = new HBoxContainer();
        vbox.AddChild(spBar);

        _systemPromptToggle = new Button { Text = "System prompt ▼", Flat = true };
        _systemPromptToggle.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _systemPromptToggle.Alignment = HorizontalAlignment.Left;
        spBar.AddChild(_systemPromptToggle);

        _systemPromptPanel = new PanelContainer();
        _systemPromptPanel.Visible = false;
        vbox.AddChild(_systemPromptPanel);

        _systemPromptEdit = new TextEdit();
        _systemPromptEdit.CustomMinimumSize = new Vector2(0, 80);
        _systemPromptEdit.PlaceholderText   = "Системный промпт (применяется к новым сессиям)";
        _systemPromptEdit.WrapMode          = TextEdit.LineWrappingMode.Boundary;
        _systemPromptPanel.AddChild(_systemPromptEdit);

        _systemPromptToggle.Pressed += () =>
        {
            _systemPromptPanel.Visible = !_systemPromptPanel.Visible;
            _systemPromptToggle.Text = _systemPromptPanel.Visible
                ? "System prompt ▲"
                : "System prompt ▼";
        };

        _systemPromptEdit.FocusExited += OnSystemPromptChanged;

        // ── History ──
        _history = new RichTextLabel();
        _history.BbcodeEnabled      = true;
        _history.ScrollFollowing    = true;
        _history.SelectionEnabled   = true;
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

        _sendButton.Pressed       += OnSendPressed;
        _input.TextSubmitted      += _ => OnSendPressed();
        _newChatButton.Pressed    += OnNewChatPressed;
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

            await PopulateModelSelector(token);
            _ = RefreshQuotaAsync(token);

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
                        Model               = GetCurrentModel(),
                        ReasoningEffort     = GetCurrentReasoningEffort(),
                        Tools               = _godotTools.BuildAIFunctions(),
                        SystemMessage       = GetSystemMessageConfig(),
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
                    Model               = GetCurrentModel(),
                    ReasoningEffort     = GetCurrentReasoningEffort(),
                    Tools               = _godotTools.BuildAIFunctions(),
                    SystemMessage       = GetSystemMessageConfig(),
                    OnPermissionRequest = PermissionHandler.ApproveAll
                }, token);
            }

            _session = session;

            // Subscribe to per-message usage events for real-time quota updates.
            _usageSubscription?.Dispose();
            _usageSubscription = session.On(evt =>
            {
                if (evt is AssistantUsageEvent usage)
                    UpdateQuotaFromUsage(usage.Data);
            });

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
            await using var titleSession = await _client.CreateSessionAsync(new SessionConfig
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
        _input.Editable      = enabled;
        _sendButton.Disabled = !enabled;
        if (_modelSelector != null && _modelIds.Count > 0)
            _modelSelector.Disabled = !enabled;
        if (_reasoningSelector != null)
        {
            if (!enabled)
            {
                _reasoningSelector.Disabled = true;
            }
            else
            {
                var pc = GetOrCreateProjectChats();
                var modelIdx = _modelIds.IndexOf(pc.LastModel ?? CopilotModel);
                bool supportsReasoning = modelIdx >= 0
                    && modelIdx < _models.Count
                    && _models[modelIdx].Capabilities.Supports.ReasoningEffort;
                _reasoningSelector.Disabled = !supportsReasoning;
            }
        }

    }

    // ────────────────────────────────────────────────────────────────────────
    //  System prompt
    // ────────────────────────────────────────────────────────────────────────

    private void LoadSystemPromptUI()
    {
        var pc = GetOrCreateProjectChats();
        if (_systemPromptEdit != null && pc.SystemPrompt != null)
            _systemPromptEdit.Text = pc.SystemPrompt;
    }

    private void OnSystemPromptChanged()
    {
        var text = _systemPromptEdit?.Text ?? "";
        var pc   = GetOrCreateProjectChats();
        pc.SystemPrompt = string.IsNullOrWhiteSpace(text) ? null : text;
        SaveConfig();
    }

    private const string BaseSystemPrompt =
        "You are a Godot 4 assistant embedded in the Godot editor. " +
        "Help the user with their game project.";

    private SystemMessageConfig GetSystemMessageConfig()
    {
        var pc      = GetOrCreateProjectChats();
        var custom  = pc.SystemPrompt;
        var content = string.IsNullOrWhiteSpace(custom)
            ? BaseSystemPrompt
            : BaseSystemPrompt + "\n\n" + custom.Trim();
        return new SystemMessageConfig { Content = content };
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Model selector
    // ────────────────────────────────────────────────────────────────────────

    private string GetCurrentModel()
    {
        var pc = GetOrCreateProjectChats();
        return pc.LastModel ?? CopilotModel;
    }

    private async System.Threading.Tasks.Task PopulateModelSelector(CancellationToken token)
    {
        try
        {
            var allModels = await _client.ListModelsAsync(token);
            if (allModels == null || allModels.Count == 0) return;

            var pc = GetOrCreateProjectChats();
            var savedModel = pc.LastModel ?? CopilotModel;

            _suppressModelSwitch = true;
            _modelSelector.Clear();
            _modelIds.Clear();
            _models.Clear();

            int selectedIdx = 0;
            for (int i = 0; i < allModels.Count; i++)
            {
                var m = allModels[i];
                _modelIds.Add(m.Id);
                _models.Add(m);
                var mult = m.Billing?.Multiplier ?? 0;
                var itemText = mult > 0 ? $"{m.Name ?? m.Id}  ×{mult:G}" : m.Name ?? m.Id;
                _modelSelector.AddItem(itemText);
                if (m.Id == savedModel)
                    selectedIdx = i;
            }

            _modelSelector.Select(selectedIdx);
            _modelSelector.Disabled = false;
            _suppressModelSwitch = false;

            PopulateReasoningSelector(selectedIdx);
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            GD.PrintErr("Copilot: failed to load models: " + e.Message);
            _suppressModelSwitch = false;
        }
    }

    private void PopulateReasoningSelector(int modelIndex)
    {
        if (_reasoningSelector == null) return;

        _suppressReasoningSwitch = true;
        _reasoningSelector.Clear();

        bool supportsReasoning = modelIndex >= 0
            && modelIndex < _models.Count
            && _models[modelIndex].Capabilities.Supports.ReasoningEffort;

        if (!supportsReasoning)
        {
            _reasoningSelector.AddItem("—");
            _reasoningSelector.Disabled = true;
            _suppressReasoningSwitch = false;
            return;
        }

        var m = _models[modelIndex];
        var efforts = m.SupportedReasoningEfforts;
        if (efforts == null || efforts.Count == 0)
        {
            _reasoningSelector.AddItem("—");
            _reasoningSelector.Disabled = true;
            _suppressReasoningSwitch = false;
            return;
        }

        foreach (var effort in efforts)
            _reasoningSelector.AddItem(effort);

        var pc = GetOrCreateProjectChats();
        var modelId = modelIndex >= 0 && modelIndex < _modelIds.Count ? _modelIds[modelIndex] : null;
        pc.ModelReasoningEfforts.TryGetValue(modelId ?? "", out var savedEffort);
        int effortIdx = 0;

        if (!string.IsNullOrEmpty(savedEffort))
        {
            var idx = efforts.IndexOf(savedEffort);
            if (idx >= 0)
            {
                effortIdx = idx;
            }
            else
            {
                // Saved effort not valid for this model — fall back to default
                if (!string.IsNullOrEmpty(m.DefaultReasoningEffort))
                {
                    var defIdx = efforts.IndexOf(m.DefaultReasoningEffort);
                    if (defIdx >= 0) effortIdx = defIdx;
                }
                if (modelId != null) pc.ModelReasoningEfforts[modelId] = efforts[effortIdx];
                SaveConfig();
            }
        }
        else if (!string.IsNullOrEmpty(m.DefaultReasoningEffort))
        {
            var defIdx = efforts.IndexOf(m.DefaultReasoningEffort);
            if (defIdx >= 0) effortIdx = defIdx;
        }

        _reasoningSelector.Select(effortIdx);
        _reasoningSelector.Disabled = false;
        _suppressReasoningSwitch = false;
    }

    private void OnReasoningSelected(long index)
    {
        if (_suppressReasoningSwitch) return;
        var pc = GetOrCreateProjectChats();
        var modelId = pc.LastModel ?? CopilotModel;
        pc.ModelReasoningEfforts[modelId] = _reasoningSelector.GetItemText((int)index);
        SaveConfig();
        _ = ReloadCurrentSessionAsync(_cts.Token);
    }

    private async System.Threading.Tasks.Task ReloadCurrentSessionAsync(CancellationToken token)
    {
        var chat = CurrentChat();
        if (chat == null) return;
        _bbcode = "";
        _history.Text = "";
        _session = null;
        SetInputEnabled(false);
        await LoadOrCreateSession(chat.Id, token);
    }

    private string? GetCurrentReasoningEffort()
    {
        var pc = GetOrCreateProjectChats();
        var modelId = pc.LastModel ?? CopilotModel;
        return pc.ModelReasoningEfforts.TryGetValue(modelId, out var effort) ? effort : null;
    }

    private void OnModelSelected(long index)
    {
        if (_suppressModelSwitch) return;
        _ = SwitchModel((int)index, _cts.Token);
    }

    private async System.Threading.Tasks.Task SwitchModel(int index, CancellationToken token)
    {
        if (index < 0 || index >= _modelIds.Count) return;
        var modelId = _modelIds[index];

        var pc = GetOrCreateProjectChats();
        pc.LastModel = modelId;
        SaveConfig();

        PopulateReasoningSelector(index);
        await ReloadCurrentSessionAsync(token);
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Quota display
    // ────────────────────────────────────────────────────────────────────────

    private void UpdateQuotaFromUsage(AssistantUsageData data)
    {
        if (data?.QuotaSnapshots == null) return;
        if (!data.QuotaSnapshots.TryGetValue("premium_interactions", out var snap)) return;

        string labelText, labelTooltip;
        if (snap.IsUnlimitedEntitlement)
        {
            labelText    = "P: ∞";
            labelTooltip = "Безлимитные премиум запросы";
        }
        else
        {
            // AssistantUsageQuotaSnapshot.RemainingPercentage is 0..100
            var usedPct  = Math.Round(100.0 - snap.RemainingPercentage, 1);
            labelText    = $"P: {usedPct:0.0}%";
            labelTooltip = $"Израсходовано {snap.UsedRequests} из {snap.EntitlementRequests} " +
                           $"премиум запросов (сброс: {snap.ResetDate})";
        }

        Callable.From(() =>
        {
            if (IsInsideTree())
            {
                _quotaLabel.Text        = labelText;
                _quotaLabel.TooltipText = labelTooltip;
            }
        }).CallDeferred();
    }

    private async System.Threading.Tasks.Task RefreshQuotaAsync(CancellationToken token)
    {
        if (_client == null || _quotaLabel == null) return;
        try
        {
            var result = await _client.Rpc.Account.GetQuotaAsync(null, token);
            if (result?.QuotaSnapshots == null) return;

            string labelText = null, labelTooltip = null;

            if (result.QuotaSnapshots.TryGetValue("premium_interactions", out var snap))
            {
                if (snap.IsUnlimitedEntitlement)
                {
                    labelText    = "P: ∞";
                    labelTooltip = "Безлимитные премиум запросы";
                }
                else
                {
                    var usedPct  = Math.Round(100.0 - snap.RemainingPercentage, 1);
                    labelText    = $"P: {usedPct:0.0}%";
                    labelTooltip = $"Израсходовано {snap.UsedRequests} из {snap.EntitlementRequests} " +
                                   $"премиум запросов (сброс: {snap.ResetDate})";
                }
            }

            if (labelText != null)
            {
                Callable.From(() =>
                {
                    if (IsInsideTree())
                    {
                        _quotaLabel.Text        = labelText;
                        _quotaLabel.TooltipText = labelTooltip;
                    }
                }).CallDeferred();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            GD.PrintErr("Copilot quota refresh failed: " + e.Message);
        }
    }

    private void RemoveUnsupportedModel(int index, string fallbackModelId)
    {
        _suppressModelSwitch = true;
        _modelIds.RemoveAt(index);
        _models.RemoveAt(index);
        _modelSelector.RemoveItem(index);

        // Select the fallback model
        var fallbackIdx = _modelIds.IndexOf(fallbackModelId);
        if (fallbackIdx < 0) fallbackIdx = 0;
        _modelSelector.Select(fallbackIdx);

        PopulateReasoningSelector(fallbackIdx);
        _suppressModelSwitch = false;
    }
}
#endif
