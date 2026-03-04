#if TOOLS
using Godot;
using System;
using System.Threading;
using GitHub.Copilot.SDK;

[Tool]
public partial class CopilotChatPanel : Control
{
    private RichTextLabel _history;
    private LineEdit _input;
    private Button _sendButton;

    private CopilotClient _client;
    private CopilotSession _session;
    private CancellationTokenSource _cts;

    public override void _Ready()
    {
        _cts = new CancellationTokenSource();

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

        _ = InitCopilot(_cts.Token);
    }

    private async System.Threading.Tasks.Task InitCopilot(CancellationToken token)
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
            }, token);

            AppendMessage("🟢 Copilot подключён!", "#00ff88");
        }
        catch (OperationCanceledException)
        {
            // Editor is closing, normal shutdown
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
                new MessageOptions { Prompt = text }, null, _cts.Token
            );
            var bbcode = _history.Text;
            var typingTag = "[color=#aaaaaa]Copilot печатает...[/color]\n\n";
            var idx = bbcode.LastIndexOf(typingTag, StringComparison.Ordinal);
            if (idx >= 0)
                _history.Text = bbcode.Remove(idx, typingTag.Length);
            AppendMessage("Copilot: " + response?.Data.Content, "#ffffff");
        }
        catch (OperationCanceledException)
        {
            // Editor is closing, normal shutdown
        }
        catch (Exception e)
        {
            AppendMessage("Ошибка: " + e.Message, "#ff4444");
        }
        finally
        {
            if (IsInsideTree())
            {
                _input.Editable = true;
                _sendButton.Disabled = false;
                _input.GrabFocus();
            }
        }
    }

    private void AppendMessage(string text, string color = "#ffffff")
    {
        _history.AppendText($"[color={color}]{text}[/color]\n\n");
    }

    public override void _ExitTree()
    {
        // 1. Cancel all in-flight async operations so continuations don't run on a dead node
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;

        // 2. Force-stop the SDK subprocess (non-blocking)
        var client = _client;
        _client = null;
        _session = null;
        if (client != null)
            _ = System.Threading.Tasks.Task.Run(() => client.ForceStopAsync());

        base._ExitTree();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
    }
}
#endif
