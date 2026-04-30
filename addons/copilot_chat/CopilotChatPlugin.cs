#if TOOLS
using Godot;

[Tool]
public partial class CopilotChatPlugin : EditorPlugin
{
    private CopilotChatPanel _panel;

    public override void _EnterTree()
    {
        _panel = new CopilotChatPanel(this);
        AddControlToDock(DockSlot.RightUl, _panel);
    }

    public override void _ExitTree()
    {
        if (_panel != null)
        {
            RemoveControlFromDocks(_panel);
            _panel.QueueFree();
        }
    }
}
#endif