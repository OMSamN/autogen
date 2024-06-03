// Copyright (c) Microsoft Corporation. All rights reserved.
// Example04_Dynamic_GroupChat_Coding_Task.cs

using AutoGen;
using AutoGen.Core;
using AutoGen.DotnetInteractive;
using AutoGen.OpenAI;
using FluentAssertions;

public partial class Example04_Dynamic_GroupChat_Coding_Task
{
    public async Task RunAsync()
    {
        var instance = new Example04_Dynamic_GroupChat_Coding_Task();

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

        var openAIKey =
            Environment.GetEnvironmentVariable("OPENAI_API_KEY", EnvironmentVariableTarget.User)
            ?? throw new InvalidCastException("Please set OPENAI_API_KEY environment variable.");
        var _llmConfig = new OpenAIConfig(openAIKey, "gpt-3.5-turbo");

        var groupAdmin = new GPTAgent(
            name: "groupAdmin",
            systemMessage: "You are the admin of the group chat",
            temperature: 0f,
            config: _llmConfig
        ).RegisterPrintMessage();

        var userProxy = new UserProxyAgent(
            name: "user",
            defaultReply: GroupChatExtension.TERMINATE,
            humanInputMode: HumanInputMode.NEVER
        ).RegisterPrintMessage();

        // Create admin agent
        var admin = new AssistantAgent(
            name: "admin",
            systemMessage: """
            You are a manager who takes coding problems from user and resolve problems by splitting them into small tasks and assigning each task to the most appropriate agent.
            Here's available agents who you can assign task to:
            - coder: write C# code to resolve task
            - runner: run C# code from coder

            The workflow is as follows:
            - You take the coding problem from user
            - You break the problem into small tasks. For each task you first ask coder to write code to resolve the task. Once the code is written, you ask runner to run the code.
            - Once a small task is resolved, you summarize the completed steps and create the next step.
            - You repeat the above steps until the coding problem is resolved.

            You can use the following JSON format to assign a task to an agent:
            ```task
            {
                "to": "{agent_name}",
                "task": "{a short description of the task}",
                "context": "{previous context from scratchpad}"
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

            Your reply must contain one of [task|ask|summary] to indicate the type of your message.
            """,
            llmConfig: new ConversableAgentConfig
            {
                Temperature = 0,
                ConfigList = [_llmConfig],
                FunctionContracts = new[] { AddFunctionContract },
            }
        ).RegisterPrintMessage();

        // create coder agent
        // The coder agent is a composite agent that contains dotnet coder, code reviewer and nuget agent.
        // The dotnet coder write dotnet code to resolve the task.
        // The code reviewer review the code block from coder's reply.
        // The nuget agent install nuget packages if there's any.
        var coderAgent = new GPTAgent(
            name: "coder",
            systemMessage: @"You act as C# .NET coder, you write C# code to resolve task. Once you finish writing code, ask runner to run the code for you.

Here're some rules to follow on writing C# code:
- put code between ```csharp and ```
- When creating HttpClient, use `var httpClient = new HttpClient()`. Don't use `using var httpClient = new HttpClient()` because it will cause error when running the code.
- Try to use `var` instead of explicit type.
- Try avoid using external library, use .NET Core library instead.
- Use top level statement to write code.
- Always print out the result to console. Don't write code that doesn't print out anything.

If you need to install nuget packages, put nuget packages in the following format:
```nuget
nuget_package_name
```

If your code is incorrect, fix the error and send the code again.

Here's some external information:
- The link to mlnet repo is: https://github.com/dotnet/machinelearning. you don't need a token to use GitHub PR API. Make sure to include a User-Agent header, otherwise GitHub will reject it.
",
            config: _llmConfig,
            temperature: 0.4f,
            functions: new Azure.AI.OpenAI.FunctionDefinition[] { this.AddFunction },
            functionMap: new Dictionary<string, Func<string, Task<string>>>
            {
                { this.AddFunction.Name, this.AddWrapper }
            }
        ).RegisterPrintMessage();

        // code reviewer agent will review if code block from coder's reply satisfy the following conditions:
        // - There's only one code block
        // - The code block is csharp code block
        // - The code block is top level statement
        // - The code block is not using declaration
        var codeReviewAgent = new GPTAgent(
            name: "reviewer",
            systemMessage: """
            You are a code reviewer who reviews code from coder. You need to check if the code satisfies the following conditions:
            - The reply from coder contains at least one code block, e.g ```csharp and ```
            - There's only one code block and it's csharp code block
            - The code block is not inside a main function. a.k.a top level statement
            - The code block does not use a "using declaration" when creating an HttpClient.

            You don't check the code style, only check if the code satisfies the above conditions.

            Put your comment between ```review and ```, if the code satisfies all conditions, put APPROVED in review.result field. Otherwise, put REJECTED along with comments. Ensure your comment is clear and easy to understand.

            ## Example 1 ##
            ```review
            comment: The code satisfies all conditions.
            result: APPROVED
            ```

            ## Example 2 ##
            ```review
            comment: The code is inside main function. Please rewrite the code in top level statement.
            result: REJECTED
            ```

            """,
            config: _llmConfig,
            temperature: 0f
        ).RegisterPrintMessage();

        // create runner agent
        // The runner agent will run the code block from coder's reply.
        // It runs dotnet code using dotnet interactive service hook.
        // It also truncate the output if the output is too long.
        var runner = new AssistantAgent(
            name: "runner",
            defaultReply: "No code available, coder, write code please"
        )
            .RegisterDotnetCodeBlockExectionHook(interactiveService: service)
            .RegisterMiddleware(
                async (msgs, option, agent, ct) =>
                {
                    var mostRecentCoderMessage =
                        msgs.LastOrDefault(x => x.From == "coder")
                        ?? throw new Exception("No coder message found");
                    return await agent.GenerateReplyAsync(
                        new[] { mostRecentCoderMessage },
                        option,
                        ct
                    );
                }
            )
            .RegisterPrintMessage();

        var adminToCoderTransition = Transition.Create(
            admin,
            coderAgent,
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
        var coderToReviewerTransition = Transition.Create(coderAgent, codeReviewAgent);
        var adminToRunnerTransition = Transition.Create(
            admin,
            runner,
            (from, to, messages) =>
            {
                // the last message should be from admin
                var lastMessage = messages.Last();
                if (lastMessage.From != admin.Name)
                {
                    return Task.FromResult(false);
                }

                // the previous messages should contain a message from coder
                var coderMessage = messages.FirstOrDefault(x => x.From == coderAgent.Name);
                if (coderMessage is null)
                {
                    return Task.FromResult(false);
                }

                return Task.FromResult(true);
            }
        );

        var runnerToAdminTransition = Transition.Create(runner, admin);

        var reviewerToAdminTransition = Transition.Create(codeReviewAgent, admin);

        var adminToUserTransition = Transition.Create(
            admin,
            userProxy,
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

        var userToAdminTransition = Transition.Create(userProxy, admin);

        var workflow = new Graph(
            [
                adminToCoderTransition,
                coderToReviewerTransition,
                reviewerToAdminTransition,
                adminToRunnerTransition,
                runnerToAdminTransition,
                adminToUserTransition,
                userToAdminTransition,
            ]
        );

        // create group chat
        var groupChat = new GroupChat(
            admin: groupAdmin,
            members: [admin, coderAgent, runner, codeReviewAgent, userProxy],
            workflow: workflow
        );
        var groupChatManager = new GroupChatManager(groupChat);

        // task 1: Add 5 and 7
        var conversationHistory = await userProxy.InitiateChatAsync(
            groupChatManager,
            $"What's 5 + 7 =",
            maxRound: 10
        );

        // task 2: retrieve the most recent pr from mlnet and save it in result.txt
        conversationHistory = await userProxy.SendAsync(
            groupChatManager,
            $"Retrieve the most recent PR from mlnet and save it in {result.Name}",
            maxRound: 30
        );

        result.Refresh();
        result.Exists.Should().BeTrue();

        // task 3: calculate the 39th fibonacci number
        var answer = 63245986;
        // clear the result file
        result.Delete();

        conversationHistory = await userProxy.InitiateChatAsync(
            groupChatManager,
            $"What's the 39th of fibonacci number? Save the result in {result.Name}",
            maxRound: 10
        );
        result.Refresh();
        result.Exists.Should().BeTrue();
        var resultContent = File.ReadAllText(result.FullName);
        resultContent.Should().Contain(answer.ToString());
    }

    /// <summary>
    /// A mathematical operation that returns the sum of two numbers.
    /// </summary>
    /// <param name="x">The first number to sum.</param>
    /// <param name="y">The second number to sum.</param>
    /// <returns>A string representation of the sum of <paramref name="x"/> and <paramref name="y"/>.</returns>
    [Function]
    public Task<string> Add(float x, float y) => Task.FromResult((x + y).ToString());
}
