using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Xml;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using HttpFreeKit.Core;

namespace HttpFreeKit.Views;

public partial class JsonTextEditor : UserControl
{
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(string), typeof(JsonTextEditor),
            new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnTextPropertyChanged));

    public static readonly DependencyProperty IsJsonProperty =
        DependencyProperty.Register(nameof(IsJson), typeof(bool), typeof(JsonTextEditor),
            new PropertyMetadata(false, OnHighlightingModeChanged));

    public static readonly DependencyProperty IsMarkupProperty =
        DependencyProperty.Register(nameof(IsMarkup), typeof(bool), typeof(JsonTextEditor),
            new PropertyMetadata(false, OnHighlightingModeChanged));

    private static readonly IHighlightingDefinition DarkJsonHighlighting = CreateJsonHighlighting(false);
    private static readonly IHighlightingDefinition LightJsonHighlighting = CreateJsonHighlighting(true);
    private static readonly IHighlightingDefinition MarkupHighlighting = CreateMarkupHighlighting();
    private bool _updatingText;

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public bool IsJson
    {
        get => (bool)GetValue(IsJsonProperty);
        set => SetValue(IsJsonProperty, value);
    }

    public bool IsMarkup
    {
        get => (bool)GetValue(IsMarkupProperty);
        set => SetValue(IsMarkupProperty, value);
    }

    public event EventHandler? TextChanged;

    public JsonTextEditor()
    {
        InitializeComponent();
        Editor.TextChanged += Editor_TextChanged;
        Editor.TextArea.TextEntering += Editor_TextEntering;
        Editor.TextArea.PreviewKeyDown += Editor_KeyDown;
        UpdateHighlighting();
    }

    private static void OnTextPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (JsonTextEditor)d;
        if (!control._updatingText && control.Editor.Text != (string?)e.NewValue)
            control.Editor.Text = (string?)e.NewValue ?? "";
    }

    private static void OnHighlightingModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (JsonTextEditor)d;
        control.UpdateHighlighting();
    }

    private void UpdateHighlighting()
    {
        if (Editor is not null)
            Editor.SyntaxHighlighting = IsJson
                ? App.IsLightTheme ? LightJsonHighlighting : DarkJsonHighlighting
                : IsMarkup ? MarkupHighlighting : null;
    }

    public void RefreshTheme() => UpdateHighlighting();

    private static IHighlightingDefinition CreateJsonHighlighting(bool lightTheme)
    {
        string keyColor = lightTheme ? "#0451A5" : "#9CDCFE";
        string stringColor = lightTheme ? "#A31515" : "#CE9178";
        string definition = $$"""
            <SyntaxDefinition name="JSON" extensions=".json" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
              <Color name="Key" foreground="{{keyColor}}" />
              <Color name="String" foreground="{{stringColor}}" />
              <Color name="Number" foreground="#B5CEA8" />
              <Color name="Keyword" foreground="#569CD6" fontWeight="bold" />
              <Color name="Punctuation" foreground="#D4D4D4" />
              <RuleSet>
                <Rule color="Key">&quot;(?:\\.|[^&quot;\\])*&quot;(?=\s*:)</Rule>
                <Rule color="String">&quot;(?:\\.|[^&quot;\\])*&quot;</Rule>
                <Rule color="Number">\b-?(0|[1-9][0-9]*)(\.[0-9]+)?([eE][+-]?[0-9]+)?\b</Rule>
                <Rule color="Keyword">\b(true|false|null)\b</Rule>
                <Rule color="Punctuation">[{}\[\],:]</Rule>
              </RuleSet>
            </SyntaxDefinition>
            """;
        using var reader = XmlReader.Create(new StringReader(definition));
        return HighlightingLoader.Load(reader, HighlightingManager.Instance);
    }

    private static IHighlightingDefinition CreateMarkupHighlighting()
    {
        const string definition = """
            <SyntaxDefinition name="Markup" extensions=".xml;.html;.xhtml" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
              <Color name="Tag" foreground="#569CD6" fontWeight="bold" />
              <Color name="Attribute" foreground="#9CDCFE" />
              <Color name="String" foreground="#CE9178" />
              <Color name="Comment" foreground="#6A9955" />
              <RuleSet>
                <Rule color="Comment">&lt;!--[\s\S]*?--&gt;</Rule>
                <Rule color="String">&quot;[^&quot;]*&quot;|'[^']*'</Rule>
                <Rule color="Attribute">\b[A-Za-z_:][A-Za-z0-9:._-]*(?=\s*=)</Rule>
                <Rule color="Tag">&lt;/?[A-Za-z][A-Za-z0-9:._-]*|/&gt;|&gt;|=</Rule>
              </RuleSet>
            </SyntaxDefinition>
            """;
        using var reader = XmlReader.Create(new StringReader(definition));
        return HighlightingLoader.Load(reader, HighlightingManager.Instance);
    }

    private void Editor_TextChanged(object? sender, EventArgs e)
    {
        _updatingText = true;
        SetCurrentValue(TextProperty, Editor.Text);
        _updatingText = false;
        TextChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Editor_TextEntering(object? sender, TextCompositionEventArgs e)
    {
        if (!IsJson || e.Text.Length != 1) return;

        char typed = e.Text[0];
        int offset = Editor.CaretOffset;
        string text = Editor.Text;
        bool insideString = IsInsideString(text, offset);
        if (offset < text.Length && text[offset] == typed
            && (typed == '"' || !insideString && typed is '}' or ']'))
        {
            Editor.CaretOffset++;
            e.Handled = true;
            return;
        }

        char? closing = typed switch
        {
            '{' when !insideString => '}',
            '[' when !insideString => ']',
            '"' when !IsInsideString(text, offset) => '"',
            _ => null
        };
        if (closing is null) return;

        int selectionStart = Editor.SelectionStart;
        int selectionLength = Editor.SelectionLength;
        string selectedText = selectionLength > 0
            ? Editor.Document.GetText(selectionStart, selectionLength)
            : "";
        string insertion = typed + selectedText + closing.Value;
        Editor.Document.Replace(selectionStart, selectionLength, insertion);
        Editor.CaretOffset = selectionStart + 1 + selectedText.Length;
        e.Handled = true;
    }

    private void Editor_KeyDown(object sender, KeyEventArgs e)
    {
        if (!IsJson) return;

        if (e.Key == Key.Tab)
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                OutdentCurrentLine();
            else
                ReplaceSelection("    ");
            e.Handled = true;
            return;
        }
        if (e.Key != Key.Enter) return;

        int offset = Editor.SelectionLength > 0 ? Editor.SelectionStart : Editor.CaretOffset;
        var line = Editor.Document.GetLineByOffset(offset);
        string before = Editor.Document.GetText(line.Offset, offset - line.Offset);
        string after = Editor.Document.GetText(offset, line.EndOffset - offset);
        string indentation = new(before.TakeWhile(character => character is ' ' or '\t').ToArray());
        char opener = before.TrimEnd().LastOrDefault();
        char closer = after.TrimStart().FirstOrDefault();
        bool betweenPair = opener == '{' && closer == '}' || opener == '[' && closer == ']';
        bool opensContainer = opener is '{' or '[';
        string innerIndent = opensContainer ? indentation + "    " : indentation;
        string newline = GetLineBreak(line);
        string insertion = newline + innerIndent;
        if (betweenPair) insertion += newline + indentation;
        else if (closer is '}' or ']' && indentation.Length >= 4)
            insertion = newline + indentation[..^4];

        Editor.Document.Replace(offset, Editor.SelectionLength, insertion);
        var insertedLine = Editor.Document.GetLineByNumber(line.LineNumber + 1);
        int caretIndent = betweenPair || opensContainer
            ? innerIndent.Length
            : insertion.Length - newline.Length;
        Editor.CaretOffset = insertedLine.Offset + caretIndent;
        e.Handled = true;
    }

    private string GetLineBreak(DocumentLine line)
    {
        string delimiter = line.DelimiterLength == 0
            ? Environment.NewLine
            : Editor.Document.GetText(line.EndOffset, line.DelimiterLength);
        return delimiter;
    }

    private void ReplaceSelection(string value)
    {
        int offset = Editor.SelectionLength > 0 ? Editor.SelectionStart : Editor.CaretOffset;
        Editor.Document.Replace(offset, Editor.SelectionLength, value);
        Editor.CaretOffset = offset + value.Length;
    }

    private void OutdentCurrentLine()
    {
        int offset = Editor.SelectionLength > 0 ? Editor.SelectionStart : Editor.CaretOffset;
        var line = Editor.Document.GetLineByOffset(offset);
        string lineText = Editor.Document.GetText(line.Offset, line.Length);
        int leadingSpaces = lineText.TakeWhile(character => character == ' ').Count();
        int removeCount = Math.Min(4, leadingSpaces);
        if (removeCount == 0) return;

        Editor.Document.Remove(line.Offset, removeCount);
        Editor.CaretOffset = Math.Max(line.Offset, offset - removeCount);
    }

    private static bool IsInsideString(string text, int offset)
    {
        bool insideString = false;
        bool escaped = false;
        for (int i = 0; i < offset; i++)
        {
            char character = text[i];
            if (escaped)
            {
                escaped = false;
                continue;
            }
            if (character == '\\')
                escaped = true;
            else if (character == '"')
                insideString = !insideString;
        }
        return insideString;
    }
}
