using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace NimPad.App.Controls;

/// <summary>
/// The 32 px search field (sidebar search, shortcut filter): search icon inside, placeholder, a clear button that
/// appears once there is text, and Esc to clear. Esc on an empty box is not handled, so it bubbles (for example to
/// close the sidebar). Exposes itself to UI Automation as an Edit with the Value pattern; set
/// <c>AutomationProperties.Name</c>/<c>AutomationId</c> on the SearchBox itself.
/// </summary>
[TemplatePart(Name = PartTextBox, Type = typeof(TextBox))]
[TemplatePart(Name = PartClearButton, Type = typeof(ButtonBase))]
public class SearchBox : Control
{
    public const string PartTextBox = "PART_TextBox";
    public const string PartClearButton = "PART_ClearButton";

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(SearchBox),
        new FrameworkPropertyMetadata(
            string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnTextChanged, CoerceText)
        {
            DefaultUpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged,
        });

    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.Register(
        nameof(Placeholder), typeof(string), typeof(SearchBox), new FrameworkPropertyMetadata(null));

    private static readonly DependencyPropertyKey HasTextPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(HasText), typeof(bool), typeof(SearchBox), new FrameworkPropertyMetadata(false));

    public static readonly DependencyProperty HasTextProperty = HasTextPropertyKey.DependencyProperty;

    public static readonly DependencyProperty ClearCommandProperty = DependencyProperty.Register(
        nameof(ClearCommand), typeof(ICommand), typeof(SearchBox), new FrameworkPropertyMetadata(null));

    public static readonly DependencyProperty ClearCommandParameterProperty = DependencyProperty.Register(
        nameof(ClearCommandParameter), typeof(object), typeof(SearchBox), new FrameworkPropertyMetadata(null));

    public static readonly RoutedEvent ClearedEvent = EventManager.RegisterRoutedEvent(
        nameof(Cleared), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(SearchBox));

    private TextBox? _textBox;
    private ButtonBase? _clearButton;

    static SearchBox()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(SearchBox), new FrameworkPropertyMetadata(typeof(SearchBox)));
        FocusableProperty.OverrideMetadata(typeof(SearchBox), new FrameworkPropertyMetadata(true));
        // The box itself is not a tab stop: Tab lands directly in the inner text field.
        IsTabStopProperty.OverrideMetadata(typeof(SearchBox), new FrameworkPropertyMetadata(false));
    }

    /// <summary>The query. Two-way by default and updated on every keystroke.</summary>
    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>Hint shown while empty ("Search all notes").</summary>
    public string? Placeholder
    {
        get => (string?)GetValue(PlaceholderProperty);
        set => SetValue(PlaceholderProperty, value);
    }

    /// <summary>True while <see cref="Text"/> is not empty (drives the clear button).</summary>
    public bool HasText => (bool)GetValue(HasTextProperty);

    /// <summary>Optional command executed after the box was cleared by its clear button, Esc or <see cref="Clear"/>.</summary>
    public ICommand? ClearCommand
    {
        get => (ICommand?)GetValue(ClearCommandProperty);
        set => SetValue(ClearCommandProperty, value);
    }

    public object? ClearCommandParameter
    {
        get => GetValue(ClearCommandParameterProperty);
        set => SetValue(ClearCommandParameterProperty, value);
    }

    /// <summary>Raised after the box was cleared by its clear button, Esc or <see cref="Clear"/>.</summary>
    public event RoutedEventHandler Cleared
    {
        add => AddHandler(ClearedEvent, value);
        remove => RemoveHandler(ClearedEvent, value);
    }

    /// <summary>Clears the text, raises <see cref="Cleared"/> and runs <see cref="ClearCommand"/>.</summary>
    public void Clear()
    {
        SetCurrentValue(TextProperty, string.Empty);
        RaiseEvent(new RoutedEventArgs(ClearedEvent, this));
        var command = ClearCommand;
        var parameter = ClearCommandParameter;
        if (command is not null && command.CanExecute(parameter))
        {
            command.Execute(parameter);
        }
    }

    /// <summary>Moves keyboard focus into the text field and selects its text.</summary>
    public void FocusAndSelectAll()
    {
        ApplyTemplate();
        if (_textBox is null)
        {
            Focus();
            return;
        }

        _textBox.Focus();
        _textBox.SelectAll();
    }

    public override void OnApplyTemplate()
    {
        if (_clearButton is not null)
        {
            _clearButton.Click -= OnClearButtonClick;
        }

        base.OnApplyTemplate();
        _textBox = GetTemplateChild(PartTextBox) as TextBox;
        _clearButton = GetTemplateChild(PartClearButton) as ButtonBase;
        if (_clearButton is not null)
        {
            _clearButton.Click += OnClearButtonClick;
        }
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        if (ReferenceEquals(e.NewFocus, this) && _textBox is not null)
        {
            // Focus lands on the box itself (Focus(), access key, AutomationElement.SetFocus): forward it.
            e.Handled = true;
            _textBox.Focus();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && e.Key == Key.Escape && HasText)
        {
            Clear();
            e.Handled = true;
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new SearchBoxAutomationPeer(this);

    private void OnClearButtonClick(object sender, RoutedEventArgs e)
    {
        Clear();
        _textBox?.Focus();
    }

    private static object CoerceText(DependencyObject d, object? value) => value ?? string.Empty;

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var box = (SearchBox)d;
        var text = (string)e.NewValue;
        box.SetValue(HasTextPropertyKey, text.Length > 0);
        if (UIElementAutomationPeer.FromElement(box) is SearchBoxAutomationPeer peer)
        {
            peer.RaiseValueChanged((string)e.OldValue, text);
        }
    }

    private sealed class SearchBoxAutomationPeer(SearchBox owner)
        : FrameworkElementAutomationPeer(owner), IValueProvider
    {
        private SearchBox Box => (SearchBox)Owner;

        public bool IsReadOnly => !Box.IsEnabled;

        public string Value => Box.Text;

        public void SetValue(string value)
        {
            if (!Box.IsEnabled)
            {
                throw new ElementNotEnabledException();
            }

            Box.SetCurrentValue(TextProperty, value ?? string.Empty);
        }

        public override object? GetPattern(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.Value ? this : base.GetPattern(patternInterface);

        public void RaiseValueChanged(string oldValue, string newValue) =>
            RaisePropertyChangedEvent(ValuePatternIdentifiers.ValueProperty, oldValue, newValue);

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Edit;

        protected override string GetClassNameCore() => nameof(SearchBox);

        protected override string GetNameCore()
        {
            var name = base.GetNameCore();
            return string.IsNullOrEmpty(name) ? Box.Placeholder ?? string.Empty : name;
        }

        protected override bool IsKeyboardFocusableCore() => Box.IsEnabled;

        protected override void SetFocusCore() => Box.FocusAndSelectAll();
    }
}
