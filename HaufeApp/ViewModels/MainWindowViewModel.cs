using Avalonia.Platform.Storage;
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using OllamaSharp;
using System.Windows.Input; // Required for ICommand
using Avalonia.Threading;
using OllamaSharp.Models;
using OllamaSharp.Models.Chat; // Make sure to include this namespace
using System.Text;
using System.Threading.Tasks;
using System.Linq; // Keep for List.ToArray()
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Net.Http;
using System.Net.Mime;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Data;
using Avalonia.Rendering.Composition;
using AvaloniaEdit;
using HaufeApp.Views; // For [CallerMemberName] in SetProperty

namespace HaufeApp.ViewModels;

public class AsyncCommand : ICommand
{
    private readonly Func<Task> _execute;
    private bool _isExecuting;

    public event EventHandler? CanExecuteChanged;

    public AsyncCommand(Func<Task> execute)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
    }

    // Returns true if the command is not currently executing
    public bool CanExecute(object? parameter) => !_isExecuting;

    // Suppress warning about async void since ICommand.Execute must be void
    [SuppressMessage("Usage", "AsyncVoid", Justification = "Required for ICommand implementation.")]
    public async void Execute(object? parameter)
    {
        if (CanExecute(parameter))
        {
            try
            {
                _isExecuting = true;
                OnCanExecuteChanged(); // Notify the UI that CanExecute status changed

                // Await the asynchronous method
                await _execute();
            }
            finally
            {
                _isExecuting = false;
                OnCanExecuteChanged(); // Notify the UI that CanExecute status changed
            }
        }
    }

    // Ensures CanExecuteChanged event is raised on the UI thread
    public virtual void OnCanExecuteChanged()
    {
        // Use Avalonia's Dispatcher to marshal the event back to the UI thread
        Dispatcher.UIThread.Post(() => CanExecuteChanged?.Invoke(this, EventArgs.Empty));
    }
}

// Main ViewModel now inherits from standard ViewModelBase
public class MainWindowViewModel : ViewModelBase
{
    private string _textContent;
    private string _reviewContent;
    private string _repairContent;
    private string _guidelineContent;
    private string _focusContent;
    private IStorageFile? _currentFile;
    private readonly IStorageProvider _storageProvider;
    private bool _isProcessing;

    public string TextContent
    {
        get => _textContent;
        set => SetProperty(ref _textContent, value);
    }

    public string ReviewContent
    {
        get => _reviewContent;
        set => SetProperty(ref _reviewContent, value);
    }
    public string RepairContent
    {
        get => _reviewContent;
        set => SetProperty(ref _reviewContent, value);
    }

    public string GuidelineContent
    {
        get => _guidelineContent;
        set => SetProperty(ref _guidelineContent, value);
    }
    public string FocusContent
    {
        get => _focusContent;
        set => SetProperty(ref _focusContent, value);
    }

    public bool IsProcessing
    {
        get => _isProcessing;
        set => SetProperty(ref _isProcessing, value);
    }

    public bool CanProcess => !IsProcessing && !string.IsNullOrWhiteSpace(TextContent);

    public IStorageFile? CurrentFile
    {
        get => _currentFile;
        private set
        {
            if (SetProperty(ref _currentFile, value))
            {
                // Manually raise property change for WindowTitle when CurrentFile changes
                OnPropertyChanged(nameof(WindowTitle));
            }
        }
    }

    // Derived property for the Window Title
    public string WindowTitle => $"Code Editor - {(CurrentFile?.Name ?? "Untitled")}";


    // Commands now use the standard ICommand interface
    public ICommand OpenFileCommand { get; }
    public ICommand SaveFileCommand { get; }
    public ICommand SaveFileAsCommand { get; }
    public ICommand ReviewCodeCommand { get; }
    public ICommand RepairCodeCommand { get; }
    

    public MainWindowViewModel(IStorageProvider storageProvider)
    {
        _storageProvider = storageProvider;

        // Initialize commands using the custom AsyncCommand
        OpenFileCommand = new AsyncCommand(OpenFileAsync);
        SaveFileCommand = new AsyncCommand(SaveFileAsync);
        SaveFileAsCommand = new AsyncCommand(SaveFileAsAsync);
        ReviewCodeCommand = new AsyncCommand(ReviewCodeAsync);
        RepairCodeCommand = new AsyncCommand(RepairCodeAsync);

        PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(IsProcessing) || e.PropertyName == nameof(TextContent))
            {
                // Manually trigger CanExecuteChanged on the ProcessCommand
                // This now works because OnCanExecuteChanged is public.
                Dispatcher.UIThread.Post(() => ((AsyncCommand)ReviewCodeCommand).OnCanExecuteChanged());
            }
        };
    }

    public MainWindowViewModel()
    {
    }

    private async Task OpenFileAsync()
    {
        var fileTypesList = new List<FilePickerFileType>
        {
            new("C# Source Files") { Patterns = new[] { "*.cs" }, MimeTypes = new[] { "text/plain" } },
            new("C/C++ Source Files")
                { Patterns = new[] { "*.cpp", "*.c", "*.h" }, MimeTypes = new[] { "text/plain" } },
            new("All Files (*.*)") { Patterns = new[] { "*" }, MimeTypes = new[] { "*" } }
        };

        var options = new FilePickerOpenOptions
        {
            Title = "Select a Code File to Open",
            AllowMultiple = false,
            FileTypeFilter = fileTypesList.ToArray()
        };

        // 1. InvokeAsync is still necessary to call the native file dialog on the UI thread.
        var files = await Dispatcher.UIThread.InvokeAsync(() => _storageProvider.OpenFilePickerAsync(options)
        );

        if (files.Count > 0)
        {
            IStorageFile fileToOpen = files[0];
            string fileContent = string.Empty;

            try
            {
                // 2. Perform file I/O (reading) safely off-thread
                await using var stream = await fileToOpen.OpenReadAsync().ConfigureAwait(false);
                using var reader = new StreamReader(stream);
                fileContent = await reader.ReadToEndAsync().ConfigureAwait(false);
                Console.WriteLine($"File opened: {fileToOpen.Path}");
            }
            catch (Exception ex)
            {
                fileContent = $"Error reading file: {ex.Message}";
                Console.WriteLine(fileContent);
            }

            // 3. InvokeAsync ensures property setters run on the UI thread.
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                CurrentFile = fileToOpen;
                TextContent = fileContent;
            });
        }
    }

    private async Task SaveFileAsync()
    {
        if (CurrentFile != null)
        {
            await WriteToFileAsync(CurrentFile);
        }
        else
        {
            await SaveFileAsAsync();
        }
    }

    private async Task SaveFileAsAsync()
    {
        var fileTypesList = new List<FilePickerFileType>
        {
            new("C# Source Files") { Patterns = new[] { "*.cs" }, MimeTypes = new[] { "text/plain" } },
            new("C/C++ Source Files")
                { Patterns = new[] { "*.cpp", "*.c", "*.h" }, MimeTypes = new[] { "text/plain" } },
            new("All Files (*.*)") { Patterns = new[] { "*" }, MimeTypes = new[] { "*" } }
        };

        var options = new FilePickerSaveOptions
        {
            Title = "Save Code File As",
            SuggestedFileName = CurrentFile?.Name ?? "Untitled.txt",
            FileTypeChoices = fileTypesList.ToArray(),
            ShowOverwritePrompt = true
        };

        // 1. InvokeAsync is still necessary to call the native file dialog on the UI thread.
        var file = await Dispatcher.UIThread.InvokeAsync(() => _storageProvider.SaveFilePickerAsync(options)
        );

        if (file != null)
        {
            // 2. Perform file I/O (writing) safely off-thread
            await WriteToFileAsync(file);

            // 3. InvokeAsync ensures the property setter runs on the UI thread.
            await Dispatcher.UIThread.InvokeAsync(() => { CurrentFile = file; });
        }
    }

    private async Task WriteToFileAsync(IStorageFile file)
    {
        try
        {
            // Use ConfigureAwait(false) on I/O operations too.
            await using var stream = await file.OpenWriteAsync().ConfigureAwait(false);
            using var writer = new StreamWriter(stream);
            await writer.WriteAsync(TextContent).ConfigureAwait(false);
            Console.WriteLine($"File saved successfully: {file.Path}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error writing file: {ex.Message}");
        }
    }
    
    private async Task RepairCodeAsync()
        {
            if (string.IsNullOrWhiteSpace(TextContent))
            {
                await Dispatcher.UIThread.InvokeAsync(() => RepairContent = "Error: Please enter some code to correct.");
                return;
            }

            IsProcessing = true;
            string originalContent = TextContent;

            // 1. Initial status and clear previous output
            await Dispatcher.UIThread.InvokeAsync(() => RepairContent = "Correcting code... (Starting stream)");

            try
            {
                // Configuration for local Ollama instance
                var ollamaUri = new Uri("http://localhost:11434");
                var ollama = new OllamaApiClient(ollamaUri)
                {
                    SelectedModel = "qwen2.5:0.5b" // Use Llama 3
                };

                // System message tailored for CORRECTION (respond ONLY with code)
                var systemMessageContent = "You are an expert C# code reviewer and fixer. Your task is to analyze the provided code, fix all bugs, improve readability, and adhere to C# best practices. Respond ONLY with the complete, corrected, and improved C# code. Do not include any explanations, markdown fences (like ```csharp), or introductory text. Extra focus on eliminating ```.";

                var chat = new Chat(ollama);

                // Add System message
                chat.Messages.Add(new Message(ChatRole.System, systemMessageContent));

                var userPrompt = $"Please review and return the corrected C# code:\n\n{originalContent}";

                // --- THROTTLING SETUP (New) ---
                const int UpdateIntervalMs = 50; // Update UI every 50 milliseconds
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                string accumulatedText = string.Empty;
                
                // Reset the TextContent before starting the stream to clear the status message
                await Dispatcher.UIThread.InvokeAsync(() => RepairContent = string.Empty);

                // 2. Stream the response and update the property (THROTTLED)
                await foreach (var answerToken in chat.SendAsync(userPrompt))
                {
                    accumulatedText += answerToken;
                    
                    // NEW: Introduce a deliberate delay to limit Ollama's resource usage.
                    await Task.Delay(50); // Delay 50ms per token to throttle LLM output

                    // Check if enough time has passed since the last UI update
                    if (stopwatch.ElapsedMilliseconds >= UpdateIntervalMs)
                    {
                        // Dispatch the full accumulated text to the UI thread
                        string textToUpdate = accumulatedText;
                        Dispatcher.UIThread.Post(() => RepairContent = textToUpdate);

                        // Restart the stopwatch for the next interval
                        stopwatch.Restart();
                    }
                }

                // 3. Final Update (Mandatory for the last remaining tokens)
                stopwatch.Stop();
                // Ensure the final accumulated text is posted to the UI thread.
                await Dispatcher.UIThread.InvokeAsync(() => RepairContent = accumulatedText);

                // Final Console confirmation
                Console.WriteLine("Ollama correction stream finished successfully.");
            }
            catch (HttpRequestException httpEx)
            {
                // Catch connection errors and display them immediately.
                string errorMessage = $"Network Error: Failed to connect to Ollama. Is it running at http://localhost:11434? Details: {httpEx.Message}";
                await Dispatcher.UIThread.InvokeAsync(() => RepairContent = errorMessage);
            }
            catch (Exception ex)
            {
                // Catch other errors
                string errorMessage = $"Error during code correction: {ex.Message}";
                await Dispatcher.UIThread.InvokeAsync(() => RepairContent = errorMessage);
            }
            finally
            {
                // Always set processing to false after completion or error
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    IsProcessing = false;
                });
            }
        }
        // Function 2: Code Review/Summary (Writes to ReviewContent)
        private async Task ReviewCodeAsync()
        {
            if (string.IsNullOrWhiteSpace(TextContent))
            {
                await Dispatcher.UIThread.InvokeAsync(() => ReviewContent = "Error: Please enter some code to review.");
                return;
            }

            IsProcessing = true;
            string originalContent = TextContent;

            // 1. Initial status and clear previous output
            await Dispatcher.UIThread.InvokeAsync(() => ReviewContent = "Reviewing code... (Starting stream)");

            try
            {
                // Configuration for local Ollama instance
                var ollamaUri = new Uri("http://localhost:11434");
                var ollama = new OllamaApiClient(ollamaUri)
                {
                    SelectedModel = "qwen2.5:0.5b" // Use Llama 3
                };

                // System message tailored for REVIEW (respond with analysis and markdown)
                var systemMessageContent = "You are an expert C# code reviewer. Provide a detailed summary of the code's purpose and a sectioned list of suggested improvements (e.g., Bugs, Readability, Performance, linting, security, architecture, testing, CI/CD, effort required to apply suggested changes). Be sure to include them all. Format your response using clear Markdown headings, while avoiding ``` for code formatting.";

                var chat = new Chat(ollama);
                chat.Messages.Add(new Message(ChatRole.System, systemMessageContent));

                var tempGuide = string.IsNullOrWhiteSpace(GuidelineContent) ? "" : "using the following coding guideline: " + GuidelineContent;
                var tempFocus = string.IsNullOrWhiteSpace(FocusContent) ? "" : "while putting a lot of focus on: " + FocusContent;
                
                var userPrompt = $"Please review " + tempGuide + tempFocus + " and summarize the following C# code:\n\n```csharp\n{originalContent}\n```";
                
                // --- THROTTLING SETUP ---
                const int UpdateIntervalMs = 50; // Update UI every 50 milliseconds
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                string accumulatedText = string.Empty;

                // Reset the ReviewContent and accumulated text
                await Dispatcher.UIThread.InvokeAsync(() => ReviewContent = string.Empty);

                // 2. Stream the response and update the property (THROTTLED)
                await foreach (var answerToken in chat.SendAsync(userPrompt))
                {
                    accumulatedText += answerToken;
                    
                    // NEW: Introduce a deliberate delay to limit Ollama's resource usage.
                    await Task.Delay(50); // Delay 50ms per token to throttle LLM output

                    // Check if enough time has passed since the last UI update
                    if (stopwatch.ElapsedMilliseconds >= UpdateIntervalMs)
                    {
                        // Dispatch the full accumulated text to the UI thread
                        string textToUpdate = accumulatedText;
                        Dispatcher.UIThread.Post(() => ReviewContent = textToUpdate);

                        // Restart the stopwatch for the next interval
                        stopwatch.Restart();
                    }
                }

                // 3. Final Update (Mandatory for the last remaining tokens)
                stopwatch.Stop();
                // Ensure the final accumulated text is posted to the UI thread.
                await Dispatcher.UIThread.InvokeAsync(() => ReviewContent = accumulatedText);

                // Final Console confirmation
                Console.WriteLine("Ollama review stream finished successfully.");
            }
            catch (HttpRequestException httpEx)
            {
                // Catch connection errors and display them immediately.
                string errorMessage = $"Network Error: Failed to connect to Ollama. Is it running at http://localhost:11434? Details: {httpEx.Message}";
                await Dispatcher.UIThread.InvokeAsync(() => ReviewContent = errorMessage);
            }
            catch (Exception ex)
            {
                // Catch other errors
                string errorMessage = $"Error during code review: {ex.Message}";
                await Dispatcher.UIThread.InvokeAsync(() => ReviewContent = errorMessage);
            }
            finally
            {
                // Always set processing to false after completion or error
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    IsProcessing = false;
                });
            }
        }
}