using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
namespace BoramRms.Lite;

public partial class MainWindow
{
    private bool _spaceInFlight, _imeSpacePending;
    private int _imeSpaceDirection = 1;
    internal Func<ModifierKeys>? ModifiersForTests { get; set; }
    private ModifierKeys CurrentModifiers => TestMode && ModifiersForTests != null ? ModifiersForTests() : Keyboard.Modifiers;
    public static int SpaceDirection(ModifierKeys modifiers) => modifiers == ModifierKeys.None ? 1 : modifiers == ModifierKeys.Control ? -1 : 0;
    public Task LastShortcutTask { get; private set; } = Task.CompletedTask;
    private void InitializeNavigation()
    {
        PreviewKeyUp += Window_KeyUp;
        CombinedTextBox.PreviewTextInput += CombinedTextBox_PreviewTextInput;
    }
    private void CombinedTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        // Same caret rule as the original RMS; let WPF/IME perform the actual insertion.
        if (_filling || string.IsNullOrEmpty(e.Text) || e.Text.Any(c => !char.IsAsciiDigit(c))) return;
        PrepareQuotaInsertion(CombinedTextBox);
    }
    public static void PrepareQuotaInsertion(TextBox input)
    {
        if (input.SelectionLength > 0 && input.SelectionStart == 0) return;
        int end = 0;
        while (end < input.Text.Length && char.IsAsciiDigit(input.Text[end])) end++;
        input.Select(end, 0);
    }
    private static DependencyObject? ParentOf(DependencyObject node) => node is Visual
        ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
    public bool CanHandleSpace(DependencyObject? source)
    {
        for (var node = source; node != null; node = ParentOf(node))
        {
            if (node == CombinedTextBox) return true;
            if (node is TextBoxBase or PasswordBox or ComboBox or ButtonBase or MenuItem or ScrollBar) return false;
        }
        return true;
    }
    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
        if (e.Key != Key.ImeProcessed && (key is Key.Up or Key.Down) &&
            CurrentModifiers == ModifierKeys.None && CanHandleSpace(e.OriginalSource as DependencyObject))
        {
            // Handle before TextBox/ListBox defaults so the same save-before-move
            // workflow works in the name field, image and list (also when detached).
            // IME candidates, editable memos and modified selection keys stay native.
            e.Handled = true;
            if (!_spaceInFlight && !_navigating)
                LastShortcutTask = AdvanceWithSpaceAsync(key == Key.Up ? -1 : 1);
            return;
        }
        var direction = SpaceDirection(CurrentModifiers);
        if (key == Key.Space && direction != 0 && CanHandleSpace(e.OriginalSource as DependencyObject))
        {
            if (e.Key == Key.ImeProcessed)
            {
                // Let the input method finish Korean text; act only on this window's key-up.
                if (!e.IsRepeat) { _imeSpacePending = true; _imeSpaceDirection = direction; }
                return;
            }
            e.Handled = true;
            if (!e.IsRepeat) LastShortcutTask = AdvanceWithSpaceAsync(direction);
            return;
        }
        if (key == Key.Escape && _compressionCancellation != null) { _compressionCancellation.Cancel(); e.Handled = true; return; }
        if (key == Key.S && Keyboard.Modifiers == ModifierKeys.Control && !e.IsRepeat) { e.Handled = true; LastShortcutTask = SaveDraftAsync(false); return; }
        if (key == Key.F5 && !e.IsRepeat) { Refresh_Click(sender, new RoutedEventArgs()); e.Handled = true; return; }
        if (key == Key.O && Keyboard.Modifiers == ModifierKeys.Control && !e.IsRepeat) { OpenFolder_Click(sender, new RoutedEventArgs()); e.Handled = true; return; }
        if (Keyboard.Modifiers != ModifierKeys.None || e.IsRepeat || !CanHandleSpace(e.OriginalSource as DependencyObject) || CombinedTextBox.IsKeyboardFocusWithin) return;
        if (key == Key.Left) { MoveImage(-1); e.Handled = true; }
        if (key == Key.Right) { MoveImage(1); e.Handled = true; }
    }
    private void Window_KeyUp(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
        if (!_imeSpacePending || key != Key.Space) return;
        _imeSpacePending = false;
        if (!CanHandleSpace(e.OriginalSource as DependencyObject)) return;
        e.Handled = true; LastShortcutTask = AdvanceWithSpaceAsync(_imeSpaceDirection);
    }
    public async Task AdvanceWithSpaceAsync(int direction = 1)
    {
        if (_spaceInFlight || _busy || _loading || _closing || _editing == null) return;
        _spaceInFlight = true; var item = _editing;
        try
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
            if (_closing || _busy || _loading || _editing != item) return;
            await NavigateImageAsync(direction);
        }
        catch (Exception ex) { Error(ex); }
        finally { _spaceInFlight = false; }
    }
    private void FocusRenameInput(bool selectAll = true)
    {
        if (TestMode || _busy || _closing || _editing == null) return;
        if (!IsActive && !_docks.Values.Any(w => w.IsActive)) return;
        if (CombinedTextBox.Focus() && selectAll) CombinedTextBox.SelectAll();
    }
    private ImageViewLayout CurrentImageLayout()
    {
        var host = Window.GetWindow(ImageStage);
        var detached = host != null && host != this;
        var area = detached ? host!.Content as FrameworkElement : AppLayout;
        return new(Math.Round(area?.ActualWidth ?? 0, 1), Math.Round(area?.ActualHeight ?? 0, 1),
            detached ? 0 : Math.Round(LeftColumn.ActualWidth, 1), detached ? 0 : Math.Round(RightColumn.ActualWidth, 1),
            !detached && WorkLogExpander.IsExpanded, detached);
    }
    private void RefreshCameraLayout()
    {
        if (_active == null) return;
        var view = _active.Camera; var layout = CurrentImageLayout();
        if (view.Layout != null && view.Layout != layout)
        {
            view.BaseWidth = 0; view.Scale = 1; view.X = view.Y = 0;
        }
        view.Layout = layout;
    }
    public void SetViewport(double scale, double x, double y)
    {
        if (_active == null || !double.IsFinite(scale) || !double.IsFinite(x) || !double.IsFinite(y)) return;
        var view = _active.Camera;
        view.Layout = CurrentImageLayout();
        view.Scale = Math.Clamp(scale, .1, 20); view.X = Math.Clamp(x, -100000, 100000); view.Y = Math.Clamp(y, -100000, 100000);
        ImageScale.ScaleX = ImageScale.ScaleY = view.Scale; ImageTranslate.X = view.X; ImageTranslate.Y = view.Y;
        ZoomText.Text = $"{view.Scale:P0}";
    }
    private void ApplyImageViewport(BitmapSource source)
    {
        if (_active == null) return;
        RefreshCameraLayout();
        var view = _active.Camera;
        if (view.BaseWidth <= 0)
        {
            if (ImageStage.ActualWidth <= 24 || ImageStage.ActualHeight <= 24) return;
            view.BaseWidth = Math.Min(ImageStage.ActualWidth - 24, (ImageStage.ActualHeight - 24) * source.PixelWidth / source.PixelHeight);
        }
        PreviewImage.Width = view.BaseWidth;
        PreviewImage.Height = view.BaseWidth * source.PixelHeight / source.PixelWidth;
        SetViewport(view.Scale, view.X, view.Y);
    }
    private void ImageStage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_active == null || ImageStage.ActualWidth <= 24 || ImageStage.ActualHeight <= 24) return;
        if (!e.WidthChanged && !e.HeightChanged) return;
        // Image loading and text reflow also raise SizeChanged. Only an actual
        // window, splitter, dock or log-panel change should reset the user's view.
        RefreshCameraLayout();
        if (PreviewImage?.Source is BitmapSource image) ApplyImageViewport(image);
    }
}
