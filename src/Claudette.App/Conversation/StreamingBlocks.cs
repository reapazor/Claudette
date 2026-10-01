using System.Collections.ObjectModel;

namespace Claudette.App.Conversation;

/// <summary>
/// The reply text and thinking a conversation is streaming (DESIGN.md §5): each delta adds to the open row of its kind,
/// until the row is closed by the end of its block or by anything else joining the conversation. Claude Code streams a
/// block's deltas, then sends the complete block in an assistant message; if deltas arrived since the last assistant
/// message, its text has already been shown.
/// </summary>
/// <param name="stamp">Gives a new reply row the time it was sent.</param>
/// <param name="expandThinking">Whether a new thinking row starts expanded.</param>
internal sealed class StreamingBlocks(ObservableCollection<ConversationItem> items, Action<MessageItem> stamp, Func<bool> expandThinking)
{
    private AssistantTextItem? _text;
    private ThinkingItem? _thinking;
    private bool _textDeltasSinceAssistant;
    private bool _thinkingDeltasSinceAssistant;

    public void OnTextDelta(string text)
    {
        _textDeltasSinceAssistant = true;
        AppendText(text);
    }

    public void OnThinkingDelta(string text)
    {
        _thinkingDeltasSinceAssistant = true;
        AppendThinking(text);
    }

    /// <summary>Whether text and thinking deltas arrived since the last assistant message; an assistant message takes them.</summary>
    public (bool Text, bool Thinking) TakeStreamed()
    {
        var streamed = (_textDeltasSinceAssistant, _thinkingDeltasSinceAssistant);
        _textDeltasSinceAssistant = false;
        _thinkingDeltasSinceAssistant = false;
        return streamed;
    }

    public void AppendText(string text)
    {
        CloseThinking();
        if (_text is null)
        {
            _text = new AssistantTextItem();
            stamp(_text);
            items.Add(_text);
        }
        _text.Append(text);
    }

    public void AppendThinking(string text)
    {
        if (text.Length == 0 && _thinking is not null)
        {
            return;
        }
        CloseText();
        if (_thinking is null)
        {
            _thinking = new ThinkingItem { IsExpanded = expandThinking() };
            items.Add(_thinking);
        }
        _thinking.Append(text);
    }

    public void CloseText()
    {
        if (_text is not null)
        {
            _text.IsStreaming = false;
            _text = null;
        }
    }

    public void CloseThinking()
    {
        if (_thinking is not null)
        {
            _thinking.IsStreaming = false;
            _thinking = null;
        }
    }

    /// <summary>Closes both rows: something else joins the conversation after them.</summary>
    public void Close()
    {
        CloseText();
        CloseThinking();
    }

    /// <summary>Forgets the rows of a cleared conversation.</summary>
    public void Clear()
    {
        _text = null;
        _thinking = null;
    }
}
