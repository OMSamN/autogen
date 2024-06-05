// Copyright (c) Microsoft Corporation. All rights reserved.
// Example04_Dynamic_GroupChat_Coding_Task.cs

using System.Text;
using AutoGen;
using AutoGen.Core;
using AutoGen.DotnetInteractive;
using AutoGen.OpenAI;
using FluentAssertions;

public partial class Example04_Dynamic_GroupChat_Coding_Task
{
    private readonly ILLMConfig _llmConfig;

    public Example04_Dynamic_GroupChat_Coding_Task(ILLMConfig llmConfig)
    {
        _llmConfig = llmConfig ?? throw new ArgumentNullException(nameof(llmConfig));
    }

    public async Task RunAsync()
    {
        // setup dotnet interactive
        var workDir = Path.Combine(Path.GetTempPath(), "InteractiveService");
        if (!Directory.Exists(workDir))
        {
            Directory.CreateDirectory(workDir);
        }

        using var service = new InteractiveService(workDir);
        var dotnetInteractiveFunctions = new DotnetInteractiveFunction(service);

        var result = new FileInfo(Path.Combine(workDir, "result.txt"));
        if (result.Exists)
        {
            result.Delete();
        }

        await service.StartAsync(workDir, default);

        var writeConversationToConsole = true;
        var userProxy = new UserProxyAgent(
            name: "user",
            defaultReply: GroupChatExtension.TERMINATE,
            humanInputMode: HumanInputMode.AUTO,
            determinePrompt: (IEnumerable<IMessage> messages) =>
                writeConversationToConsole
                    ? "Please give feedback: Press enter or type 'exit' to stop the conversation."
                    : $"{messages?.Last().FormatMessage()}{Environment.NewLine}Please give feedback: Press enter or type 'exit' to stop the conversation.");

        GroupChatManager? groupChatManager = null;

        var groupAdmin = new GPTAgent(
            name: "groupAdmin",
            systemMessage: "You are group admin, terminate the group chat once task is completed by saying [TERMINATE] plus the final answer",
            temperature: 0f,
            config: _llmConfig)
            .RegisterMiddleware(
                async (msgs, option, agent, ct) =>
                {
                    var reply = await agent.GenerateReplyAsync(msgs, option, ct);
                    if (
                        reply is TextMessage textMessage
                        && textMessage.Content.Contains("TERMINATE") is true)
                    {
                        var userResponse = await userProxy.SendAsync(
                            $"{userProxy.Name} please type 'exit' if you are satisfied with the answer.",
                            msgs,
                            writeConversationToConsole,
                            ct);
                        if (!userResponse.IsGroupChatTerminateMessage() && groupChatManager != null)
                        {
                            msgs = await groupChatManager.SendAsync(
                                userProxy,
                                chatHistory: msgs.Concat([userResponse]),
                                maxRound: 11,
                                writeConversationToConsole: writeConversationToConsole);
                        }

                        return new TextMessage(
                            Role.Assistant,
                            $"{textMessage.Content}{Environment.NewLine}{Environment.NewLine}{GroupChatExtension.TERMINATE}",
                            from: reply.From);
                    }

                    return reply;
                });

        var coder = CreateCoderAgentAsync();
        var runner = CreateRunnerAgentAsync(service, coder);

        groupChatManager = SetupWorkflowAndGroupChat(writeConversationToConsole, _llmConfig, groupAdmin, userProxy, coder, runner);

        // task 1: Add 5 and 7
        IEnumerable<IMessage>? conversationHistory = null;
        conversationHistory = await userProxy.InitiateChatAsync(
            groupChatManager,
            $"What's 5 + 7 =",
            maxRound: 11,
            writeConversationToConsole: writeConversationToConsole);
        if (!conversationHistory.Last().IsGroupChatTerminateMessage())
        {
            conversationHistory = await SeekUserInputToRetry(userProxy, groupChatManager, conversationHistory, writeConversationToConsole);
        }
        conversationHistory.Last().IsGroupChatTerminateMessage().Should().BeTrue();

        // task 2: retrieve the most recent PR from mlnet and save it in result.txt
        var openAIKey =
            Environment.GetEnvironmentVariable("OPENAI_API_KEY", EnvironmentVariableTarget.User)
            ?? throw new InvalidCastException("Please set OPENAI_API_KEY environment variable.");
        var llmConfig = new OpenAIConfig(openAIKey, OpenAiModelId.Gpt4o_May13);
        groupChatManager = SetupWorkflowAndGroupChat(writeConversationToConsole, llmConfig, groupAdmin, userProxy, coder, runner);

        conversationHistory = await userProxy.InitiateChatAsync(
            groupChatManager,
            $"Retrieve the most recent PR from mlnet and save it in {result.Name}",
            maxRound: 30,
            writeConversationToConsole: writeConversationToConsole);

        await RetryIfFileDoesNotExist(
            result,
            userProxy,
            groupChatManager,
            $"I couldn't find {result.Name}.  Did {runner.Name} execute the code successfully?",
            conversationHistory,
            writeConversationToConsole,
            remainingRetries: 3);
        result.Exists.Should().BeTrue();

        // clear the result file
        result.Delete();

        // task 3: calculate the 39th fibonacci number
        int answer = 63245986;

        conversationHistory = await userProxy.InitiateChatAsync(
            groupChatManager,
            $"What's the 39th of fibonacci number? Save the result in {result.Name}",
            maxRound: 10,
            writeConversationToConsole: writeConversationToConsole
        );
        await RetryIfFileDoesNotExist(
            result,
            userProxy,
            groupChatManager,
            $"I couldn't find {result.Name}.  Did {runner.Name} execute the code successfully?",
            conversationHistory,
            writeConversationToConsole,
            remainingRetries: 3
        );
        result.Exists.Should().BeTrue();

        string resultContent = File.ReadAllText(result.FullName);
        resultContent.Should().Contain(answer.ToString());

        // clear the result file
        result.Delete();
    }

    private static async Task<IEnumerable<IMessage>> SeekUserInputToRetry(
        IAgent userProxy,
        GroupChatManager? groupChatManager,
        IEnumerable<IMessage> conversationHistory,
        bool writeConversationToConsole)
    {
        var userResponse = await userProxy.SendAsync(
            $"{userProxy.Name} please type 'exit' if you are satisfied with the answer.",
            conversationHistory,
            writeConversationToConsole);

        conversationHistory = conversationHistory.Concat([userResponse]);

        if (!userResponse.IsGroupChatTerminateMessage() && groupChatManager != null)
        {
            conversationHistory = await groupChatManager.SendAsync(
                userProxy,
                chatHistory: conversationHistory,
                maxRound: 11,
                writeConversationToConsole: writeConversationToConsole);
        }

        return conversationHistory;
    }

    private static async Task RetryIfFileDoesNotExist(
        FileInfo result,
        IAgent userProxy,
        GroupChatManager? groupChatManager,
        string messageToGroupChat,
        IEnumerable<IMessage> conversationHistory,
        bool writeConversationToConsole,
        int remainingRetries = 3)
    {
        do
        {
            result.Refresh();
            if (!result.Exists)
            {
                conversationHistory = await userProxy.SendAsync(
                    groupChatManager,
                    messageToGroupChat,
                    chatHistory: conversationHistory,
                    maxRound: 30,
                    writeConversationToConsole: writeConversationToConsole);
                result.Refresh();
            }
        } while (!result.Exists && --remainingRetries > 0);
    }

    private GroupChatManager SetupWorkflowAndGroupChat(
        bool writeConversationToConsole,
        ILLMConfig defaultAgentConfig,
        IAgent groupAdmin,
        IAgent user,
        IAgent coder,
        IAgent runner)
    {
        var admin = CreateAdminAsync(defaultAgentConfig);
        var reviewApprovedMessage =
            $"The code looks good, please ask {runner.Name} to run the code for you.";
        string reviewRejectedMessage = "There are some code review comments, please fix these:";
        var reviewer = CreateReviewerAgentAsync(defaultAgentConfig, reviewApprovedMessage, reviewRejectedMessage);

        var adminToCoderTransition = Transition.Create(admin, coder);
        var adminToRunnerTransition = Transition.Create(admin, runner);
        var coderToReviewerTransition = Transition.Create(coder, reviewer);
        var coderToCoderTransition = Transition.Create(
            coder,
            coder,
            canTransitionAsync: (from, to, messages) =>
            {
                // the last message should be be a system message from nobody
                if (
                    messages.Last() is TextMessage tm
                    && tm.Role == Role.System
                    && string.IsNullOrEmpty(tm.From)
                )
                {
                    return Task.FromResult(true);
                }

                return Task.FromResult(false);
            }
        );
        var reviewerToRunnerTransition = Transition.Create(
            reviewer,
            runner,
            canTransitionAsync: (from, to, messages) =>
            {
                // the last message should be the reviewed message.
                var lastMessage = messages.Last();
                if (
                    lastMessage is TextMessage textMessage
                    && textMessage.Content.Contains(
                        reviewApprovedMessage,
                        StringComparison.CurrentCultureIgnoreCase
                    )
                        is true
                )
                {
                    // ask runner to run the code
                    return Task.FromResult(true);
                }

                return Task.FromResult(false);
            }
        );

        var reviewerToCoderTransition = Transition.Create(
            reviewer,
            coder,
            canTransitionAsync: (from, to, messages) =>
            {
                // the last message should be a review failed message
                var lastMessage = messages.Last();
                if (
                    lastMessage is TextMessage textMessage
                    && textMessage.Content.Contains(
                        reviewRejectedMessage,
                        StringComparison.CurrentCultureIgnoreCase
                    )
                        is true
                )
                {
                    // ask coder to fix the error
                    return Task.FromResult(true);
                }

                return Task.FromResult(false);
            }
        );

        var runnerToCoderTransition = Transition.Create(
            from: runner,
            to: coder,
            canTransitionAsync: async (from, to, messages) =>
            {
                var lastMessage = messages.Last();
                if (
                    lastMessage is TextMessage textMessage
                    && textMessage.Content.Contains(
                        "error",
                        StringComparison.CurrentCultureIgnoreCase
                    )
                        is true
                )
                {
                    // ask coder to fix the error
                    return true;
                }

                return false;
            }
        );

        var runnerToAdminTransition = Transition.Create(runner, admin);

        var adminToUserTransition = Transition.Create(
            admin,
            user,
            (from, to, messages) =>
            {
                // the last message should be from admin
                var lastMessage = messages.Last();
                if (lastMessage.From != admin.Name)
                {
                    return Task.FromResult(false);
                }

                return Task.FromResult(true);
            }
        );

        var userToAdminTransition = Transition.Create(user, admin);

        var workflow = new Graph(
            [
                adminToCoderTransition,
                adminToRunnerTransition,
                coderToCoderTransition,
                coderToReviewerTransition,
                reviewerToRunnerTransition,
                reviewerToCoderTransition,
                runnerToCoderTransition,
                runnerToAdminTransition,
                adminToUserTransition,
                userToAdminTransition,
            ]
        );

        // create group chat
        var groupChat = new GroupChat(
            admin: groupAdmin,
            members: [admin, coder, runner, reviewer, user],
            workflow: workflow,
            writeConversationToConsole: writeConversationToConsole);
        admin.SendIntroduction("Welcome to my group. Work together to resolve my task.", groupChat);
        coder.SendIntroduction("I will write C# code to resolve the task.", groupChat);
        reviewer.SendIntroduction("I will review C# code.", groupChat);
        runner.SendIntroduction("I will run C# code once the review is approved.", groupChat);

        return new GroupChatManager(groupChat);
    }

    private IAgent CreateAdminAsync(ILLMConfig llmConfig)
    {
        // Create admin agent
        return new AssistantAgent(
            name: "admin",
            systemMessage: """
            You are a manager who takes coding problems from user and resolves the problems by splitting them into small tasks and assigning each task to the coder, who works in C#.

            The workflow is as follows:
            - You take the coding problem from user
            - You break the problem into small tasks. For each task you first ask coder to write code to resolve the task.
              - coder will pass the code to reviewer.
              - reviewer will pass failed reviews back to coder, and acceptable code to runner.
              - runner will pass failed executions to coder, and successful executions to you.
            - Once a small task is resolved, you summarize the completed steps and create the next step.
            - You repeat the above steps until the coding problem is resolved, and runner has executed the code without error.

            You can use the following JSON format to assign a task to an agent:
            ```task
            {
                "to": "{agent_name}",
                "task": "{a short description of the task}",
                "context": "{previous context e.g. code to execute, review comments, or execution feedback}"
            }
            ```

            If you need to ask user for extra information, you can use the following format:
            ```ask
            {
                "question": "{question}"
            }
            ```

            Once the coding problem is resolved, summarize each step and it's result, and send the summary to the user using the following format:
            ```summary
            {
                "problem": "{coding problem}",
                "steps": [
                    {
                        "step": "{step}",
                        "result": "{result}"
                    }
                ]
            }
            ```

            Whenever you see reviewer attempting to take on a coding task, reject the offer and assign the task to coder.

            Your reply must contain one of [task|ask|summary] to indicate the type of your message.
            """,
            llmConfig: new ConversableAgentConfig { Temperature = 0, ConfigList = [llmConfig], });
    }

    private IAgent CreateCoderAgentAsync()
    {
        // create coder agent
        // The coder agent is a composite agent that contains dotnet coder, code reviewer and NuGet agent.
        // The dotnet coder write dotnet code to resolve the task.
        // The code reviewer review the code block from coder's reply.
        // The NuGet agent install NuGet packages if there's any.
        return new GPTAgent(
            name: "coder",
            systemMessage: @"You act as C# .NET coder, you write C# code to resolve task. Once you finish writing code, ask runner to run the code for you.

Here're some rules to follow on writing C# code:
- put code between ```csharp and ```
- Avoid adding the `using` keyword when creating disposable objects. e.g `var httpClient = new HttpClient()`
- Try to use `var` instead of explicit type.
- Try avoid using external libraries, use .NET Core libraries instead.
- Use top-level statements to write code.
- Always additionally print out the result to console (if not already requested). Don't write code that doesn't print out anything.

If you need to install NuGet packages, put NuGet packages in the following format:
```nuget
nuget_package_name
```

If your code is incorrect, reviewer will tell you the problem. Fix the error(s) and send the code again.
If your code is fails to execute, runner will tell you the error message. Fix the error(s) and send the code again.

Here's some external information:
- The link to mlnet repo is: https://github.com/dotnet/machinelearning. You don't need a token to use GitHub PR API. Make sure to include a User-Agent header, otherwise GitHub will reject it.
",
            config: _llmConfig,
            temperature: 0.4f,
            functionMap: new Dictionary<FunctionContract, Func<string, Task<string>>>
            {
                { this.AddFunctionContract, this.AddWrapper }
            });
    }

    private static IAgent CreateRunnerAgentAsync(InteractiveService service, IAgent coder)
    {
        // create runner agent
        // The runner agent will run the code block from coder's reply.
        // It runs dotnet code using dotnet interactive service hook.
        // It also truncate the output if the output is too long.
        return new AssistantAgent(
            name: "runner",
            systemMessage: "You run .NET code",
            defaultReply: "No code available."
        )
            .RegisterDotnetCodeBlockExectionHook(interactiveService: service)
            .RegisterMiddleware(
                async (msgs, option, agent, ct) =>
                {
                    if (!msgs.Any() || msgs.All(msg => msg.From != coder.Name))
                    {
                        return new TextMessage(
                            Role.Assistant,
                            $"No code available. {coder.Name} please write code"
                        );
                    }
                    else
                    {
                        var coderMsg = msgs.Last(msg => msg.From == coder.Name);
                        return await agent.GenerateReplyAsync([coderMsg], option, ct);
                    }
                },
                middlewareName: "Identify code to execute");
    }

    private IAgent CreateReviewerAgentAsync(
        ILLMConfig llmConfig,
        string approvedMessage = "The code looks good, please ask runner to run the code for you.",
        string rejectedMessage = "There are some code review comments, please fix these:")
    {
        // code reviewer agent will review if code block from coder's reply satisfy the following conditions:
        // - There's only one code block
        // - The code block is csharp code block
        // - The code block uses top-level statements
        // - The code block is not using declaration
        return new GPTAgent(
            name: "reviewer",
            systemMessage: """
            You are a code reviewer who reviews code from coder. Your primary responsibility is to ensure that the code meets specific conditions and provide constructive feedback to help the coder improve. Please check if the code satisfies the following conditions:
            - The reply from coder contains at least one code block, e.g ```csharp and ```
            - There's only one code block and it is a C# (i.e. csharp) code block.
            - The code block is not inside a main function. It should use top-level statements.
            - The code block does not use a `using` declaration when creating disposable objects.
            - The code block is complete, and carries out the requested task without missing any details.
            Important: If the coder fails to provide a code block or does not meet the specified conditions, you must reject the code and provide clear feedback on what is missing or incorrect. Do not attempt to write or complete the code yourself.

            Remember, your role is to review the code, not to write or rewrite it. Focus on providing detailed feedback and suggestions based on the criteria above. Your constructive feedback helps the coder learn and improve, fostering a collaborative environment. By staying within your role and working closely with the coder, you contribute significantly to the project's success.

            You don't need to check the code style, only verify if the code satisfies the above conditions.

            Thank you for your dedication and commitment to ensuring the quality of our codebase.

            Put your comment between ```review and ```, if the code satisfies all conditions, put APPROVED in review.result field. Otherwise, put REJECTED along with comments. Ensure your comment is clear and easy to understand.

            ## Example 1 ##
            ```review
            comment: The code satisfies all conditions.
            result: APPROVED
            ```

            ## Example 2 ##
            ```review
            comment: The code is inside the main function. Please rewrite the code in top-level statements.
            result: REJECTED
            ```

            """,
            config: llmConfig,
            temperature: 0f,
            functionMap: new Dictionary<FunctionContract, Func<string, Task<string>>>()
            {
                { this.ReviewCodeBlockFunctionContract, this.ReviewCodeBlockWrapper },
            })
            .RegisterMiddleware(async (msgs, option, innerAgent, ct) =>
            {
                var maxRetry = 3;
                var reply = await innerAgent.GenerateReplyAsync(msgs, option, ct);
                while (maxRetry-- > 0)
                {
                    if (reply.GetToolCalls() is var toolCalls && toolCalls.Count == 1 && toolCalls[0].FunctionName == nameof(ReviewCodeBlock))
                    {
                        var toolCallResult = reply.GetContent();
                        var reviewResultObj = System.Text.Json.JsonSerializer.Deserialize<CodeReviewResult>(toolCallResult);
                        var reviews = new List<string>();
                        if (reviewResultObj.HasMultipleCodeBlocks)
                        {
                            var fixCodeBlockPrompt = @"There're multiple code blocks, please combine them into one code block.";
                            reviews.Add(fixCodeBlockPrompt);
                        }

                        if (reviewResultObj.IsDotnetCodeBlock is false)
                        {
                            var fixCodeBlockPrompt = @"The code block is not a csharp code block, please write C# code only.";
                            reviews.Add(fixCodeBlockPrompt);
                        }

                        if (reviewResultObj.IsTopLevelStatement is false)
                        {
                            var fixCodeBlockPrompt = @"The code is not using top-level statements, please rewrite your C# code using top-level statements.";
                            reviews.Add(fixCodeBlockPrompt);
                        }

                        if (reviewResultObj.IsPrintResultToConsole is false)
                        {
                            var fixCodeBlockPrompt = @"The code doesn't write anything to the console, please print the output to console.";
                            reviews.Add(fixCodeBlockPrompt);
                        }

                        if (reviews.Count > 0)
                        {
                            var sb = new StringBuilder();
                            sb.AppendLine(rejectedMessage);
                            foreach (var review in reviews)
                            {
                                sb.AppendLine($"- {review}");
                            }

                            return new TextMessage(Role.Assistant, sb.ToString(), from: innerAgent.Name);
                        }
                        else
                        {
                            var msg = new TextMessage(Role.Assistant, approvedMessage)
                            {
                                From = innerAgent.Name,
                            };

                            return msg;
                        }
                    }
                    else
                    {
                        var originalContent = reply.GetContent();
                        var prompt = $@"Please convert the content to {nameof(ReviewCodeBlock)} function arguments.

## Original Content
{originalContent}";

                        reply = await innerAgent.SendAsync(prompt, msgs, ct: ct);
                    }
                }

                throw new Exception("Failed to review code block");
            },
            middlewareName: "Summarise code review feedback");
    }

    /// <summary>
    /// A mathematical operation that returns the sum of two numbers.
    /// </summary>
    /// <param name="x">The first number to sum.</param>
    /// <param name="y">The second number to sum.</param>
    /// <returns>A string representation of the sum of <paramref name="x"/> and <paramref name="y"/>.</returns>
    [Function]
    public Task<string> Add(float x, float y) => Task.FromResult((x + y).ToString());

    public struct CodeReviewResult
    {
        public bool HasMultipleCodeBlocks { get; set; }
        public bool IsTopLevelStatement { get; set; }
        public bool IsDotnetCodeBlock { get; set; }
        public bool IsPrintResultToConsole { get; set; }
    }

    /// <summary>
    /// Review code block
    /// </summary>
    /// <param name="hasMultipleCodeBlocks">true if there're multiple C# code blocks.</param>
    /// <param name="isTopLevelStatement">true if the code is using top-level statements.</param>
    /// <param name="isDotnetCodeBlock">true if the code block is a C# code block.</param>
    /// <param name="isPrintResultToConsole">true if the code block prints out result to console</param>
    [Function]
    public async Task<string> ReviewCodeBlock(
        bool hasMultipleCodeBlocks,
        bool isTopLevelStatement,
        bool isDotnetCodeBlock,
        bool isPrintResultToConsole
    )
    {
        var obj = new CodeReviewResult
        {
            HasMultipleCodeBlocks = hasMultipleCodeBlocks,
            IsTopLevelStatement = isTopLevelStatement,
            IsDotnetCodeBlock = isDotnetCodeBlock,
            IsPrintResultToConsole = isPrintResultToConsole,
        };

        return System.Text.Json.JsonSerializer.Serialize(obj);
    }
}
