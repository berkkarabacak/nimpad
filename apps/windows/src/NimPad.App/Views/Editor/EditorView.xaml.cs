using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;
using NimPad.Presentation.Editor;
using NimPad.Presentation.Shell;

namespace NimPad.App.Views.Editor;

/// <summary>
/// STUB of the editor area (it is replaced by the full editor view): one AvalonEdit <see cref="TextEditor"/>
/// (AutomationId <c>Editor</c>) driven by an <see cref="EditorHost"/> that gives every tab its own document, with the
/// design's colors (Surface, Text, Selection, CurrentLine, Accent caret), the settings (font, size × zoom, word wrap,
/// line numbers, tab size), the editor padding of the layout, the empty-note hint, the loading states and
/// Ctrl+wheel zoom. DataContext: the <see cref="ShellViewModel"/>.
/// </summary>
public partial class EditorView : UserControl
{
    private const string FontFallback = ", Cascadia Mono, Consolas, Courier New";
    private ShellViewModel? _shell;
    private EditorHost? _host;
    private bool _isDarkTheme;

    /// <summary>Creates the view.</summary>
    public EditorView()
    {
        InitializeComponent();
        ConfigureEditor();
        DataContextChanged += OnDataContextChanged;
        Root.PreviewMouseWheel += OnPreviewMouseWheel;
        Editor.TextArea.TextView.LayoutUpdated += (_, _) => UpdateOverlayPosition();
        Loaded += (_, _) => ApplyThemeBrushes();
    }

    /// <summary>The AvalonEdit editor.</summary>
    public TextEditor TextEditor => Editor;

    /// <summary>The document manager (after the DataContext is set).</summary>
    public EditorHost? Host => _host;

    /// <summary>Whether the dark theme is applied; the window sets it on theme changes (syntax colors, caret).</summary>
    public bool IsDarkTheme
    {
        get => _isDarkTheme;
        set
        {
            _isDarkTheme = value;
            ApplyThemeBrushes();
            _host?.RefreshHighlighting();
        }
    }

    /// <summary>The selected text (Find / Replace prefill), or an empty string.</summary>
    public string SelectedText => Editor.TextArea.Selection.IsEmpty ? string.Empty : Editor.SelectedText;

    /// <summary>Moves keyboard focus into the text.</summary>
    /// <returns><see langword="true"/> when the editor took focus.</returns>
    public bool FocusEditor()
    {
        if (!Editor.IsVisible)
        {
            return false;
        }

        Editor.TextArea.Focus();
        return ReferenceEquals(Keyboard.Focus(Editor.TextArea), Editor.TextArea) || Editor.TextArea.IsKeyboardFocusWithin;
    }

    private void ConfigureEditor()
    {
        var options = Editor.Options;
        options.HighlightCurrentLine = true;
        options.EnableEmailHyperlinks = false;
        options.EnableHyperlinks = false;
        options.ConvertTabsToSpaces = false;
        options.AllowScrollBelowDocument = false;
        options.EnableRectangularSelection = true;
        var area = Editor.TextArea;
        area.SetResourceReference(TextArea.SelectionBrushProperty, "SelectionBrush");
        area.SelectionForeground = null;
        area.SelectionBorder = null;
        area.SelectionCornerRadius = 2;
        area.TextView.SetResourceReference(TextView.CurrentLineBackgroundProperty, "CurrentLineBrush");
        area.TextView.CurrentLineBorder = new Pen(Brushes.Transparent, 0);
        area.TextView.SetResourceReference(TextView.LinkTextForegroundBrushProperty, "AccentBrush");

        // The text area takes focus and is what screen readers and UI tests reach with the keyboard; it carries the
        // same automation ID as the editor (the editor's peer comes first in the tree and exposes the Value pattern).
        AutomationProperties.SetAutomationId(area, "Editor");
        AutomationProperties.SetName(area, "Note text");
    }

    private void ApplyThemeBrushes()
    {
        if (TryFindResource("AccentBrush") is Brush caret)
        {
            Editor.TextArea.Caret.CaretBrush = caret;
        }
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_shell is not null)
        {
            _shell.Editor.PropertyChanged -= OnEditorPropertyChanged;
            _host?.Dispose();
            _host = null;
        }

        _shell = e.NewValue as ShellViewModel;
        if (_shell is null)
        {
            return;
        }

        _shell.Editor.PropertyChanged += OnEditorPropertyChanged;
        _host = new EditorHost(Editor, _shell, () => _isDarkTheme);
        ApplyEditorSettings();
    }

    private void OnEditorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(EditorViewModel.FontFamily):
            case nameof(EditorViewModel.TabSize):
            case nameof(EditorViewModel.HorizontalPadding):
                ApplyEditorSettings();
                break;
        }
    }

    private void ApplyEditorSettings()
    {
        if (_shell is null)
        {
            return;
        }

        var editor = _shell.Editor;
        Editor.FontFamily = new FontFamily(editor.FontFamily + FontFallback);
        Editor.Options.IndentationSize = Math.Max(1, editor.TabSize);
        Editor.Padding = new Thickness(editor.HorizontalPadding, 20, editor.HorizontalPadding, 0);
        UpdateOverlayPosition();
    }

    // The hint and the skeleton lines sit where the first line of text starts.
    private void UpdateOverlayPosition()
    {
        if (EmptyHint.Visibility != Visibility.Visible && LoadingPlaceholder.Visibility != Visibility.Visible)
        {
            return;
        }

        var textView = Editor.TextArea.TextView;
        if (!textView.IsLoaded || PresentationSource.FromVisual(textView) is null)
        {
            return;
        }

        var origin = textView.TranslatePoint(default, Root);
        var margin = new Thickness(Math.Max(0, origin.X), Math.Max(0, origin.Y), 0, 0);
        if (!EmptyHint.Margin.Equals(margin))
        {
            EmptyHint.Margin = margin;
        }

        var placeholderMargin = new Thickness(margin.Left, margin.Top + 4, 0, 0);
        if (!LoadingPlaceholder.Margin.Equals(placeholderMargin))
        {
            LoadingPlaceholder.Margin = placeholderMargin;
        }
    }

    // Ctrl+wheel over the text zooms (design page 24).
    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_shell is null || (Keyboard.Modifiers & ModifierKeys.Control) == 0 || e.Delta == 0)
        {
            return;
        }

        var command = e.Delta > 0 ? _shell.ZoomInCommand : _shell.ZoomOutCommand;
        if (command.CanExecute(null))
        {
            command.Execute(null);
        }

        e.Handled = true;
    }
}
