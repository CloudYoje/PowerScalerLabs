using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PowerScalerLabs.App.Overlay;

public partial class ExperimentOverlayWindow : Window
{
    private readonly MainWindow _host;
    private OverlayViewState _state = new(
        "Offline",
        false,
        false,
        0,
        false,
        string.Empty,
        false,
        string.Empty,
        0,
        0,
        0,
        0,
        "Waiting for PowerScaler Labs.");
    private ExperimentTestDefinition _selectedTest = ExperimentCatalog.DefaultTest;
    private bool _allowClose;
    private bool _selectionReady;
    private bool _commandInFlight;
    private string? _feedbackOverride;
    private bool _feedbackOverrideIsError;
    private DateTimeOffset _feedbackUntilUtc;

    internal ExperimentOverlayWindow(MainWindow host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        InitializeComponent();
        CategoryListBox.ItemsSource = ExperimentCatalog.Categories.Select(category => category.Name).ToArray();
        TestListBox.DisplayMemberPath = nameof(ExperimentTestDefinition.Name);
        CategoryListBox.SelectedIndex = 0;
        _selectionReady = true;
        PopulateTestsForSelectedCategory();
        SelectTest(ExperimentCatalog.DefaultTest);
    }

    internal string SelectedActionLabel => _selectedTest.Name;

    internal void ShowOverlay()
    {
        FitToWorkArea();
        if (!IsVisible)
        {
            Show();
        }
        WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        CategoryListBox.Focus();
    }

    internal void ToggleOverlay()
    {
        if (IsVisible)
        {
            Hide();
        }
        else
        {
            ShowOverlay();
        }
    }

    internal void ForceClose()
    {
        _allowClose = true;
        Close();
    }

    internal void UpdateState(OverlayViewState state)
    {
        _state = state;
        RuntimeStateText.Text = state.RuntimeState;
        RuntimeStateText.Foreground = StateBrush(state.RuntimeConnected && state.GameDetected);
        FighterCountText.Text = state.FighterCount.ToString();
        FighterCountText.Foreground = state.FighterCount > 0 ? ResourceBrush("SuccessBrush") : ResourceBrush("WarningBrush");
        RecordingStateText.Text = state.IsRecording ? "ON" : "OFF";
        RecordingStateText.Foreground = state.IsRecording ? ResourceBrush("DangerBrush") : ResourceBrush("WarningBrush");
        BaselineStateText.Text = state.HasBaseline
            ? string.IsNullOrWhiteSpace(state.BaselineLabel) ? "Armed" : state.BaselineLabel
            : "None";
        BaselineStateText.Foreground = state.HasBaseline ? ResourceBrush("SuccessBrush") : ResourceBrush("WarningBrush");
        ChangedCountText.Text = state.ChangedObservations.ToString("N0");
        StableCountText.Text = state.StableObservations.ToString("N0");
        PendingCountText.Text = state.PendingObservations.ToString("N0");
        PendingCountText.Foreground = state.PendingObservations == 0 ? ResourceBrush("SuccessBrush") : ResourceBrush("WarningBrush");
        RecordingButton.Content = state.IsRecording ? "Stop & Save" : "Start Recording";
        DetailText.Text = state.Detail;

        bool ready = state.RuntimeConnected && state.GameDetected && state.FighterCount > 0;
        bool queueEmpty = state.PendingObservations == 0;
        bool menuUnlocked = !state.HasBaseline && queueEmpty && !_commandInFlight;
        CategoryListBox.IsEnabled = menuUnlocked;
        TestListBox.IsEnabled = menuUnlocked;
        CaptureBaselineButton.IsEnabled = ready && state.IsRecording && queueEmpty && !state.HasBaseline && !_commandInFlight;
        CompareResultsButton.IsEnabled = ready && state.IsRecording && state.HasBaseline && queueEmpty && !_commandInFlight;
        RepeatTestButton.IsEnabled = ready && state.IsRecording && state.HasBaseline && queueEmpty && !_commandInFlight;
        FullSnapshotButton.IsEnabled = ready && state.IsRecording && queueEmpty && !_commandInFlight;
        CancelTestButton.IsEnabled = state.HasBaseline && queueEmpty && !_commandInFlight;
        RecordingButton.IsEnabled = !_commandInFlight && (state.IsRecording ? queueEmpty : ready && !state.HasBaseline);

        if (!string.IsNullOrWhiteSpace(_feedbackOverride) && DateTimeOffset.UtcNow < _feedbackUntilUtc)
        {
            FeedbackText.Text = _feedbackOverride;
            FeedbackText.Foreground = _feedbackOverrideIsError ? ResourceBrush("DangerBrush") : ResourceBrush("SuccessBrush");
        }
        else
        {
            _feedbackOverride = null;
            FeedbackText.Foreground = ResourceBrush("PrimaryTextBrush");
            if (!state.RuntimeConnected)
            {
                FeedbackText.Text = "Waiting for the companion runtime.";
            }
            else if (!state.GameDetected)
            {
                FeedbackText.Text = "Launch Xenoverse 2 normally. HealthScaler remains untouched.";
            }
            else if (state.FighterCount == 0)
            {
                FeedbackText.Text = "Enter a battle or Training mode and wait for fighters.";
            }
            else if (!state.IsRecording && state.HasBaseline)
            {
                FeedbackText.Text = "A baseline is still armed from the previous recording. Select Cancel Test before starting a new session.";
            }
            else if (!state.IsRecording)
            {
                FeedbackText.Text = "Start Recording, then select a test and capture its baseline.";
            }
            else if (state.PendingObservations > 0)
            {
                FeedbackText.Text = $"Processing {state.PendingObservations:N0} observations. Wait for Pending to reach 0.";
            }
            else if (state.HasBaseline)
            {
                FeedbackText.Text = $"Baseline armed for '{state.BaselineLabel}'. Perform the action, then select Compare Results.";
            }
            else
            {
                FeedbackText.Text = $"Ready for '{_selectedTest.Name}'. Select Capture Baseline.";
            }
        }
    }

    internal void ShowFeedback(string message, bool isError)
    {
        _feedbackOverride = message;
        _feedbackOverrideIsError = isError;
        _feedbackUntilUtc = DateTimeOffset.UtcNow.AddSeconds(isError ? 6 : 4);
        FeedbackText.Text = message;
        FeedbackText.Foreground = isError ? ResourceBrush("DangerBrush") : ResourceBrush("SuccessBrush");
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        FitToWorkArea();
        CategoryListBox.Focus();
    }

    private void FitToWorkArea()
    {
        Rect workArea = SystemParameters.WorkArea;
        const double gap = 18;
        double availableWidth = Math.Max(MinWidth, workArea.Width - gap * 2);
        double availableHeight = Math.Max(MinHeight, workArea.Height - gap * 2);
        Width = Math.Min(620, availableWidth);
        Height = Math.Min(520, availableHeight);
        Left = Math.Clamp(workArea.Right - Width - gap, workArea.Left + gap, workArea.Right - Width - gap);
        Top = workArea.Top + gap;
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void MinimizeOverlayButton_Click(object sender, RoutedEventArgs e) => Hide();

    private void HideOverlayButton_Click(object sender, RoutedEventArgs e) => Hide();

    private void OpenMainAppButton_Click(object sender, RoutedEventArgs e)
    {
        _host.ShowMainApp();
    }

    private void CategoryListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_selectionReady)
        {
            PopulateTestsForSelectedCategory();
        }
    }

    private void TestListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TestListBox.SelectedItem is ExperimentTestDefinition test)
        {
            SelectTest(test);
        }
    }

    private void PopulateTestsForSelectedCategory()
    {
        int categoryIndex = Math.Max(0, CategoryListBox.SelectedIndex);
        ExperimentCategoryDefinition category = ExperimentCatalog.Categories[Math.Min(categoryIndex, ExperimentCatalog.Categories.Count - 1)];
        TestListBox.ItemsSource = category.Tests;
        TestListBox.SelectedIndex = 0;
    }

    private void SelectTest(ExperimentTestDefinition test)
    {
        _selectedTest = test;
        SelectedTestNameText.Text = test.Name;
        SelectedTestInstructionText.Text = test.Instruction;
        SelectedTestExpectedText.Text = "Expected: " + test.ExpectedDirection;
        SelectedTestTipText.Text = "Isolation: " + test.IsolationTip;
        _host.SetGuidedActionLabel(test.Name);
        if (_state.IsRecording && !_state.HasBaseline && _state.PendingObservations == 0)
        {
            FeedbackText.Text = $"Ready for '{test.Name}'. Select Capture Baseline.";
            FeedbackText.Foreground = ResourceBrush("PrimaryTextBrush");
        }
    }

    private async void CaptureBaselineButton_Click(object sender, RoutedEventArgs e)
    {
        await RunOverlayActionAsync(
            () => _host.CaptureGuidedBaselineAsync(_selectedTest.Name),
            $"Baseline requested for '{_selectedTest.Name}'.").ConfigureAwait(true);
    }

    private async void CompareResultsButton_Click(object sender, RoutedEventArgs e)
    {
        await RunOverlayActionAsync(
            () => _host.CompareGuidedResultsAsync(_selectedTest.Name),
            $"Comparison requested for '{_selectedTest.Name}'.").ConfigureAwait(true);
    }

    private async void RepeatTestButton_Click(object sender, RoutedEventArgs e)
    {
        await RunOverlayActionAsync(
            () => _host.RepeatGuidedTestAsync(_selectedTest.Name),
            $"Fresh baseline requested to repeat '{_selectedTest.Name}'.").ConfigureAwait(true);
    }

    private async void FullSnapshotButton_Click(object sender, RoutedEventArgs e)
    {
        await RunOverlayActionAsync(
            () => _host.CaptureGuidedSnapshotAsync(_selectedTest.Name),
            $"Full snapshot requested for '{_selectedTest.Name}'.").ConfigureAwait(true);
    }

    private async void CancelTestButton_Click(object sender, RoutedEventArgs e)
    {
        await RunOverlayActionAsync(
            _host.CancelGuidedTestAsync,
            "Current baseline cancelled.").ConfigureAwait(true);
    }

    private async void RecordingButton_Click(object sender, RoutedEventArgs e)
    {
        if (_commandInFlight)
        {
            return;
        }

        _commandInFlight = true;
        UpdateState(_state);
        try
        {
            if (_state.IsRecording)
            {
                await _host.StopGuidedRecordingAsync().ConfigureAwait(true);
                ShowFeedback("Recording stopped and saved. The experiment baseline was cleared.", false);
            }
            else
            {
                await _host.StartGuidedRecordingAsync(_selectedTest.Name).ConfigureAwait(true);
                ShowFeedback("Recording started. Capture a baseline before performing the test.", false);
            }
        }
        catch (Exception exception)
        {
            ShowFeedback(exception.Message, true);
        }
        finally
        {
            _commandInFlight = false;
            UpdateState(_state);
        }
    }

    private async Task RunOverlayActionAsync(Func<Task> action, string queuedMessage)
    {
        if (_commandInFlight)
        {
            return;
        }

        _commandInFlight = true;
        ShowFeedback(queuedMessage, false);
        UpdateState(_state);
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            ShowFeedback(exception.Message, true);
        }
        finally
        {
            _commandInFlight = false;
            UpdateState(_state);
        }
    }

    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Up or Key.Down)
        {
            if (!CategoryListBox.IsEnabled || !TestListBox.IsEnabled)
            {
                ShowFeedback("The test selection is locked while a baseline is armed. Compare, Repeat, or Cancel the current test first.", true);
                e.Handled = true;
                return;
            }

            ListBox target = CategoryListBox.IsKeyboardFocusWithin ? CategoryListBox : TestListBox;
            MoveSelection(target, e.Key == Key.Up ? -1 : 1);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Left)
        {
            if (CategoryListBox.IsEnabled)
            {
                CategoryListBox.Focus();
            }
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Right)
        {
            if (TestListBox.IsEnabled)
            {
                TestListBox.Focus();
            }
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter)
        {
            if (CategoryListBox.IsKeyboardFocusWithin)
            {
                TestListBox.Focus();
                e.Handled = true;
                return;
            }
            if (TestListBox.IsKeyboardFocusWithin)
            {
                if (!_state.IsRecording)
                {
                    RecordingButton.Focus();
                    ShowFeedback("Start Recording before capturing the selected test baseline.", true);
                }
                else if (_state.PendingObservations > 0)
                {
                    ShowFeedback($"Wait for Pending to reach 0. {_state.PendingObservations:N0} observations remain.", true);
                }
                else if (_state.HasBaseline)
                {
                    CompareResultsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                else
                {
                    CaptureBaselineButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                e.Handled = true;
                return;
            }
        }

        if (e.Key is Key.Escape or Key.Back)
        {
            if (TestListBox.IsKeyboardFocusWithin)
            {
                CategoryListBox.Focus();
            }
            else if (_state.HasBaseline && _state.PendingObservations == 0)
            {
                await _host.CancelGuidedTestAsync().ConfigureAwait(true);
                ShowFeedback("Current baseline cancelled.", false);
            }
            else
            {
                Hide();
            }
            e.Handled = true;
        }
    }

    private static void MoveSelection(ListBox listBox, int delta)
    {
        if (listBox.Items.Count == 0)
        {
            return;
        }

        int current = Math.Max(0, listBox.SelectedIndex);
        int next = Math.Clamp(current + delta, 0, listBox.Items.Count - 1);
        listBox.SelectedIndex = next;
        object? selectedItem = listBox.SelectedItem;
        if (selectedItem is not null)
        {
            listBox.ScrollIntoView(selectedItem);
        }
        listBox.Focus();
    }

    private Brush StateBrush(bool ready) => ready ? ResourceBrush("SuccessBrush") : ResourceBrush("WarningBrush");

    private static Brush ResourceBrush(string key)
    {
        Application? application = Application.Current;
        return (Brush)(application?.TryFindResource(key) ?? Brushes.White);
    }
}
