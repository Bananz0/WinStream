#nullable enable

using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using WinStream.Core.Logging;
using WinStream.Core.Streaming;

namespace WinStream;

/// <summary>
/// Presents the AirPlay pairing PIN and password <see cref="ContentDialog"/>s.
/// Concurrent callers for the same receiver join one in-flight prompt via
/// <see cref="SingleFlightPrompt"/>.
/// </summary>
internal sealed class PairingDialogPresenter
{
    private const int DialogSlotAttempts = 5;
    private const int DialogSlotRetryDelayMs = 400;

    private readonly DispatcherQueue _dispatcherQueue;
    private readonly Func<XamlRoot?> _xamlRoot;
    private readonly SingleFlightPrompt _pinPrompt = new();
    private readonly SingleFlightPrompt _passwordPrompt = new();

    public PairingDialogPresenter(DispatcherQueue dispatcherQueue, Func<XamlRoot?> xamlRoot)
    {
        ArgumentNullException.ThrowIfNull(dispatcherQueue);
        ArgumentNullException.ThrowIfNull(xamlRoot);
        _dispatcherQueue = dispatcherQueue;
        _xamlRoot = xamlRoot;
    }

    public Task<string> PromptForPinAsync(string receiverKey, CancellationToken cancellationToken) =>
        _pinPrompt.JoinOrStart(
            receiverKey,
            "pin",
            () => ShowCredentialDialogAsync(
                cancellationToken,
                "pin",
                title: "Enter AirPlay code",
                body: PairingCopy.PromptBody,
                primaryButton: PairingCopy.TrustButton,
                closeButton: PairingCopy.SkipButton,
                createInput: () =>
                {
                    var pinBox = new TextBox { PlaceholderText = "4-digit AirPlay code" };
                    AutomationProperties.SetName(pinBox, "AirPlay pairing code");
                    return pinBox;
                },
                readValue: box => ((TextBox)box).Text?.Trim() ?? string.Empty,
                onShown: null,
                onDismissed: null));

    public Task<string> PromptForPasswordAsync(string receiverKey, CancellationToken cancellationToken) =>
        _passwordPrompt.JoinOrStart(
            receiverKey,
            "password",
            () => ShowCredentialDialogAsync(
                cancellationToken,
                "password",
                title: PairingCopy.PasswordPromptTitle,
                body: PairingCopy.PasswordPromptBody,
                primaryButton: PairingCopy.PasswordButton,
                closeButton: PairingCopy.PasswordCancelButton,
                createInput: () =>
                {
                    var passwordBox = new PasswordBox { PlaceholderText = "AirPlay password" };
                    AutomationProperties.SetName(passwordBox, "AirPlay Receiver password");
                    return passwordBox;
                },
                readValue: box => ((PasswordBox)box).Password ?? string.Empty,
                onShown: () => AppLog.Info("password", "Password prompt shown."),
                onDismissed: result =>
                    AppLog.Info("password", $"Password prompt dismissed result={result}.")));

    private Task<string> ShowCredentialDialogAsync(
        CancellationToken cancellationToken,
        string category,
        string title,
        string body,
        string primaryButton,
        string closeButton,
        Func<FrameworkElement> createInput,
        Func<FrameworkElement, string> readValue,
        Action? onShown,
        Action<ContentDialogResult>? onDismissed)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        if (category == "password")
                        {
                            AppLog.Info("password", "Password prompt skipped: connect already cancelled.");
                        }

                        tcs.TrySetResult(string.Empty);
                        return;
                    }

                    var input = createInput();
                    var dialog = new ContentDialog
                    {
                        Title = title,
                        Content = new StackPanel
                        {
                            Spacing = 12,
                            Children =
                            {
                                new TextBlock
                                {
                                    Text = body,
                                    TextWrapping = TextWrapping.WrapWholeWords
                                },
                                input
                            }
                        },
                        PrimaryButtonText = primaryButton,
                        CloseButtonText = closeButton,
                        DefaultButton = ContentDialogButton.Primary,
                        XamlRoot = _xamlRoot()
                    };

                    using var reg = cancellationToken.Register(() =>
                    {
                        if (category == "password")
                        {
                            AppLog.Info("password", "Password prompt closed: connect cancelled.");
                        }

                        dialog.Hide();
                        tcs.TrySetResult(string.Empty);
                    });

                    onShown?.Invoke();
                    var result = await ShowWhenDialogSlotFreeAsync(dialog, category);
                    onDismissed?.Invoke(result);
                    if (result != ContentDialogResult.Primary)
                    {
                        tcs.TrySetResult(string.Empty);
                        return;
                    }

                    var value = readValue(input);
                    if (category == "password")
                    {
                        AppLog.Info(
                            "password",
                            value.Length == 0
                                ? "Password prompt submitted with an empty box."
                                : "Password captured from prompt.");
                    }

                    tcs.TrySetResult(value);
                }
                catch (Exception ex)
                {
                    if (category == "password")
                    {
                        AppLog.Error("password", $"Password prompt failed: {ex.GetType().Name}: {ex.Message}");
                    }

                    tcs.TrySetException(ex);
                }
            }))
        {
            if (category == "password")
            {
                AppLog.Warn("password", "Password prompt could not reach the UI thread.");
            }

            tcs.TrySetResult(string.Empty);
        }

        return tcs.Task;
    }

    private static async Task<ContentDialogResult> ShowWhenDialogSlotFreeAsync(
        ContentDialog dialog,
        string category)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await dialog.ShowAsync();
            }
            catch (COMException ex) when (attempt < DialogSlotAttempts)
            {
                AppLog.Warn(
                    category,
                    $"Dialog slot busy ({ex.GetType().Name}); retry {attempt} of {DialogSlotAttempts - 1}.");
                await Task.Delay(DialogSlotRetryDelayMs);
            }
        }
    }
}
