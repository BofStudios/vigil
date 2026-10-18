using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Vigil.Core;
using Vigil.Core.Providers;
using Vigil.Core.Security;
using Vigil.Core.Tools;
using Vigil.Desktop;

namespace Vigil.UI;

/// <summary>The conversation: what the agent tells the window, and how it is drawn.
/// Everything here may be called from the agent's worker thread, so it marshals
/// onto the window's thread before touching anything on screen.</summary>
public partial class ShellWindow : IAgentHost
{
    Agent? _agent;
    bool _atBottom = true;
    volatile string? _voiceTurn;
    (string Text, string? Language)? _queued;

    readonly StringBuilder _stream = new();
    readonly DispatcherTimer _render = new() { Interval = TimeSpan.FromMilliseconds(40) };
    RichTextBox? _streamBox;
    bool _streamDirty;
    ToolRow? _lastTool;

    bool _approvalPending;
    bool _approvalArmed;
    TaskCompletionSource<Answer>? _approval;
    IntPtr _returnFocusTo;

    bool _askPending;
    TaskCompletionSource<string?>? _ask;

    int _drivers;
    readonly DispatcherTimer _reshow = new() { Interval = TimeSpan.FromMilliseconds(650) };
    bool _hiddenForDriving;

    bool Busy => _agent?.Busy == true;

    Brush B(string key) => (Brush)FindResource(key);

    void WireHost()
    {
        _render.Tick += (_, _) => FlushStream();
        _reshow.Tick += (_, _) =>
        {
            _reshow.Stop();
            if (_drivers == 0 && _hiddenForDriving)
            {
                _hiddenForDriving = false;
                if (!_hiddenByUser && !_yielded) Show();
            }
        };
        AllowButton.Click += (_, _) => AnswerApproval(Answer.Yes);
        DenyButton.Click += (_, _) => AnswerApproval(Answer.No);
        AlwaysButton.Click += (_, _) => AnswerApproval(Answer.Always);
    }

    // =================================================================== the agent
    bool EnsureAgent()
    {
        if (_agent is not null) return true;
        if (!_config.IsReady)
        {
            ShowSetup();
            return false;
        }
        try
        {
            _agent = new Agent(_config, ProviderFactory.Build(_config), Registry.Build(_config), this);
            RefreshFooter();
            return true;
        }
        catch (ProviderException ex)
        {
            ShowSetup(ex.Message);
            return false;
        }
    }

    void RebuildAgent()
    {
        var cwd = _agent?.Context.Cwd;
        var history = _agent?.Messages;
        _agent?.Dispose();
        _agent = null;
        if (!EnsureAgent()) return;
        if (cwd is not null) _agent!.Context.Cwd = cwd;
        if (history is { Count: > 1 })
        {
            _agent!.Messages.AddRange(history.Skip(1));
            _agent.RefreshSystemPrompt();
        }
    }

    void SubmitInput()
    {
        var text = Input.Text.Trim();
        if (text.Length == 0) return;
        Input.Clear();
        if (_askPending)
        {
            AnswerAsk(text);
            return;
        }
        Submit(text, null);
    }

    /// <summary>One request, typed or spoken. `language` is set when it was spoken.</summary>
    async void Submit(string text, string? language)
    {
        _voice.Speaker.Stop();   // a new request cuts off the old answer
        if (!EnsureAgent())
        {
            GoTo(Shape.Panel);
            return;
        }
        if (Busy)
        {
            _queued = (text, language);
            _agent!.Stop();
            return;
        }

        AddUser(text, spoken: language is not null);
        GoTo(Shape.Panel);
        // the language Vigil will answer in - English unless it is set to follow the user
        _voiceTurn = language is not null && _config.SpeakReplies ? (AnswersInEnglish ? "en" : language) : null;
        if (_voiceTurn is not null)
        {
            _voice.Speaker.Begin(_voiceTurn);
            _voice.Thinking();
        }

        SetBusy(true);
        ShowThinking();
        var answer = "";
        try
        {
            answer = await _agent!.RunAsync(text, language);
        }
        catch (Exception ex)
        {
            AddLine("Something went wrong: " + ex.Message, "High");
            App.Log(ex);
        }
        finally
        {
            HideThinking();
            SetBusy(false);
        }

        var spoken = _voiceTurn;
        _voiceTurn = null;
        if (spoken is not null)
        {
            _voice.Speaker.Finish();
            _voice.Settle();
        }

        if (_queued is { } next)
        {
            _queued = null;
            Submit(next.Text, next.Language);
            return;
        }

        if (spoken is not null)
        {
            await _voice.Speaker.WhenDoneAsync();
            // it closed with a question, so wait a little longer for the answer
            var asked = answer.TrimEnd().EndsWith('?') || answer.TrimEnd().EndsWith('？');
            var window = asked ? Math.Max(_config.FollowUpSeconds, 9) : _config.FollowUpSeconds;
            if (window > 0 && _voice.On && !Busy && !_askPending)
                FollowUp(spoken, window);
        }
    }

    void SetBusy(bool on)
    {
        Working(on);
        SendButton.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        StopButton.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        UpdateTray();
        RefreshFooter();
    }

    void UpdateTray() => _tray.Set(
        _approvalPending || _askPending ? TrayState.Waiting
        : Busy ? TrayState.Busy
        : _voice.State is Voice.VoiceState.Listening ? TrayState.Listening
        : TrayState.Idle);

    void StopEverything()
    {
        _queued = null;
        _voice.Speaker.Stop();
        _agent?.Stop();
        if (_askPending) AnswerAsk(null);
    }

    void NewConversation()
    {
        StopEverything();
        _agent?.Reset();
        Thread.Children.Clear();
        PlanStrip.Visibility = Visibility.Collapsed;
        _streamBox = null;
        _stream.Clear();
        _lastTool = null;
        GoTo(Shape.Bar, focus: true);
    }

    // =============================================================== drawing it
    void ScrollIfFollowing()
    {
        if (_atBottom) Dispatcher.BeginInvoke(DispatcherPriority.Background, () => Scroller.ScrollToEnd());
    }

    void AddUser(string text, bool spoken)
    {
        var body = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14,
            LineHeight = 20,
        };
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        if (spoken)
            content.Children.Add(new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M10,3.2 A2.6,2.6 0 0 1 12.6,5.8 V9.6 A2.6,2.6 0 0 1 7.4,9.6 V5.8 A2.6,2.6 0 0 1 10,3.2 Z M5.4,9.4 A4.6,4.6 0 0 0 14.6,9.4 M10,14 V16.6"),
                Stroke = B("TextFaint"),
                StrokeThickness = 1.5,
                Width = 20,
                Height = 20,
                Margin = new Thickness(-4, -1, 4, 0),
                VerticalAlignment = VerticalAlignment.Top,
            });
        content.Children.Add(body);
        var bubble = new Border
        {
            Background = B("Surface2"),
            BorderBrush = B("CardRim"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18, 18, 6, 18),
            Padding = new Thickness(15, 9, 15, 10),
            Margin = new Thickness(80, 4, 0, 16),
            HorizontalAlignment = HorizontalAlignment.Right,
            Child = content,
        };
        Arrive(bubble);
        Thread.Children.Add(bubble);
        _atBottom = true;
        ScrollIfFollowing();
    }

    /// <summary>New things slide up a few pixels as they fade in, rather than appearing.</summary>
    void Arrive(FrameworkElement element, double delay = 0)
    {
        if (Motion.Reduced || _preview) return;
        element.Opacity = 0;
        var shift = new TranslateTransform(0, 8);
        element.RenderTransform = shift;
        element.To(UIElement.OpacityProperty, 1, 260, delay);
        shift.BeginAnimation(TranslateTransform.YProperty,
            new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.FromMilliseconds(320))
            {
                EasingFunction = Flow.Curve,
                BeginTime = TimeSpan.FromMilliseconds(delay),
            });
    }

    // ============================================================== thinking
    FrameworkElement? _thinking;

    /// <summary>The moment between asking and the first word back: a line of light
    /// moving through the word, so it never looks frozen.</summary>
    void ShowThinking()
    {
        HideThinking();
        var text = new TextBlock { Text = "Thinking", FontSize = 13.5, FontWeight = FontWeights.SemiBold };
        var sweep = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.5),
            EndPoint = new Point(1, 0.5),
            MappingMode = BrushMappingMode.RelativeToBoundingBox,
            GradientStops =
            {
                new GradientStop(Color.FromRgb(0x5A, 0x5D, 0x66), 0),
                new GradientStop(Color.FromRgb(0x5A, 0x5D, 0x66), 0.35),
                new GradientStop(Color.FromRgb(0x6F, 0xE3, 0xFF), 0.5),
                new GradientStop(Color.FromRgb(0x5A, 0x5D, 0x66), 0.65),
                new GradientStop(Color.FromRgb(0x5A, 0x5D, 0x66), 1),
            },
            RelativeTransform = new TranslateTransform(-1, 0),
        };
        text.Foreground = sweep;
        if (!Motion.Reduced)
            ((TranslateTransform)sweep.RelativeTransform).BeginAnimation(TranslateTransform.XProperty,
                new System.Windows.Media.Animation.DoubleAnimation(-1, 1, TimeSpan.FromSeconds(1.5))
                { RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever });
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 14) };
        row.Children.Add(text);
        Arrive(row, 120);
        _thinking = row;
        Thread.Children.Add(row);
        ScrollIfFollowing();
    }

    void HideThinking()
    {
        if (_thinking is null) return;
        Thread.Children.Remove(_thinking);
        _thinking = null;
    }

    void AddLine(string text, string colour = "TextFaint", double size = 12.5)
    {
        Thread.Children.Add(new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            FontSize = size,
            Foreground = B(colour),
            Margin = new Thickness(0, 0, 0, 10),
        });
        ScrollIfFollowing();
    }

    RichTextBox NewAnswerBox() => new()
    {
        IsReadOnly = true,
        IsDocumentEnabled = true,
        Background = Brushes.Transparent,
        BorderThickness = new Thickness(0),
        Padding = new Thickness(0),
        Margin = new Thickness(-2, 6, 0, 14),
        Foreground = B("TextBrush"),
        CaretBrush = Brushes.Transparent,
        SelectionBrush = new SolidColorBrush(Color.FromArgb(0x55, 255, 255, 255)),
        FocusVisualStyle = null,
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Cursor = Cursors.IBeam,
    };

    void FlushStream()
    {
        if (!_streamDirty)
        {
            _render.Stop();
            return;
        }
        _streamDirty = false;
        if (_streamBox is null)
        {
            _streamBox = NewAnswerBox();
            Thread.Children.Add(_streamBox);
        }
        _streamBox.Document = Markdown.Render(_stream.ToString(), B("TextBrush"), B("TextDim"), B("Sunken"),
                                              (FontFamily)FindResource("Sans"), (FontFamily)FindResource("Mono"));
        ScrollIfFollowing();
    }

    // ============================================================ IAgentHost
    void IAgentHost.StreamText(string chunk)
    {
        if (_voiceTurn is not null) _voice.Speaker.Feed(chunk);
        Dispatcher.BeginInvoke(() =>
        {
            HideThinking();
            _stream.Append(chunk);
            _streamDirty = true;
            if (!_render.IsEnabled) _render.Start();
        });
    }

    void IAgentHost.EndStream() => Dispatcher.BeginInvoke(() =>
    {
        FlushStream();
        _render.Stop();
        _streamBox = null;
        _stream.Clear();
    });

    void IAgentHost.ToolStarted(string tool, string summary, Risk risk) => Dispatcher.BeginInvoke(() =>
    {
        HideThinking();
        _lastTool = new ToolRow(this, tool, summary, risk);
        Arrive(_lastTool.Root);
        Thread.Children.Add(_lastTool.Root);
        ScrollIfFollowing();
    });

    void IAgentHost.ToolFinished(string tool, string output, bool ok) => Dispatcher.BeginInvoke(() =>
    {
        _lastTool?.Finish(output, ok);
        if (Busy) ShowThinking();   // the model is reading the result
        ScrollIfFollowing();
    });

    void IAgentHost.Note(string text) => Dispatcher.BeginInvoke(() => AddLine(text, "TextFaint", 11.5));
    void IAgentHost.Warn(string text) => Dispatcher.BeginInvoke(() => AddLine(text, "Moderate"));

    void IAgentHost.Error(string text) => Dispatcher.BeginInvoke(() =>
    {
        HideThinking();
        AddLine(text, "High", 13);
        if (_shape != Shape.Panel || !IsVisible) Toast(text, B("High"));
        if (_voiceTurn is { } language) _voice.Speaker.Feed("\n" + Phrases.Get("trouble", language) + "\n");
    });

    void IAgentHost.PlanChanged(IReadOnlyList<PlanStep> steps)
    {
        var copy = steps.ToList();
        Dispatcher.BeginInvoke(() => DrawPlan(copy));
    }

    void DrawPlan(List<PlanStep> steps)
    {
        PlanStrip.Visibility = steps.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var done = steps.Count(s => s.Status == "done");
        PlanCount.Text = done + "/" + steps.Count;
        PlanBar.HorizontalAlignment = HorizontalAlignment.Stretch;
        PlanBar.Width = double.NaN;
        PlanBar.RenderTransformOrigin = new Point(0, 0.5);
        PlanBar.RenderTransform = new ScaleTransform(steps.Count == 0 ? 0 : done / (double)steps.Count, 1);

        PlanList.Children.Clear();
        foreach (var (step, index) in steps.Select((s, i) => (s, i)))
        {
            var (mark, colour) = step.Status switch
            {
                "done" => ("✓", "TextFaint"),
                "doing" => ("›", "TextBrush"),
                "blocked" => ("!", "High"),
                _ => ("·", "TextDim"),
            };
            var row = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };
            row.Children.Add(new TextBlock { Text = mark, Width = 16, Foreground = B(step.Status == "blocked" ? "High" : colour), FontSize = 12.5 });
            row.Children.Add(new TextBlock
            {
                Text = (index + 1) + ". " + step.Text + (step.Note.Length > 0 ? "  - " + step.Note : ""),
                Foreground = B(colour),
                FontSize = 12.5,
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontWeight = step.Status == "doing" ? FontWeights.SemiBold : FontWeights.Normal,
            });
            PlanList.Children.Add(row);
            if (PlanList.Children.Count >= 8) break;
        }
    }

    // ================================================================ approval
    /// <summary>Called on the agent's worker thread; blocks it until a person answers.
    /// Keyboard or mouse only - never a voice, which a video playing nearby could fake.</summary>
    Answer IAgentHost.Confirm(GuardAction action)
    {
        var answer = new TaskCompletionSource<Answer>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.Invoke(() => ShowApproval(action, answer));
        var token = _agent?.Context.Cancel ?? CancellationToken.None;
        using (token.Register(() => answer.TrySetResult(Answer.No)))
        {
            var result = answer.Task.GetAwaiter().GetResult();
            Dispatcher.Invoke(CloseApproval);
            return result;
        }
    }

    void ShowApproval(GuardAction action, TaskCompletionSource<Answer> answer)
    {
        var foreground = Native.GetForegroundWindow();
        _returnFocusTo = foreground != _hwnd ? foreground : _lastForeground;
        _approval = answer;
        _approvalPending = true;
        _approvalArmed = false;

        _hiddenByUser = false;
        if (_yielded) { _yielded = false; Topmost = true; }
        if (!IsVisible) Show();
        GoTo(Shape.Panel);

        var risk = action.Verdict.Risk;
        var colour = (SolidColorBrush)B(risk switch { Risk.Safe => "Safe", Risk.Moderate => "Moderate", _ => "High" });
        RiskPill.BorderBrush = colour;
        RiskPill.Background = new SolidColorBrush(Color.FromArgb(0x22, colour.Color.R, colour.Color.G, colour.Color.B));
        RiskText.Foreground = colour;
        RiskText.Text = risk.Label().ToUpperInvariant();
        var (icon, verb) = Describe(action.Tool);
        ApprovalIcon.Text = icon;
        ApprovalIcon.Foreground = colour;
        ApprovalTool.Text = verb;
        ApprovalSummary.Text = Core.Tools.Text.Clip(action.Summary, 320);
        ApprovalReason.Text = action.Verdict.Reason.Length > 0 ? char.ToUpper(action.Verdict.Reason[0]) + action.Verdict.Reason[1..] : "";
        ApprovalDetail.Text = action.Detail;
        ApprovalDetailBox.Visibility = action.Detail.Trim().Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ApprovalNote.Text = Rules.AlwaysAsk.Contains(action.Tool)
            ? "Mouse, keyboard and screen are confirmed every single time. There is no “always” for these, and a voice cannot approve them."
            : risk == Risk.High ? "High-risk actions are asked every time." : "";
        ApprovalNoteRow.Visibility = ApprovalNote.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        AlwaysButton.Visibility = action.CanAlwaysAllow ? Visibility.Visible : Visibility.Collapsed;

        // a beat before it can be answered, so a key already on its way down does not answer it
        AllowButton.IsEnabled = DenyButton.IsEnabled = AlwaysButton.IsEnabled = false;
        var arm = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        arm.Tick += (_, _) =>
        {
            arm.Stop();
            if (_approval != answer) return;
            _approvalArmed = true;
            AllowButton.IsEnabled = DenyButton.IsEnabled = AlwaysButton.IsEnabled = true;
        };
        arm.Start();

        Scrim.Fade(true, 160);
        Activate();
        Approval.Focusable = true;
        Keyboard.Focus(Approval);
        Native.FlashWindow(_hwnd, true);
        UpdateTray();
        if (_voiceTurn is { } language) _voice.Speaker.Feed("\n" + Phrases.Get("approve", language) + "\n");
        if (!IsActive) _tray.Tell("Vigil needs your approval", action.Summary);
    }

    void OnApprovalKey(KeyEventArgs e)
    {
        e.Handled = true;
        if (!_approvalArmed) return;
        switch (e.Key)
        {
            case Key.Y: AnswerApproval(Answer.Yes); break;
            case Key.N:
            case Key.Escape: AnswerApproval(Answer.No); break;
            case Key.A when AlwaysButton.Visibility == Visibility.Visible: AnswerApproval(Answer.Always); break;
        }
    }

    void AnswerApproval(Answer answer)
    {
        if (!_approvalPending || (!_approvalArmed && answer != Answer.No)) return;
        _approvalPending = false;
        _approvalArmed = false;
        _approval?.TrySetResult(answer);
    }

    void CloseApproval()
    {
        _approvalPending = false;
        _approval = null;
        Scrim.Fade(false, 140);
        UpdateTray();
    }

    // ================================================================ asking
    string? IAgentHost.AskUser(string question, IReadOnlyList<string> choices, CancellationToken ct)
    {
        var answer = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.Invoke(() => ShowAsk(question, choices, answer));
        using (ct.Register(() => answer.TrySetResult(null)))
        {
            var result = answer.Task.GetAwaiter().GetResult();
            Dispatcher.Invoke(() =>
            {
                _askPending = false;
                AskCard.Fade(false, 140);
                PlaceholderText.Text = "Ask Vigil anything…";
                PlaceholderHint.Text = "   or say “Hey Vigil”  ·  Ctrl+<";
                UpdateTray();
            });
            return result;
        }
    }

    void ShowAsk(string question, IReadOnlyList<string> choices, TaskCompletionSource<string?> answer)
    {
        _ask = answer;
        _askPending = true;
        AskText.Text = question;
        AskChoices.Children.Clear();
        foreach (var choice in choices)
        {
            var button = new Button { Content = choice, Style = (Style)FindResource("Chip"), Margin = new Thickness(0, 0, 6, 6) };
            button.Click += (_, _) => AnswerAsk(choice);
            AskChoices.Children.Add(button);
        }
        AskChoices.Visibility = choices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        AskCard.Fade(true, 180);
        PlaceholderText.Text = "Type your answer…";
        PlaceholderHint.Text = _voiceTurn is not null ? "   or just say it" : "";
        _hiddenByUser = false;
        if (!IsVisible) Show();
        GoTo(Shape.Panel, focus: true);
        UpdateTray();

        if (_voiceTurn is { } language)
        {
            _voice.Speaker.Feed("\n" + question + "\n");
            _ = HearAnswer(answer, language);
        }
    }

    async Task HearAnswer(TaskCompletionSource<string?> pending, string language)
    {
        await _voice.Speaker.IdleAsync();
        if (pending.Task.IsCompleted) return;
        var heard = await _voice.ListenAsync(TimeSpan.FromSeconds(8), null, false, chime: true);
        if (heard is not null && !pending.Task.IsCompleted)
            await Dispatcher.InvokeAsync(() => AnswerAsk(heard.Text));
    }

    void AnswerAsk(string? answer)
    {
        if (!_askPending || _ask is null) return;
        _askPending = false;
        if (!string.IsNullOrWhiteSpace(answer)) AddUser(answer, spoken: false);
        _ask.TrySetResult(answer);
    }

    // ============================================================ hands off
    /// <summary>While Vigil holds the pointer or keyboard: the edge glows, the bar steps out
    /// from under the pointer, and focus goes back to the app the person was using.</summary>
    IDisposable IAgentHost.Driving()
    {
        var target = IntPtr.Zero;
        Dispatcher.Invoke(() =>
        {
            _drivers++;
            _reshow.Stop();
            target = _returnFocusTo != IntPtr.Zero ? _returnFocusTo : _lastForeground;
            if (IsVisible)
            {
                _hiddenForDriving = true;
                Hide();
            }
        });
        if (target != IntPtr.Zero && Native.GetForegroundWindow() != target)
        {
            Native.Focus(target);
            System.Threading.Thread.Sleep(150);
        }
        var glow = _glow.Hold();
        return new Undo(() =>
        {
            glow.Dispose();
            Dispatcher.BeginInvoke(() =>
            {
                if (--_drivers <= 0)
                {
                    _drivers = 0;
                    _reshow.Start();
                }
            });
        });
    }

    IDisposable IAgentHost.OutOfShot()
    {
        Dispatcher.Invoke(() => Native.SetWindowDisplayAffinity(_hwnd, Native.WDA_EXCLUDEFROMCAPTURE));
        return new Undo(() => Dispatcher.Invoke(() => Native.SetWindowDisplayAffinity(_hwnd, 0)));
    }

    sealed class Undo(Action undo) : IDisposable
    {
        int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) undo();
        }
    }

    // ============================================================== tool rows
    /// <summary>An icon and a few words for each tool, used on step rows and the approval card.</summary>
    static (string Icon, string Verb) Describe(string tool) => tool switch
    {
        "open_app" => ("\uE8A7", "Open an app or file"),
        "manage_path" => ("\uE8B7", "Work with files"),
        "plan" => ("\uE9D5", "Plan"),
        "memory" => ("\uE734", "Use its memory"),
        "system" => ("\uE7F4", "Check the system"),
        "look" => ("\uE722", "Look at your screen"),
        "mouse" => ("\uE962", "Use your mouse"),
        "keyboard" => ("\uE765", "Use your keyboard"),
        "windows" => ("\uE737", "Switch windows"),
        "browser" => ("\uE774", "Use the browser"),
        "run_command" => ("\uE756", "Run a command"),
        "change_dir" or "current_dir" => ("\uE8B7", "Change folder"),
        "read_file" => ("\uE8A5", "Read a file"),
        "write_file" or "edit_file" => ("\uE70F", "Change a file"),
        "list_dir" or "find_files" or "make_dir" or "copy_path" or "move_path" => ("\uE8B7", "Work with files"),
        "search_text" => ("\uE721", "Search in files"),
        "delete_path" => ("\uE74D", "Delete permanently"),
        "screen_capture" or "browser_screenshot" => ("\uE722", "Look at your screen"),
        "click_on" or "mouse_click" or "mouse_move" or "mouse_scroll" => ("\uE962", "Use your mouse"),
        "keyboard_type" or "press_keys" => ("\uE765", "Use your keyboard"),
        "clipboard" => ("\uE77F", "Use your clipboard"),
        "list_windows" or "focus_window" => ("\uE737", "Switch windows"),
        "kill_process" => ("\uE711", "End a program"),
        "clean_temp" => ("\uE74D", "Clean up temporary files"),
        "system_info" or "disk_usage" or "network_info" or "list_processes" or "list_installed_apps" => ("\uE7F4", "Check the system"),
        "current_time" => ("\uE823", "Check the time"),
        "remember" or "recall" or "forget" => ("\uE734", "Use its memory"),
        "create_plan" or "update_plan" or "show_plan" => ("\uE9D5", "Plan"),
        "ask_user" => ("\uE897", "Ask you"),
        _ when tool.StartsWith("browser_") => ("\uE774", "Use the browser"),
        _ => ("\uE945", tool.Replace('_', ' ')),
    };

    /// <summary>One step the agent took: what kind, what exactly, how it went. Click to see
    /// what the tool returned.</summary>
    sealed class ToolRow
    {
        readonly ShellWindow _w;
        readonly Grid _badge;
        readonly TextBlock _icon;
        readonly System.Windows.Shapes.Ellipse _spinner;
        readonly TextBlock _status;
        readonly Border _output;
        readonly TextBlock _outputText;
        readonly Border _face;

        public Border Root { get; }

        public ToolRow(ShellWindow window, string tool, string summary, Risk risk)
        {
            _w = window;
            var (glyph, _) = Describe(tool);

            // the badge: a soft square with the tool's icon, and a ring that turns while it runs
            _icon = new TextBlock
            {
                Text = glyph,
                FontFamily = (FontFamily)window.FindResource("Icons"),
                FontSize = 12,
                Foreground = window.B("TextDim"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            _spinner = new System.Windows.Shapes.Ellipse
            {
                Width = 26,
                Height = 26,
                Stroke = window.B("CoreRing"),
                StrokeThickness = 1.4,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new RotateTransform(),
            };
            if (!Motion.Reduced)
                ((RotateTransform)_spinner.RenderTransform).BeginAnimation(RotateTransform.AngleProperty,
                    new System.Windows.Media.Animation.DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9))
                    { RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever });
            _badge = new Grid { Width = 26, Height = 26, Margin = new Thickness(0, 0, 11, 0), VerticalAlignment = VerticalAlignment.Center };
            _badge.Children.Add(new Border { CornerRadius = new CornerRadius(8), Background = window.B("Surface2"), Margin = new Thickness(2) });
            _badge.Children.Add(_spinner);
            _badge.Children.Add(_icon);

            _status = new TextBlock
            {
                FontFamily = (FontFamily)window.FindResource("Icons"),
                FontSize = 11,
                Foreground = window.B("TextFaint"),
                Margin = new Thickness(10, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };

            var line = new DockPanel();
            line.Children.Add(_badge);
            DockPanel.SetDock(_status, Dock.Right);
            line.Children.Add(_status);
            if (risk >= Risk.High)
            {
                var tag = new Border
                {
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(6, 1, 6, 2),
                    Margin = new Thickness(8, 0, 0, 0),
                    BorderBrush = window.B("High"),
                    BorderThickness = new Thickness(1),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock { Text = "high risk", FontSize = 10, Foreground = window.B("High") },
                };
                DockPanel.SetDock(tag, Dock.Right);
                line.Children.Add(tag);
            }
            line.Children.Add(new TextBlock
            {
                Text = summary,
                FontSize = 13,
                Foreground = window.B("TextDim"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = summary.Length > 70 ? summary : null,
            });

            _outputText = new TextBlock
            {
                FontFamily = (FontFamily)window.FindResource("Mono"),
                FontSize = 11.5,
                LineHeight = 17,
                Foreground = window.B("TextDim"),
                TextWrapping = TextWrapping.Wrap,
            };
            _output = new Border
            {
                Background = window.B("Sunken"),
                BorderBrush = window.B("Edge"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 9, 12, 9),
                Margin = new Thickness(37, 6, 0, 4),
                Visibility = Visibility.Collapsed,
                Child = new ScrollViewer { MaxHeight = 200, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _outputText },
            };

            _face = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(4, 3, 6, 3), Background = Brushes.Transparent, Child = line };
            var stack = new StackPanel();
            stack.Children.Add(_face);
            stack.Children.Add(_output);
            Root = new Border { Child = stack, Margin = new Thickness(-4, 0, 0, 4), Cursor = Cursors.Arrow };
            Root.MouseEnter += (_, _) => { if (_outputText.Text.Length > 0) _face.Background = window.B("Hover"); };
            Root.MouseLeave += (_, _) => _face.Background = Brushes.Transparent;
            Root.MouseLeftButtonUp += (_, _) =>
            {
                if (_outputText.Text.Length > 0)
                    _output.Visibility = _output.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            };
        }

        public void Finish(string output, bool ok)
        {
            ((RotateTransform)_spinner.RenderTransform).BeginAnimation(RotateTransform.AngleProperty, null);
            _spinner.Visibility = Visibility.Collapsed;
            _icon.Foreground = ok ? _w.B("TextDim") : _w.B("High");
            _status.Text = ok ? "\uE73E" : "\uE711";
            _status.Foreground = ok ? _w.B("Safe") : _w.B("High");
            _outputText.Text = output.Length > 6000 ? output[..6000] + "\n…" : output;
            if (output.Length > 0) Root.Cursor = Cursors.Hand;
            if (!ok && output.Length > 0) _output.Visibility = Visibility.Visible;
        }
    }
}

/// <summary>The few things Vigil says on its own, in the languages it is most often spoken to in.
/// Everything else it says comes from the model, in whatever language was used.</summary>
static class Phrases
{
    static readonly Dictionary<string, Dictionary<string, string>> Table = new()
    {
        ["approve"] = new()
        {
            ["en"] = "I need your approval on screen for that.",
            ["tr"] = "Bunun için ekranda onayını almam gerekiyor.",
            ["de"] = "Dafür brauche ich deine Bestätigung auf dem Bildschirm.",
            ["es"] = "Necesito tu aprobación en la pantalla para eso.",
            ["fr"] = "J'ai besoin de ton accord à l'écran pour ça.",
        },
        ["trouble"] = new()
        {
            ["en"] = "I ran into a problem. The details are on screen.",
            ["tr"] = "Bir sorunla karşılaştım. Ayrıntılar ekranda.",
            ["de"] = "Da gab es ein Problem. Die Details stehen auf dem Bildschirm.",
            ["es"] = "Tuve un problema. Los detalles están en la pantalla.",
            ["fr"] = "J'ai rencontré un problème. Les détails sont à l'écran.",
        },
    };

    public static string Get(string key, string language)
    {
        var code = Voice.Speaker.Code(language);
        return Table[key].TryGetValue(code, out var text) ? text : Table[key]["en"];
    }
}
