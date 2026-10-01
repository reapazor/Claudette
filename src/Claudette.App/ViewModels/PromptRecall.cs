namespace Claudette.App.ViewModels;

/// <summary>
/// Up and Down in the composer go back through the tab's earlier prompts, as a terminal's history does (DESIGN.md §5,
/// "Composer"). Going back keeps what was being typed, and Down past the newest brings it back. Prompts that repeat the
/// one before are kept once.
/// </summary>
public sealed class PromptRecall
{
    /// <summary>How many prompts a tab remembers.</summary>
    public const int Limit = 200;

    private readonly List<string> _prompts = [];
    private int _position = -1;
    private string _draft = "";

    /// <summary>Going through the history: Down moves forward rather than through the text.</summary>
    public bool IsRecalling => _position >= 0;

    public int Count => _prompts.Count;

    /// <summary>A prompt the tab sent, or one restored from its transcript, oldest first. Ends any recall.</summary>
    public void Add(string prompt)
    {
        var text = prompt.Trim();
        if (text.Length > 0 && (_prompts.Count == 0 || _prompts[^1] != text))
        {
            _prompts.Add(text);
            if (_prompts.Count > Limit)
            {
                _prompts.RemoveAt(0);
            }
        }
        Reset();
    }

    /// <summary>The prompt before the one shown, or null at the oldest. <paramref name="draft"/> is kept for the way back.</summary>
    public string? Older(string draft)
    {
        if (_prompts.Count == 0)
        {
            return null;
        }
        if (_position < 0)
        {
            _draft = draft;
            _position = _prompts.Count;
        }
        if (_position == 0)
        {
            return null;
        }
        _position--;
        return _prompts[_position];
    }

    /// <summary>The prompt after the one shown, then the draft again; null when not recalling.</summary>
    public string? Newer()
    {
        if (_position < 0)
        {
            return null;
        }
        _position++;
        if (_position < _prompts.Count)
        {
            return _prompts[_position];
        }
        var draft = _draft;
        Reset();
        return draft;
    }

    /// <summary>The user typed, or sent: the next Up starts from the newest again.</summary>
    public void Reset()
    {
        _position = -1;
        _draft = "";
    }
}
