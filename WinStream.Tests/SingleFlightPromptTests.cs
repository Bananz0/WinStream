using WinStream.Core.Streaming;

namespace WinStream.Tests;

public class SingleFlightPromptTests
{
    [Fact]
    public void A_second_call_for_the_same_receiver_joins_the_in_flight_task()
    {
        var prompt = new SingleFlightPrompt();
        var started = new TaskCompletionSource<string>();

        var first = prompt.JoinOrStart("receiver-a", "password", () => started.Task);
        var second = prompt.JoinOrStart(
            "receiver-a",
            "password",
            () => throw new InvalidOperationException("A joining caller must not start a second prompt."));

        Assert.Same(first, second);
    }

    [Fact]
    public async Task A_second_waiter_for_a_different_receiver_waits_for_the_first_answer()
    {
        var prompt = new SingleFlightPrompt();
        var started = new TaskCompletionSource<string>();

        var first = prompt.JoinOrStart("receiver-a", "password", () => started.Task);
        var secondTask = prompt.JoinOrStart("receiver-b", "password", () => Task.FromResult("wrong"));
        started.SetResult("for-a");

        Assert.Equal("for-a", await first);
        Assert.Equal("wrong", await secondTask);
    }

    [Fact]
    public async Task A_second_waiter_observes_the_first_callers_result()
    {
        var prompt = new SingleFlightPrompt();
        var started = new TaskCompletionSource<string>();

        var first = prompt.JoinOrStart("receiver-a", "password", () => started.Task);
        var second = prompt.JoinOrStart("receiver-a", "password", () => Task.FromResult("unused"));
        started.SetResult("hunter2");

        Assert.Equal("hunter2", await first);
        Assert.Equal("hunter2", await second);
    }

    [Fact]
    public async Task Once_the_first_prompt_completes_a_new_call_starts_a_fresh_one()
    {
        var prompt = new SingleFlightPrompt();

        var first = await prompt.JoinOrStart("receiver-a", "password", () => Task.FromResult("first"));
        var startedSecond = false;
        var second = await prompt.JoinOrStart("receiver-a", "password", () =>
        {
            startedSecond = true;
            return Task.FromResult("second");
        });

        Assert.Equal("first", first);
        Assert.True(startedSecond);
        Assert.Equal("second", second);
    }

    [Fact]
    public async Task A_faulted_prompt_frees_the_slot_for_the_next_caller()
    {
        var prompt = new SingleFlightPrompt();
        var faulting = new TaskCompletionSource<string>();

        var first = prompt.JoinOrStart("receiver-a", "password", () => faulting.Task);
        faulting.SetException(new InvalidOperationException("boom"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => first);

        var recovered = await prompt.JoinOrStart("receiver-a", "password", () => Task.FromResult("ok"));

        Assert.Equal("ok", recovered);
    }
}
