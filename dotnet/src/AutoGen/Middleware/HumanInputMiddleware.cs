// Copyright (c) Microsoft Corporation. All rights reserved.
// HumanInputMiddleware.cs

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AutoGen;

/// <summary>
/// the middleware to get human input
/// </summary>
public class HumanInputMiddleware : IMiddleware
{
    private readonly HumanInputMode mode;
    private readonly Func<IEnumerable<IMessage>, string> determinePrompt;
    private readonly string exitKeyword;
    private Func<IEnumerable<IMessage>, CancellationToken, Task<bool>> isTermination;
    private Func<string> getInput = Console.ReadLine;
    private Action<string> writeLine = Console.WriteLine;
    public string? Name => nameof(HumanInputMiddleware);

    public HumanInputMiddleware(
        Func<IEnumerable<IMessage>, string>? determinePrompt = null,
        string exitKeyword = "exit",
        HumanInputMode mode = HumanInputMode.AUTO,
        Func<IEnumerable<IMessage>, CancellationToken, Task<bool>>? isTermination = null,
        Func<string>? getInput = null,
        Action<string>? writeLine = null
    )
    {
        this.determinePrompt = determinePrompt
            ?? ((IEnumerable<IMessage> messages) => "Please give feedback: Press enter or type 'exit' to stop the conversation.");
        this.isTermination = isTermination ?? DefaultIsTermination;
        this.exitKeyword = exitKeyword;
        this.mode = mode;
        this.getInput = getInput ?? GetInput;
        this.writeLine = writeLine ?? WriteLine;
    }

    public async Task<IMessage> InvokeAsync(MiddlewareContext context, IAgent agent, CancellationToken cancellationToken = default)
    {
        // if the mode is never, or auto, but message was a termination message, then just return the input message
        if (mode == HumanInputMode.NEVER
            || mode == HumanInputMode.AUTO && await isTermination(context.Messages, cancellationToken) is true)
        {
            return await agent.GenerateReplyAsync(context.Messages, context.Options, cancellationToken);
        }

        // if the mode is always, or auto but message was not a termination message, then prompt the user for input
        this.writeLine(determinePrompt(context.Messages));
        var input = getInput();
        if (input == exitKeyword)
        {
            return new TextMessage(Role.Assistant, GroupChatExtension.TERMINATE, agent.Name);
        }

        return new TextMessage(Role.Assistant, input, agent.Name);
    }

    private async Task<bool> DefaultIsTermination(IEnumerable<IMessage> messages, CancellationToken _)
    {
        return messages?.Last().IsGroupChatTerminateMessage() is true;
    }

    private string GetInput()
    {
        return Console.ReadLine();
    }

    private void WriteLine(string message)
    {
        Console.Out.WriteColouredLine(ConsoleColor.White, message);
    }
}
