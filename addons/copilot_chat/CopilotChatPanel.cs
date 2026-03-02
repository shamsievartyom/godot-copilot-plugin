#if TOOLS
using Godot;
using System;
using GitHub.Copilot.SDK;

[Tool]
public partial class CopilotChatPanel : Control
{
    private RichTextLabel _history;
    private LineEdit _input;
    private Button _sendButton;

    private CopilotClient _client;
    private CopilotSession _session;

    public override void _Ready()
    {
        // Строим UI программно
        CustomMinimumSize = new Vector2(250, 400);

        var vbox = new VBoxContainer();
        vbox.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(vbox);

        _history = new RichTextLabel();
        _history.BbcodeEnabled = true;
        _history.ScrollFollowing = true;
        _history.SizeFlagsVertical = SizeFlags.ExpandFill;
        vbox.AddChild(_history);

        var hbox = new HBoxContainer();
        vbox.AddChild(hbox);

        _input = new LineEdit();
        _input.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _input.PlaceholderText = "Напишите сообщение...";
        hbox.AddChild(_input);

        _sendButton = new Button();
        _sendButton.Text = "➤";
        hbox.AddChild(_sendButton);

        _sendButton.Pressed += OnSendPressed;
        _input.TextSubmitted += _ => OnSendPressed();

        _ = InitCopilot();
    }

    private async System.Threading.Tasks.Task InitCopilot()
    {
        try
        {
            _client = new CopilotClient(new CopilotClientOptions
            {
                UseStdio = true
            });

            _session = await _client.CreateSessionAsync(new SessionConfig
            {
                Model = "gpt-4.1",
                OnPermissionRequest = PermissionHandler.ApproveAll
            });

            AppendMessage("🟢 Copilot подключён!", "#00ff88");
        }
        catch (Exception e)
        {
            AppendMessage("🔴 Ошибка: " + e.Message, "#ff4444");
        }
    }

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
        _input.Editable = false;
        _sendButton.Disabled = true;

        AppendMessage("Вы: " + text, "#88ccff");
        AppendMessage("Copilot печатает...", "#aaaaaa");

        try
        {
            var response = await _session.SendAndWaitAsync(
                new MessageOptions { Prompt = text }
            );
            // Remove the "typing" indicator line and show the real response
            var bbcode = _history.Text;
            var typingTag = "[color=#aaaaaa]Copilot печатает...[/color]\n\n";
            var idx = bbcode.LastIndexOf(typingTag, StringComparison.Ordinal);
            if (idx >= 0)
                _history.Text = bbcode.Remove(idx, typingTag.Length);
            AppendMessage("Copilot: " + response?.Data.Content, "#ffffff");
        }
        catch (Exception e)
        {
            AppendMessage("Ошибка: " + e.Message, "#ff4444");
        }
        finally
        {
            _input.Editable = true;
            _sendButton.Disabled = false;
            _input.GrabFocus();
        }
    }

    private void AppendMessage(string text, string color = "#ffffff")
    {
        _history.AppendText($"[color={color}]{text}[/color]\n\n");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _session?.DisposeAsync().GetAwaiter().GetResult();
            _client?.DisposeAsync().GetAwaiter().GetResult();
        }
        base.Dispose(disposing);
    }
}
#endif