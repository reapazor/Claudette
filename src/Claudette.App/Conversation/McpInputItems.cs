using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Claudette.Core.Protocol;
using Claudette.Core.Sessions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.Conversation;

/// <summary>What kind of value a field of an MCP server's form takes.</summary>
public enum McpFieldKind
{
    Text,
    Number,
    Integer,
    Boolean,
    Choice,
}

/// <summary>One choice of a <see cref="McpFieldKind.Choice"/> field: the value sent, and what's shown.</summary>
public sealed record McpChoice(string Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// A field of an MCP server's form (DESIGN.md §7, "MCP servers asking for input"), from one property of its JSON
/// Schema: the MCP spec keeps them flat, with a string, number, integer, boolean or enum type.
/// </summary>
public sealed partial class McpField : ObservableObject
{
    public McpField(string name, JsonObject schema, bool required)
    {
        Name = name;
        Label = schema.GetString("title") is { Length: > 0 } title ? title : name;
        Description = schema.GetString("description");
        Required = required;
        var choices = schema.GetArray("enum")?.Select(v => v?.ToString()).OfType<string>().ToArray();
        var names = schema.GetArray("enumNames")?.Select(v => v?.ToString()).ToArray();
        var oneOf = schema.GetArray("oneOf")?.OfType<JsonObject>().Where(o => o["const"] is not null).ToArray();
        if (choices is { Length: > 0 })
        {
            Kind = McpFieldKind.Choice;
            Choices = [.. choices.Select((value, i) => new McpChoice(value, names is not null && i < names.Length && names[i] is { } shown ? shown : value))];
        }
        else if (oneOf is { Length: > 0 })
        {
            Kind = McpFieldKind.Choice;
            Choices = [.. oneOf.Select(o => new McpChoice(o["const"]!.ToString(), o.GetString("title") ?? o["const"]!.ToString()))];
        }
        else
        {
            Kind = schema.GetString("type") switch
            {
                "number" => McpFieldKind.Number,
                "integer" => McpFieldKind.Integer,
                "boolean" => McpFieldKind.Boolean,
                _ => McpFieldKind.Text,
            };
        }
        Minimum = schema.GetDouble("minimum");
        Maximum = schema.GetDouble("maximum");
        MinLength = schema.GetDouble("minLength") is { } min ? (int)min : null;
        MaxLength = schema.GetDouble("maxLength") is { } max ? (int)max : null;
        var initial = schema["default"];
        if (Kind == McpFieldKind.Boolean)
        {
            IsChecked = initial?.GetValueKind() == JsonValueKind.True;
        }
        else if (Kind == McpFieldKind.Choice)
        {
            Selected = Choices.FirstOrDefault(c => c.Value == initial?.ToString());
        }
        else
        {
            Text = initial?.ToString() ?? "";
        }
    }

    public string Name { get; }

    public string Label { get; }

    public string? Description { get; }

    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    public bool Required { get; }

    /// <summary>"Project (required)", over the field.</summary>
    public string Heading => Required ? $"{Label} (required)" : Label;

    public McpFieldKind Kind { get; }

    public IReadOnlyList<McpChoice> Choices { get; } = [];

    public double? Minimum { get; }

    public double? Maximum { get; }

    public int? MinLength { get; }

    public int? MaxLength { get; }

    public bool IsText => Kind is McpFieldKind.Text or McpFieldKind.Number or McpFieldKind.Integer;

    public bool IsBoolean => Kind == McpFieldKind.Boolean;

    public bool IsChoice => Kind == McpFieldKind.Choice;

    [ObservableProperty]
    public partial string Text { get; set; } = "";

    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    [ObservableProperty]
    public partial McpChoice? Selected { get; set; }

    /// <summary>Why the value can't be sent; null when it can.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; set; }

    public bool HasError => Error is not null;

    /// <summary>Checks the value and returns it as the schema wants it; null when it's left empty or wrong (see <see cref="Error"/>).</summary>
    internal JsonNode? Read(out bool ok)
    {
        Error = null;
        ok = true;
        switch (Kind)
        {
            case McpFieldKind.Boolean:
                return IsChecked;
            case McpFieldKind.Choice:
                if (Selected is null)
                {
                    ok = !Required;
                    Error = ok ? null : "Choose one.";
                    return null;
                }
                return Selected.Value;
        }
        var text = Text.Trim();
        if (text.Length == 0)
        {
            ok = !Required;
            Error = ok ? null : "Required.";
            return null;
        }
        switch (Kind)
        {
            case McpFieldKind.Integer when !long.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var whole):
            case McpFieldKind.Number when !double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var number) || !double.IsFinite(number):
                ok = false;
                Error = Kind == McpFieldKind.Integer ? "A whole number." : "A number.";
                return null;
        }
        if (Kind is McpFieldKind.Integer or McpFieldKind.Number)
        {
            var value = double.Parse(text, CultureInfo.CurrentCulture);
            if (value < Minimum || value > Maximum)
            {
                ok = false;
                Error = Minimum is { } low && Maximum is { } high ? $"From {low} to {high}." : Minimum is { } lowest ? $"At least {lowest}." : $"At most {Maximum}.";
                return null;
            }
            return Kind == McpFieldKind.Integer ? JsonValue.Create(long.Parse(text, CultureInfo.CurrentCulture)) : JsonValue.Create(value);
        }
        if (text.Length < MinLength || text.Length > MaxLength)
        {
            ok = false;
            Error = MinLength is { } shortest && MaxLength is { } longest ? $"{shortest} to {longest} characters." : MinLength is { } min ? $"At least {min} characters." : $"At most {MaxLength} characters.";
            return null;
        }
        return text;
    }
}

/// <summary>
/// An MCP server asking the user for input (DESIGN.md §7, "MCP servers asking for input"): a form built from its
/// schema, or a link to finish something in the browser. <b>Send</b> (or <b>Done</b>), <b>Decline</b> or <b>Dismiss</b>
/// answers it. The server's name, message and fields are the server's own: untrusted text.
/// </summary>
public sealed partial class McpInputItem : ConversationItem
{
    private readonly Func<string, Task>? _openUrl;

    public McpInputItem(ElicitationRequest request, Func<string, Task>? openUrl = null)
    {
        Request = request;
        _openUrl = openUrl;
        if (request.Schema?.GetObject("properties") is { } properties)
        {
            var required = request.Schema.GetArray("required")?.Select(r => r?.ToString()).OfType<string>().ToHashSet() ?? [];
            foreach (var (name, schema) in properties)
            {
                if (schema is JsonObject field)
                {
                    Fields.Add(new McpField(name, field, required.Contains(name)));
                }
            }
        }
    }

    public ElicitationRequest Request { get; }

    /// <summary>"tickets asks", or the server's own title when it gives one.</summary>
    public string Title => Request.Title is { Length: > 0 } title ? title : $"{Request.DisplayName ?? Request.ServerName} asks";

    public string ServerName => Request.ServerName;

    public string Message => Request.Message;

    public bool IsUrl => Request.IsUrl;

    public bool IsForm => !Request.IsUrl;

    public string? Url => Request.Url;

    public ObservableCollection<McpField> Fields { get; } = [];

    public bool HasFields => Fields.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOutcome))]
    [NotifyCanExecuteChangedFor(nameof(SendCommand), nameof(DeclineCommand), nameof(DismissCommand))]
    public partial bool IsPending { get; private set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOutcome))]
    public partial string Outcome { get; private set; } = "";

    public bool HasOutcome => !IsPending && Outcome.Length > 0;

    /// <summary>A URL request: the user opened the link, so <b>Done</b> shows.</summary>
    [ObservableProperty]
    public partial bool HasOpenedUrl { get; private set; }

    /// <summary>Raised when the user answers, so the tab can update its "needs input" status.</summary>
    public event EventHandler? Answered;

    [RelayCommand]
    private async Task OpenUrlAsync()
    {
        if (Url is { } url && _openUrl is not null)
        {
            HasOpenedUrl = true;
            await _openUrl(url);
        }
    }

    /// <summary><b>Send</b> a form whose values all check out, or <b>Done</b> for a URL request.</summary>
    [RelayCommand(CanExecute = nameof(IsPending))]
    private void Send()
    {
        if (IsUrl)
        {
            Request.Accept();
            Finish("Done");
            return;
        }
        var content = new JsonObject();
        var ok = true;
        foreach (var field in Fields)
        {
            var value = field.Read(out var fieldOk);
            ok &= fieldOk;
            if (value is not null)
            {
                content[field.Name] = value;
            }
        }
        if (!ok)
        {
            return;
        }
        Request.Accept(content);
        Finish("Sent");
    }

    [RelayCommand(CanExecute = nameof(IsPending))]
    private void Decline()
    {
        Request.Decline();
        Finish("Declined");
    }

    [RelayCommand(CanExecute = nameof(IsPending))]
    private void Dismiss()
    {
        Request.Cancel();
        Finish("Dismissed");
    }

    /// <summary>Claude Code withdrew it, or the session ended.</summary>
    public void Withdraw(string? outcome = null)
    {
        if (IsPending)
        {
            Outcome = outcome ?? PromptItem.WithdrawnOutcome;
            IsPending = false;
        }
    }

    /// <summary>The server says the URL request is done (<c>elicitation_complete</c>).</summary>
    public void Complete()
    {
        if (IsPending)
        {
            Request.Accept();
            Finish("Done");
        }
    }

    private void Finish(string outcome)
    {
        Outcome = outcome;
        IsPending = false;
        Answered?.Invoke(this, EventArgs.Empty);
    }
}
