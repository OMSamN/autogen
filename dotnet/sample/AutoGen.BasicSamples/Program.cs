// Copyright (c) Microsoft Corporation. All rights reserved.
// Program.cs

using AutoGen.OpenAI;

var openAIKey =
    Environment.GetEnvironmentVariable("OPENAI_API_KEY", EnvironmentVariableTarget.User)
    ?? throw new InvalidCastException("Please set OPENAI_API_KEY environment variable.");
var llmConfig = new OpenAIConfig(openAIKey, "gpt-3.5-turbo");

await new Example04_Dynamic_GroupChat_Coding_Task(llmConfig).RunAsync();
