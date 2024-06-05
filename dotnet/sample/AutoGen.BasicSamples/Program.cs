// Copyright (c) Microsoft Corporation. All rights reserved.
// Program.cs

using AutoGen.OpenAI;

var openAIKey =
    Environment.GetEnvironmentVariable("OPENAI_API_KEY", EnvironmentVariableTarget.User)
    ?? throw new InvalidCastException("Please set OPENAI_API_KEY environment variable.");
var llmConfig = new OpenAIConfig(openAIKey, OpenAiModelId.Gpt3_5T);

await new Example04_Dynamic_GroupChat_Coding_Task(llmConfig).RunAsync();

internal static class OpenAiModelId
{
    public const string Gpt3_5T = "gpt-3.5-turbo";
    public const string Gpt4o_May13 = "gpt-4o-2024-05-13";
}
