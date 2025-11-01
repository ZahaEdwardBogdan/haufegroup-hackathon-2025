using Avalonia.Controls;
using AvaloniaEdit;
using Avalonia.Data;
using System;
using Avalonia;
using System.Threading.Tasks;
using System.Threading;
using Avalonia.Threading;
using System.Collections.Generic;

namespace HaufeApp.Markup
{
    // This adapter is required to handle the binding gap between Compiled Bindings and TextEditor.Text
    public static class AvaloniaEditAdapter
    {
        // 300ms debounce delay for UI to VM updates
        private const int DebounceDelayMs = 300; 

        // Attached property for the string content (used by XAML binding)
        public static readonly AttachedProperty<string> EditorTextProperty =
            AvaloniaProperty.RegisterAttached<Control, TextEditor, string>(
                "EditorText", 
                defaultValue: string.Empty, 
                inherits: false, 
                defaultBindingMode: BindingMode.TwoWay);

        // Attached property to store the CancellationTokenSource for debouncing (per instance)
        private static readonly AttachedProperty<CancellationTokenSource> DebouncerTokenProperty =
            AvaloniaProperty.RegisterAttached<Control, TextEditor, CancellationTokenSource>(
                "DebouncerToken", 
                defaultValue: null!,
                inherits: false);

        // Attached property to track internal updates to prevent re-entrancy (The FIX)
        private static readonly AttachedProperty<bool> IsUpdatingInternallyProperty =
            AvaloniaProperty.RegisterAttached<Control, TextEditor, bool>(
                "IsUpdatingInternally", 
                defaultValue: false,
                inherits: false);
                
        // NEW: Attached property to ensure we only subscribe to TextChanged once per instance.
        private static readonly AttachedProperty<bool> IsSubscribedProperty =
            AvaloniaProperty.RegisterAttached<Control, TextEditor, bool>(
                "IsSubscribed",
                defaultValue: false,
                inherits: false);


        static AvaloniaEditAdapter()
        {
            EditorTextProperty.Changed.AddClassHandler<TextEditor>((editor, e) => OnEditorTextPropertyChanged(editor, e));
        }

        public static string GetEditorText(TextEditor element) => element.GetValue(EditorTextProperty);
        public static void SetEditorText(TextEditor element, string value) => element.SetValue(EditorTextProperty, value);
        
        // Helper accessors for attached properties
        private static CancellationTokenSource GetDebouncerToken(TextEditor element) => element.GetValue(DebouncerTokenProperty);
        private static void SetDebouncerToken(TextEditor element, CancellationTokenSource value) => element.SetValue(DebouncerTokenProperty, value);
        private static bool GetIsUpdatingInternally(TextEditor element) => element.GetValue(IsUpdatingInternallyProperty);
        private static void SetIsUpdatingInternally(TextEditor element, bool value) => element.SetValue(IsUpdatingInternallyProperty, value);
        private static bool GetIsSubscribed(TextEditor element) => element.GetValue(IsSubscribedProperty);
        private static void SetIsSubscribed(TextEditor element, bool value) => element.SetValue(IsSubscribedProperty, value);


        private static void OnEditorTextPropertyChanged(TextEditor editor, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == EditorTextProperty)
            {
                // FIX: If the change originated from the UI -> VM flow (via the debounce handler), 
                // we skip the VM -> UI update below to prevent recursion.
                if (GetIsUpdatingInternally(editor))
                {
                    return;
                }
                
                // VM -> UI Flow: Update TextEditor's content directly.
                if (e.NewValue is string newText)
                {
                    // Only update the TextEditor if the text is different to prevent unnecessary DOM updates.
                    if (editor.Text != newText)
                    {
                        editor.Text = newText; 
                    }
                }

                // FIX: Subscribe to TextChanged only once, regardless of the property's initial value state.
                if (!GetIsSubscribed(editor))
                {
                    editor.TextChanged += (sender, args) =>
                    {
                        HandleTextChangeWithDebounce(editor);
                    };
                    SetIsSubscribed(editor, true);
                }
            }
        }

        private static void HandleTextChangeWithDebounce(TextEditor editor)
        {
            // 1. Cancel any previous pending update task for this editor instance.
            var cts = GetDebouncerToken(editor);
            cts?.Cancel();

            // 2. Create a new token for the new debounced task.
            // Using a new token source for each debounce request is crucial for reliability.
            cts = new CancellationTokenSource();
            SetDebouncerToken(editor, cts);
            var token = cts.Token;

            // 3. Start the debounced update task.
            // We use Task.Run for the delay to avoid blocking the UI thread.
            Task.Run(async () =>
            {
                try
                {
                    // Wait for the debounce delay.
                    await Task.Delay(DebounceDelayMs, token);

                    // If the token hasn't been cancelled, marshal the update back to the UI thread.
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        // Set the flag to true *before* setting the attached property.
                        SetIsUpdatingInternally(editor, true);

                        try
                        {
                            // UI -> VM Flow: Update the attached property. 
                            // This successfully flows back to the ViewModel property (TextContent).
                            // This MUST be done on the UI thread.
                            SetEditorText(editor, editor.Text);
                        }
                        finally
                        {
                            // Always clear the flag afterward.
                            SetIsUpdatingInternally(editor, false);
                        }
                    });
                }
                catch (TaskCanceledException)
                {
                    // Expected when the user types again before the delay finishes.
                }
                catch (Exception ex)
                {
                    // If an error occurs, try to reset the flag and log the error.
                    // Use Post() as a fallback for error handling to avoid potential deadlocks if InvokeAsync fails.
                    Dispatcher.UIThread.Post(() => SetIsUpdatingInternally(editor, false));
                    Console.WriteLine($"Debounce error: {ex.Message}");
                }
            });
        }
    }
}
