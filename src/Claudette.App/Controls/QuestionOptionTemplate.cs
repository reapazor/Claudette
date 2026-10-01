using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Claudette.App.Conversation;

namespace Claudette.App.Controls;

/// <summary>
/// Shows an option of a clarifying question as a radio button, or as a check box when the question takes several
/// answers (DESIGN.md §5). Only that one control is made: a hidden radio button still belongs to its group, and
/// checking it with its box would uncheck the others' radio buttons and with them their boxes.
/// </summary>
public sealed class QuestionOptionTemplate : IDataTemplate
{
    /// <summary>The template for an option of a question with one answer.</summary>
    public IDataTemplate? Single { get; set; }

    /// <summary>The template for an option of a question with any number of answers.</summary>
    public IDataTemplate? Multiple { get; set; }

    public Control? Build(object? param) => (param is QuestionOption { MultiSelect: true } ? Multiple : Single)?.Build(param);

    public bool Match(object? data) => data is QuestionOption;
}
